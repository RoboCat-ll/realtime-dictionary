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
    internal sealed partial class OverlayContext : ApplicationContext
    {
        private void UpdateCaptionFromScan(
            int generation,
            IntPtr capturedWindow,
            NativeRect captureRect,
            List<OcrWord> words,
            int scanOffsetX,
            int scanOffsetY)
        {
            List<OcrWord> selectedCaptionWords;
            string text = BuildCaptionText(words, out selectedCaptionWords);
            if (String.IsNullOrWhiteSpace(text))
                return;
            DateTime now = DateTime.UtcNow;
            if (!String.Equals(text, pendingCaptionText, StringComparison.Ordinal))
            {
                bool variant = AreCaptionVariants(pendingCaptionText, text);
                if (!String.IsNullOrWhiteSpace(pendingCaptionText))
                {
                    if (!variant && !pendingCaptionCommitted)
                        CommitPendingCaption(false);
                    else if (variant && pendingCaptionCommitted && captionHistory.Count > 0 &&
                        String.Equals(captionHistory[captionHistory.Count - 1].text,
                            pendingCaptionText, StringComparison.Ordinal))
                    {
                        captionHistory[captionHistory.Count - 1].text = text;
                        SaveCaptionEntry(captionHistory[captionHistory.Count - 1], true);
                        captionHistoryWindow.RefreshArchiveDate();
                    }
                }
                pendingCaptionText = text;
                pendingCaptionCommitted = false;
                pendingCaptionChangedUtc = now;
            }
            pendingCaptionWords = selectedCaptionWords;
            pendingCaptionGeneration = generation;
            pendingCaptionWindow = capturedWindow;
            pendingCaptionCaptureRect = captureRect;
            pendingCaptionOffsetX = scanOffsetX;
            pendingCaptionOffsetY = scanOffsetY;
            if (selectedCaptionWords.Count > 0)
            {
                double sourceTop = Double.MaxValue;
                foreach (OcrWord word in selectedCaptionWords) sourceTop = Math.Min(sourceTop, word.y);
                captionLyricWindow.SetSourceTop(scanOffsetY + (int)sourceTop);
            }
            captionLyricWindow.ShowLines(PreviousCaptionText(), pendingCaptionText, targetRect);
        }

        private void CommitPendingCaption(bool allowModel)
        {
            if (String.IsNullOrWhiteSpace(pendingCaptionText) || pendingCaptionCommitted)
                return;
            if (captionHistory.Count == 0 || !String.Equals(
                    captionHistory[captionHistory.Count - 1].text,
                    pendingCaptionText,
                    StringComparison.Ordinal))
            {
                CaptionEntry entry = new CaptionEntry {
                    timestamp = DateTime.Now,
                    text = pendingCaptionText
                };
                SaveCaptionEntry(entry, false);
                captionHistory.Add(entry);
                if (captionHistory.Count > CaptionHistoryLimit)
                    captionHistory.RemoveAt(0);
                captionHistoryWindow.RefreshArchiveDate();
            }
            pendingCaptionCommitted = true;
            captionLyricWindow.ShowLines(PreviousCaptionText(), pendingCaptionText, targetRect);
            services.Log("Committed stable caption to in-memory history");
            if (allowModel)
                TryStartCaptionRefinement();
        }

        private void TryStartCaptionRefinement()
        {
            if (!active || services.WorkMode != "caption" ||
                !pendingCaptionCommitted || pendingCaptionWords == null ||
                pendingCaptionWords.Count == 0 || refinementRunning ||
                pendingCaptionGeneration <= lastCaptionRefinedGeneration ||
                DateTime.UtcNow - lastCaptionModelUtc <
                    TimeSpan.FromMilliseconds(CaptionModelInterval))
                return;
            refinementRunning = true;
            lastCaptionModelUtc = DateTime.UtcNow;
            lastCaptionRefinedGeneration = pendingCaptionGeneration;
            services.Log(
                "Started stable caption refinement generation " +
                pendingCaptionGeneration + " with " + pendingCaptionWords.Count + " OCR words");
            StartRefinement(
                pendingCaptionGeneration,
                pendingCaptionWindow,
                pendingCaptionCaptureRect,
                new List<OcrWord>(pendingCaptionWords),
                pendingCaptionOffsetX,
                pendingCaptionOffsetY);
        }

        private string PreviousCaptionText()
        {
            int index = captionHistory.Count - 1;
            if (index >= 0 && String.Equals(
                    captionHistory[index].text, pendingCaptionText, StringComparison.Ordinal))
                index--;
            return index >= 0 ? captionHistory[index].text : String.Empty;
        }

        private static bool AreCaptionVariants(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second))
                return false;
            string left = first.Trim().ToLowerInvariant();
            string right = second.Trim().ToLowerInvariant();
            if (left.StartsWith(right, StringComparison.Ordinal) ||
                right.StartsWith(left, StringComparison.Ordinal))
                return true;
            int limit = Math.Min(left.Length, right.Length);
            int common = 0;
            while (common < limit && left[common] == right[common])
                common++;
            return limit >= 6 && common >= limit * 3 / 5;
        }

        private static string BuildCaptionText(
            List<OcrWord> words,
            out List<OcrWord> selectedWords)
        {
            selectedWords = new List<OcrWord>();
            if (words == null || words.Count == 0)
                return String.Empty;
            List<OcrWord> ordered = words.FindAll(delegate(OcrWord word) {
                return word != null && !String.IsNullOrWhiteSpace(word.text) && word.h > 0;
            });
            ordered.Sort(delegate(OcrWord left, OcrWord right) {
                int vertical = left.y.CompareTo(right.y);
                // Establish a total order first; the following line grouping handles baseline tolerance.
                int horizontal = left.x.CompareTo(right.x);
                if (vertical != 0) return vertical;
                if (horizontal != 0) return horizontal;
                int height = left.h.CompareTo(right.h);
                return height != 0 ? height : String.CompareOrdinal(left.text, right.text);
            });
            List<CaptionOcrLine> lines = new List<CaptionOcrLine>();
            foreach (OcrWord word in ordered)
            {
                double center = word.y + word.h / 2.0;
                CaptionOcrLine line = null;
                foreach (CaptionOcrLine candidate in lines)
                {
                    if (Math.Abs(candidate.centerY - center) <=
                        Math.Max(candidate.height, word.h) * 0.65)
                    {
                        line = candidate;
                        break;
                    }
                }
                if (line == null)
                {
                    line = new CaptionOcrLine();
                    lines.Add(line);
                }
                line.Add(word);
            }
            foreach (CaptionOcrLine line in lines)
                line.Finish();
            lines.RemoveAll(delegate(CaptionOcrLine line) {
                return line.text.Length < 4;
            });
            if (lines.Count == 0)
                return String.Empty;
            lines.Sort(delegate(CaptionOcrLine left, CaptionOcrLine right) {
                int score = right.Score.CompareTo(left.Score);
                return score != 0 ? score : right.centerY.CompareTo(left.centerY);
            });
            CaptionOcrLine best = lines[0];
            CaptionOcrLine adjacent = null;
            foreach (CaptionOcrLine line in lines)
            {
                if (line == best)
                    continue;
                if (Math.Abs(line.centerY - best.centerY) <= Math.Max(line.height, best.height) * 2.6 &&
                    Math.Abs(line.centerX - best.centerX) <= Math.Max(line.width, best.width) * 0.35)
                {
                    adjacent = line;
                    break;
                }
            }
            string result;
            if (adjacent == null)
            {
                result = best.text;
                selectedWords.AddRange(best.Words);
            }
            else if (adjacent.centerY < best.centerY)
            {
                result = adjacent.text + " " + best.text;
                selectedWords.AddRange(adjacent.Words);
                selectedWords.AddRange(best.Words);
            }
            else
            {
                result = best.text + " " + adjacent.text;
                selectedWords.AddRange(best.Words);
                selectedWords.AddRange(adjacent.Words);
            }
            result = result.Trim();
            return result.Length <= 500 ? result : result.Substring(0, 500).TrimEnd();
        }

        private static void OffsetHighlights(ScanResponse response, int x, int y)
        {
            if (response == null || response.highlights == null || (x == 0 && y == 0))
                return;
            foreach (HighlightItem item in response.highlights)
            {
                item.x += x;
                item.y += y;
            }
        }

        private static ScanResponse CloneScanResponseHighlights(ScanResponse source)
        {
            ScanResponse clone = new ScanResponse {
                ok = source != null && source.ok,
                error = source == null ? null : source.error,
                duration_ms = source == null ? 0 : source.duration_ms,
                ocr_ms = source == null ? 0 : source.ocr_ms,
                local_analysis_ms = source == null ? 0 : source.local_analysis_ms,
                analysis_duration_ms = source == null ? 0 : source.analysis_duration_ms,
                analysis_mode = source == null ? null : source.analysis_mode,
                words = source == null ? null : source.words,
                highlights = new List<HighlightItem>()
            };
            if (source == null || source.highlights == null)
                return clone;
            foreach (HighlightItem item in source.highlights)
            {
                if (item == null) continue;
                clone.highlights.Add(new HighlightItem {
                    term = item.term, context = item.context, kind = item.kind,
                    title = item.title, time_text = item.time_text,
                    start_iso = item.start_iso, end_iso = item.end_iso,
                    utc_offset = item.utc_offset,
                    needs_confirmation = item.needs_confirmation,
                    x = item.x, y = item.y, w = item.w, h = item.h
                });
            }
            return clone;
        }

    }
}
