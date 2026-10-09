using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;

namespace SemanticOverlay.NativeHost
{
    internal static class MessageReaderSafetyTest
    {
        static int Main()
        {
            using (var image = new Bitmap(600, 250))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.FromArgb(244, 244, 244));
                using (var brush = new SolidBrush(Color.FromArgb(157, 242, 159)))
                {
                    graphics.FillRectangle(brush, new Rectangle(80, 70, 450, 60));
                    graphics.FillRectangle(brush, new Rectangle(180, 155, 350, 60));
                }
                // Closed glyph with a disconnected green center at the click.
                graphics.FillRectangle(Brushes.Black, new Rectangle(280, 86, 20, 26));
                using (var brush = new SolidBrush(Color.FromArgb(157, 242, 159)))
                    graphics.FillRectangle(brush, new Rectangle(286, 92, 8, 14));
                Rectangle bounds;
                if (!MessageBubbleDetector.FindInBitmap(image, new Point(290, 99), out bounds) ||
                    bounds != new Rectangle(80, 70, 450, 60)) return 6;
                if (!MessageBubbleDetector.FindInBitmap(image, new Point(290, 122), out bounds) ||
                    bounds.Bottom > 130) return 7;
            }
            Console.WriteLine("bubble-glyph-island-and-adjacent-message-isolation-ok");
            if (MessageTextReader.Normalize("第一行\r\n第二行  RAG") != "第一行\n第二行 RAG") return 11;
            var oneBubble = new Rectangle(100, 100, 250, 70);
            if (!MessageTextReader.FitsBubble(new Rectangle(110, 110, 200, 40), oneBubble) ||
                MessageTextReader.FitsBubble(new Rectangle(100, 50, 250, 120), oneBubble)) return 12;
            if (MessageTextReader.IsCandidate(new string('a', 1001), "text", oneBubble,
                new Rectangle(0, 0, 800, 800), new Point(120, 120), 1000) ||
                !MessageTextReader.IsCandidate(new string('a', 1000), "text", oneBubble,
                new Rectangle(0, 0, 800, 800), new Point(120, 120), 1000)) return 13;
            Console.WriteLine("source-paragraph-length-and-accessibility-neighbor-boundary-ok");
            var splitGlyph = new List<OcrWord> {
                new OcrWord { text = "另", x = 1082, y = 32, w = 21, h = 38 },
                new OcrWord { text = "刂", x = 1107, y = 30, w = 13, h = 40 },
                new OcrWord { text = "忘", x = 1122, y = 30, w = 41, h = 40 } };
            var repairedGlyph = ServiceManager.RepairSplitOcrGlyphs(splitGlyph);
            if (repairedGlyph.Count != 2 || repairedGlyph[0].text != "别" ||
                repairedGlyph[0].w != 38 || splitGlyph[0].text != "另") return 8;
            splitGlyph[0].w = 40; splitGlyph[1].x = 1125; splitGlyph[1].w = 40;
            if (ServiceManager.RepairSplitOcrGlyphs(splitGlyph).Count != 3) return 9;
            splitGlyph[0].w = 21; splitGlyph[1].x = 1107; splitGlyph[1].w = 13;
            splitGlyph[1].y = 80;
            if (ServiceManager.RepairSplitOcrGlyphs(splitGlyph).Count != 3) return 10;
            Console.WriteLine("ocr-split-glyph-geometry-and-source-preservation-ok");
            var automation = Assembly.LoadFrom(System.IO.Path.Combine(
                RuntimeEnvironment.GetRuntimeDirectory(), "WPF", "UIAutomationClient.dll"));
            var walkerType = automation.GetType("System.Windows.Automation.TreeWalker");
            var elementType = automation.GetType("System.Windows.Automation.AutomationElement");
            if (MessageTextReader.ResolveControlViewWalker(walkerType) == null ||
                walkerType.GetMethod("GetParent", new Type[] { elementType }) == null) return 1;

            using (var release = new ManualResetEvent(false))
            using (var finished = new ManualResetEvent(false))
            {
                var watch = Stopwatch.StartNew();
                var blocked = MessageTextReader.ReadBounded(delegate {
                    release.WaitOne(); finished.Set(); return new MessageProbeResult { Text = "late" };
                }, 30);
                if (blocked != null || watch.ElapsedMilliseconds > 500) return 2;
                int secondCalls = 0;
                var second = MessageTextReader.ReadBounded(delegate {
                    secondCalls++; return new MessageProbeResult { Text = "unexpected" };
                }, 30);
                if (second != null || secondCalls != 0) return 3;
                release.Set();
                if (!finished.WaitOne(1000)) return 4;
                MessageProbeResult next = null;
                for (int attempt = 0; attempt < 50 && next == null; attempt++)
                {
                    Thread.Sleep(10);
                    next = MessageTextReader.ReadBounded(delegate {
                        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                            throw new Exception("Reader is not isolated in STA");
                        return new MessageProbeResult { Text = "fresh" };
                    }, 100);
                }
                if (next == null || next.Text != "fresh") return 5;
            }
            Console.WriteLine("message-reader-field-overload-timeout-single-worker-and-recovery-ok");
            return 0;
        }
    }
}
