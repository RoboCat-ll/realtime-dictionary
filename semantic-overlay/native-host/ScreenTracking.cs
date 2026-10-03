using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal sealed class ScrollProbeResult
    {
        internal ScrollFrame Next;
        internal bool Confident;
        internal int Delta;
    }

    // Bounded vertical translation tracker. It measures pixels, never wheel units.
    internal sealed class ScrollFrame
    {
        internal const int Columns = 144;
        internal readonly byte[] Pixels;
        internal readonly int Height;
        internal ScrollFrame(byte[] pixels, int height) { Pixels = pixels; Height = height; }
        internal static ScrollFrame Capture(NativeRect rect)
        {
            // One broad copy keeps left/right chat bubbles in view. Matching below
            // evaluates three lanes without paying for three CopyFromScreen calls.
            int sourceWidth = Math.Min(rect.Width, Math.Min(1200, Math.Max(480, rect.Width * 3 / 4)));
            int sourceX = rect.Left + (rect.Width - sourceWidth) / 2;
            using (Bitmap strip = new Bitmap(sourceWidth, rect.Height, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(strip))
            {
                g.CopyFromScreen(sourceX, rect.Top, 0, 0,
                    new Size(sourceWidth, rect.Height), CopyPixelOperation.SourceCopy);
                return FromBitmap(strip);
            }
        }
        internal static ScrollFrame FromBitmap(Bitmap full)
        {
            using (Bitmap small = new Bitmap(Columns, full.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.Low;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g.DrawImage(full, 0, 0, Columns, full.Height);
                }
                return FromSample(small);
            }
        }
        private static ScrollFrame FromSample(Bitmap small)
        {
                BitmapData bits = small.LockBits(new Rectangle(0, 0, small.Width, small.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] raw = new byte[bits.Stride * small.Height];
                    Marshal.Copy(bits.Scan0, raw, 0, raw.Length);
                    byte[] pixels = new byte[Columns * small.Height];
                    for (int y = 0; y < small.Height; y++)
                        for (int x = 0; x < Columns; x++)
                        {
                            int p = y * bits.Stride + x * 4;
                            pixels[y * Columns + x] = (byte)((raw[p] * 11 + raw[p + 1] * 59 + raw[p + 2] * 30) / 100);
                        }
                    return new ScrollFrame(pixels, small.Height);
                }
                finally { small.UnlockBits(bits); }
        }
        internal bool TryDisplacement(ScrollFrame next, out int delta)
        {
            delta = 0;
            if (next == null || next.Height != Height) return false;
            // Identical repetitive text is stationary, not an ambiguous scroll.
            bool same = true;
            for (int i = 0; i < Pixels.Length; i++)
                if (Pixels[i] != next.Pixels[i]) { same = false; break; }
            if (same) return true;
            List<MotionVote> votes = new List<MotionVote>();
            MotionVote vote;
            if (TryRange(next, 2, Columns - 2, out vote)) votes.Add(vote);
            int lane = Columns / 3;
            if (TryRange(next, 2, lane + 8, out vote)) votes.Add(vote);
            if (TryRange(next, lane - 8, lane * 2 + 8, out vote)) votes.Add(vote);
            if (TryRange(next, lane * 2 - 8, Columns - 2, out vote)) votes.Add(vote);
            if (votes.Count < 2) return false;

            int bestCount = 0;
            double bestConfidence = Double.MinValue;
            int bestDelta = 0;
            foreach (MotionVote seed in votes)
            {
                int count = 0;
                double confidence = 0;
                double weightedDelta = 0;
                foreach (MotionVote candidate in votes)
                {
                    if (Math.Abs(candidate.Delta - seed.Delta) > 2) continue;
                    count++;
                    confidence += candidate.Confidence;
                    weightedDelta += candidate.Delta * candidate.Confidence;
                }
                if (count > bestCount || (count == bestCount && confidence > bestConfidence))
                {
                    bestCount = count;
                    bestConfidence = confidence;
                    bestDelta = (int)Math.Round(weightedDelta / Math.Max(0.01, confidence));
                }
            }
            if (bestCount < 2) return false;
            delta = bestDelta;
            return true;
        }

        private bool TryRange(ScrollFrame next, int xStart, int xEnd, out MotionVote vote)
        {
            vote = new MotionVote();
            List<int> anchors = new List<int>();
            xStart = Math.Max(2, xStart);
            xEnd = Math.Min(Columns - 2, xEnd);
            for (int y = 6; y < Height - 6; y += 3)
                for (int x = xStart; x < xEnd; x += 2)
                {
                    int p = y * Columns + x;
                    if (Math.Abs(Pixels[p] - Pixels[p - 1]) > 22) anchors.Add(p);
                }
            if (anchors.Count < 24) return false;
            int step = Math.Max(1, anchors.Count / 900);
            int range = Math.Min(240, Height / 3);
            double best = Double.MaxValue;
            int bestDelta = 0;
            double[] scores = new double[range * 2 + 1];
            for (int shift = -range; shift <= range; shift++)
            {
                long difference = 0; int count = 0;
                for (int i = 0; i < anchors.Count; i += step)
                {
                    int p = anchors[i], q = p + shift * Columns;
                    if (q < Columns || q >= next.Pixels.Length - Columns) continue;
                    difference += Math.Abs(Pixels[p] - next.Pixels[q]); count++;
                }
                double score = count < Math.Max(30, anchors.Count / step / 2)
                    ? Double.MaxValue : (double)difference / count;
                scores[shift + range] = score;
                if (score < best) { best = score; bestDelta = shift; }
            }
            if (best > 18) return false;
            double runner = Double.MaxValue;
            for (int shift = -range; shift <= range; shift++)
                if (Math.Abs(shift - bestDelta) > 3)
                    runner = Math.Min(runner, scores[shift + range]);
            if (runner - best < 3) return false;
            vote.Delta = bestDelta;
            vote.Confidence = Math.Min(20, runner - best) * Math.Min(2.0, anchors.Count / 120.0);
            return true;
        }

        private sealed class MotionVote
        {
            internal int Delta;
            internal double Confidence;
        }
    }

}
