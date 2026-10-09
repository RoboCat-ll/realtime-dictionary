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
    internal sealed partial class ServiceManager : IDisposable
    {
        public string Capture(NativeRect rect, float ocrScale = 1f)
        {
            string directory = Path.Combine(Path.GetTempPath(), "RealtimeDictionary");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "scan-" + Guid.NewGuid().ToString("N") + ".png");
            using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    rect.Left,
                    rect.Top,
                    0,
                    0,
                    new Size(rect.Width, rect.Height),
                    CopyPixelOperation.SourceCopy);
                if (ocrScale <= 1f)
                {
                    bitmap.Save(path, ImageFormat.Png);
                }
                else
                {
                    int width = (int)Math.Round(rect.Width * ocrScale);
                    int height = (int)Math.Round(rect.Height * ocrScale);
                    using (Bitmap enlarged = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    using (Graphics upscaler = Graphics.FromImage(enlarged))
                    {
                        upscaler.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        upscaler.DrawImage(bitmap, new Rectangle(0, 0, width, height),
                            new Rectangle(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel);
                        enlarged.Save(path, ImageFormat.Png);
                    }
                }
            }
            return path;
        }

        public byte[] ComputeVisualFingerprint(string path)
        {
            using (Bitmap bitmap = new Bitmap(path))
                return ComputeBitmapFingerprint(bitmap);
        }

        public byte[] ComputeScreenFingerprint(NativeRect rect)
        {
            using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                    new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
                return ComputeBitmapFingerprint(bitmap);
            }
        }

        private byte[] ComputeBitmapFingerprint(Bitmap bitmap)
        {
            const int columns = 48;
            const int rows = 27;
            byte[] fingerprint = new byte[columns * rows];
            {
                for (int row = 0; row < rows; row++)
                {
                    int y = Math.Min(bitmap.Height - 1, (row * 2 + 1) * bitmap.Height / (rows * 2));
                    for (int column = 0; column < columns; column++)
                    {
                        int x = Math.Min(bitmap.Width - 1, (column * 2 + 1) * bitmap.Width / (columns * 2));
                        Color color = bitmap.GetPixel(x, y);
                        fingerprint[row * columns + column] = (byte)(
                            (color.R * 30 + color.G * 59 + color.B * 11) / 100);
                    }
                }
            }
            return fingerprint;
        }

        public bool AreVisualFingerprintsEquivalent(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return false;
            int materiallyChanged = 0;
            int allowedChanges = Math.Max(5, first.Length / 100);
            for (int index = 0; index < first.Length; index++)
            {
                if (Math.Abs(first[index] - second[index]) <= 12)
                    continue;
                materiallyChanged++;
                if (materiallyChanged > allowedChanges)
                    return false;
            }
            return true;
        }

        public ScanResponse Scan(NativeRect rect, string capturePath)
        {
            try
            {
                return ScanWithWindowsOcr(capturePath);
            }
            catch (AnalysisRequestException error)
            {
                Log("Term analysis failed after Windows OCR; not retrying OCR: " + error);
                return new ScanResponse { ok = false, error = error.Message };
            }
            catch (Exception error)
            {
                Log("Windows OCR failed; using PaddleOCR fallback: " + error);
                EnsureLegacyOcr();
                return ScanWithLegacyOcr(rect);
            }
        }

        private ScanResponse ScanWithWindowsOcr(string capturePath)
        {
            Stopwatch totalWatch = Stopwatch.StartNew();
            Stopwatch ocrWatch = Stopwatch.StartNew();
            List<OcrWord> words = ReadWindowsOcrWords(capturePath);
            int ocrMs = (int)ocrWatch.ElapsedMilliseconds;
            Stopwatch analysisWatch = Stopwatch.StartNew();
            ScanResponse result = AnalyzeWords(words, "instant");
            int localAnalysisMs = (int)analysisWatch.ElapsedMilliseconds;
            result.words = words;
            result.ocr_ms = ocrMs;
            result.local_analysis_ms = localAnalysisMs;
            result.duration_ms = (int)totalWatch.ElapsedMilliseconds;
            Log("Windows OCR scan: " + words.Count + " words, " + result.highlights.Count +
                " highlights, OCR " + result.ocr_ms + "ms, local analysis " +
                result.local_analysis_ms + "ms (server " + result.analysis_duration_ms +
                "ms), total " + result.duration_ms + "ms");
            return result;
        }

        public string ReadSelectionRegion(Rectangle region)
        {
            // The chat client's small antialiased text loses Chinese strokes at native size.
            // Scale only bounded message OCR; conversation scans retain pixel coordinates.
            string path = Capture(new NativeRect { Left = region.Left, Top = region.Top,
                Right = region.Right, Bottom = region.Bottom }, 1.7f);
            try
            {
                List<OcrWord> words = ReadWindowsOcrWords(path, true);
                words = RepairSplitOcrGlyphs(words);
                StringBuilder text = new StringBuilder();
                OcrWord previous = null;
                foreach (OcrWord word in words)
                {
                    if (previous != null) text.Append(SeparatorBetweenWords(previous, word));
                    text.Append(word.text); previous = word;
                }
                return text.ToString();
            }
            finally { try { File.Delete(path); } catch { } }
        }

        internal static List<OcrWord> RepairSplitOcrGlyphs(List<OcrWord> words)
        {
            List<OcrWord> repaired = new List<OcrWord>();
            for (int i = 0; i < words.Count; i++)
            {
                OcrWord left = words[i];
                if (i + 1 < words.Count && left.text == "另" && words[i + 1].text == "刂")
                {
                    OcrWord right = words[i + 1];
                    double height = Math.Max(left.h, right.h);
                    double gap = right.x - left.x - left.w;
                    double width = right.x + right.w - left.x;
                    if (height > 0 && gap >= -height * .1 && gap <= height * .25 &&
                        Math.Abs(left.y - right.y) <= height * .2 &&
                        left.w <= height * .8 && right.w <= height * .5 &&
                        width >= height * .65 && width <= height * 1.15)
                    {
                        repaired.Add(new OcrWord { text = "别", x = left.x,
                            y = Math.Min(left.y, right.y), w = width,
                            h = Math.Max(left.y + left.h, right.y + right.h) - Math.Min(left.y, right.y) });
                        i++;
                        continue;
                    }
                }
                repaired.Add(left);
            }
            return repaired;
        }

        internal static List<OcrWord> RepairLatinOcrArtifacts(List<OcrWord> primary, List<OcrWord> english)
        {
            if (primary == null) return new List<OcrWord>();
            if (english == null || english.Count == 0) return primary;
            var output = new List<OcrWord>();
            for (int index = 0; index < primary.Count; index++)
            {
                OcrWord word = primary[index];
                bool repaired = false;
                string token = "";
                double left=word.x, top=word.y, right=word.x+word.w, bottom=word.y+word.h;
                for (int end=index; end<primary.Count && end<index+5; end++)
                {
                    var part=primary[end];
                    string fragment=part.text ?? "";
                    if (!Regex.IsMatch(fragment, @"^[A-Z℃°]+$")) break;
                    if (end>index) {
                        double height=Math.Max(word.h,part.h);
                        var previous=primary[end-1];
                        double gap=part.x-previous.x-previous.w;
                        if (height<=0 || gap>height*.35 || gap< -height*.15 ||
                            Math.Abs((part.y+part.h/2)-(word.y+word.h/2))>height*.35) break;
                    }
                    token+=fragment;
                    left=Math.Min(left,part.x); top=Math.Min(top,part.y);
                    right=Math.Max(right,part.x+part.w); bottom=Math.Max(bottom,part.y+part.h);
                    if (token.Length>8) break;
                    if (!Regex.IsMatch(token, @"^[A-Z][A-Z℃°]{1,7}$") ||
                        (token.IndexOf('℃')<0 && token.IndexOf('°')<0) || right<=left || bottom<=top) continue;
                    string original=token;
                    double x=left,y=top,w=right-left,h=bottom-top;
                    var aligned=english.Where(candidate=> {
                        string value=candidate.text ?? "";
                        if (!Regex.IsMatch(value,@"^[A-Z]{2,8}$") || value[0]!=original[0] ||
                            value[value.Length-1]!=original[original.Length-1] || candidate.w<=0 || candidate.h<=0) return false;
                        double width=Math.Max(0,Math.Min(x+w,candidate.x+candidate.w)-Math.Max(x,candidate.x));
                        double height=Math.Max(0,Math.Min(y+h,candidate.y+candidate.h)-Math.Max(y,candidate.y));
                        double intersection=width*height;
                        double union=w*h+candidate.w*candidate.h-intersection;
                        return union>0 && intersection/union>=.75;
                    }).ToList();
                    if (aligned.Count!=1) continue;
                    output.Add(new OcrWord { text=aligned[0].text,x=x,y=y,w=w,h=h });
                    index=end; repaired=true; break;
                }
                if (!repaired) output.Add(word);
            }
            return output;
        }

        private List<OcrWord> ReadWindowsOcrWords(string capturePath, bool includeLatin = false)
        {
            Exception workerError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    lock (windowsOcrLock)
                    {
                        EnsureWindowsOcrWorker();
                        string requestId = Guid.NewGuid().ToString("N");
                        string request = serializer.Serialize(new Dictionary<string, string> {
                            { "request_id", requestId },
                            { "include_latin", includeLatin ? "true" : "false" },
                            { "image_path_base64", Convert.ToBase64String(
                                Encoding.UTF8.GetBytes(Path.GetFullPath(capturePath))) }
                        });
                        windowsOcrWorker.StandardInput.WriteLine(request);
                        windowsOcrWorker.StandardInput.Flush();
                        Task<string> responseTask = Task.Factory.StartNew(
                            delegate { return windowsOcrWorker.StandardOutput.ReadLine(); });
                        if (!responseTask.Wait(15000))
                            throw new TimeoutException("Windows OCR worker timed out.");
                        string output = responseTask.Result;
                        if (String.IsNullOrWhiteSpace(output))
                            throw new InvalidOperationException("Windows OCR worker closed its output stream.");
                        WindowsOcrResponse ocr = serializer.Deserialize<WindowsOcrResponse>(output);
                        if (ocr == null || !ocr.ok || !String.Equals(ocr.request_id, requestId,
                            StringComparison.Ordinal))
                            throw new InvalidOperationException("Windows OCR worker returned an invalid response: " +
                                (ocr == null ? "empty response" : ocr.error));
                        Log("Windows OCR worker: decode " + ocr.decode_ms + "ms, recognize " +
                            ocr.recognize_ms + "ms, worker " + ocr.worker_ms + "ms");
                        return includeLatin ? RepairLatinOcrArtifacts(ocr.words, ocr.latin_words) :
                            (ocr.words ?? new List<OcrWord>());
                    }
                }
                catch (Exception error)
                {
                    workerError = error;
                    lock (windowsOcrLock)
                        StopWindowsOcrWorker();
                }
            }

            Log("Windows OCR worker unavailable; using one-shot bridge: " + workerError.Message);
            return ReadWindowsOcrWordsOneShot(capturePath);
        }

        private void EnsureWindowsOcrWorker()
        {
            if (windowsOcrWorker != null && !windowsOcrWorker.HasExited)
                return;

            StopWindowsOcrWorker();
            string script = Path.Combine(projectRoot, "native-host", "windows_ocr_worker.ps1");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "powershell.exe";
            info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"";
            info.WorkingDirectory = projectRoot;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            Process worker = Process.Start(info);
            windowsOcrWorker = worker;
            if (worker == null)
                throw new InvalidOperationException("Windows OCR worker did not start.");
            windowsOcrErrorTask = Task.Factory.StartNew(
                delegate { return worker.StandardError.ReadToEnd(); });
            Task<string> readyTask = Task.Factory.StartNew(
                delegate { return worker.StandardOutput.ReadLine(); });
            if (!readyTask.Wait(10000))
                throw new TimeoutException("Windows OCR worker warmup timed out.");
            string readyJson = readyTask.Result;
            WindowsOcrResponse ready = String.IsNullOrWhiteSpace(readyJson) ? null :
                serializer.Deserialize<WindowsOcrResponse>(readyJson);
            if (ready == null || !ready.ok || !ready.ready)
                throw new InvalidOperationException("Windows OCR worker failed to initialize.");
            Log("Windows OCR worker ready as PID " + windowsOcrWorker.Id);
        }

        private void StopWindowsOcrWorker()
        {
            Process worker = windowsOcrWorker;
            Task<string> errorTask = windowsOcrErrorTask;
            windowsOcrWorker = null;
            windowsOcrErrorTask = null;
            if (worker == null)
                return;
            try
            {
                if (!worker.HasExited)
                {
                    try { worker.StandardInput.Close(); } catch { }
                    if (!worker.WaitForExit(750))
                        worker.Kill();
                }
                if (errorTask != null && errorTask.IsCompleted)
                {
                    string error = errorTask.Result.Trim();
                    if (!String.IsNullOrEmpty(error))
                        Log("Windows OCR worker stderr: " + error);
                }
            }
            catch { }
            finally { worker.Dispose(); }
        }

        private List<OcrWord> ReadWindowsOcrWordsOneShot(string capturePath)
        {
            string script = Path.Combine(projectRoot, "native-host", "windows_ocr.ps1");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "powershell.exe";
            info.Arguments =
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script +
                "\" -ImagePath \"" + capturePath + "\"";
            info.WorkingDirectory = projectRoot;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            Process process = Process.Start(info);
            if (process == null)
                throw new InvalidOperationException("Windows OCR bridge did not start.");

            Task<string> outputTask = Task.Factory.StartNew(
                delegate { return process.StandardOutput.ReadToEnd(); });
            Task<string> errorTask = Task.Factory.StartNew(
                delegate { return process.StandardError.ReadToEnd(); });
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Windows OCR timed out.");
            }
            string output = outputTask.Result.Trim();
            string error = errorTask.Result.Trim();
            int exitCode = process.ExitCode;
            process.Dispose();
            if (exitCode != 0 || string.IsNullOrEmpty(output))
                throw new InvalidOperationException(
                    "Windows OCR bridge failed: " + (string.IsNullOrEmpty(error) ? output : error));

            WindowsOcrResponse ocr = serializer.Deserialize<WindowsOcrResponse>(output);
            if (ocr == null || !ocr.ok)
                throw new InvalidOperationException("Windows OCR returned an invalid response.");
            return ocr.words ?? new List<OcrWord>();
        }

        private ScanResponse AnalyzeWords(List<OcrWord> words, string mode)
        {
            if (mode != "local" && mode != "instant") EnsureCloudConsent(false);
            StringBuilder fullText = new StringBuilder();
            List<int> starts = new List<int>();
            OcrWord previous = null;
            foreach (OcrWord word in words)
            {
                if (previous != null)
                    fullText.Append(SeparatorBetweenWords(previous, word));
                starts.Add(fullText.Length);
                fullText.Append(word.text ?? string.Empty);
                previous = word;
            }

            string sourceText = fullText.ToString();
            if (String.IsNullOrWhiteSpace(sourceText))
            {
                Log("OCR frame contained no readable text; skipped term analysis");
                return new ScanResponse {
                    ok = true,
                    highlights = new List<HighlightItem>(),
                    words = words,
                    analysis_mode = "local"
                };
            }
            Dictionary<string, string> analyzePayload = new Dictionary<string, string>();
            analyzePayload["text"] = sourceText;
            analyzePayload["mode"] = mode;
            analyzePayload["difficulty"] = Difficulty;
            analyzePayload["context_id"] = AnalysisContextId;
            byte[] analyzeBody = Encoding.UTF8.GetBytes(serializer.Serialize(analyzePayload));
            AnalyzeResponse analysis = null;
            Exception analyzeError = null;
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8877/analyze");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                bool localOnly = mode == "local" || mode == "instant";
                request.Timeout = localOnly ? 5000 : 22000;
                request.ReadWriteTimeout = localOnly ? 5000 : 22000;
                SetToken(request);
                request.ContentLength = analyzeBody.Length;
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(analyzeBody, 0, analyzeBody.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        analysis = serializer.Deserialize<AnalyzeResponse>(reader.ReadToEnd());
                    analyzeError = null;
                    break;
                }
                catch (WebException error)
                {
                    if (attempt == 0 && IsForbidden(error))
                    {
                        ResetServiceToken();
                        continue;
                    }
                    analyzeError = error;
                    break;
                }
                catch (Exception error)
                {
                    analyzeError = error;
                    break;
                }
            }
            if (analyzeError != null)
            {
                throw new AnalysisRequestException("Term analysis timed out or returned an invalid response.", analyzeError);
            }

            List<HighlightItem> highlights = new List<HighlightItem>();
            HashSet<string> seen = new HashSet<string>();
            List<AnalysisEntity> renderEntities = new List<AnalysisEntity>();
            if (analysis != null)
            {
                UnicodeSpans.Convert(sourceText, analysis.entities);
                UnicodeSpans.Convert(sourceText, analysis.actions);
                if (analysis.entities != null)
                    renderEntities.AddRange(analysis.entities);
                if (analysis.actions != null)
                    renderEntities.AddRange(analysis.actions);
            }
            if (renderEntities.Count > 0)
            {
                foreach (AnalysisEntity entity in renderEntities)
                {
                    bool isTask = String.Equals(
                        entity.type, "calendar_event", StringComparison.OrdinalIgnoreCase);
                    if (!isTask && IsIgnoredTerm(entity.text))
                        continue;
                    if (entity.start < 0 || entity.end <= entity.start)
                        continue;
                    int contextStart = Math.Max(0, entity.start - 90);
                    int contextEnd = Math.Min(sourceText.Length, entity.end + 90);
                    string entityContext = sourceText.Substring(
                        contextStart, Math.Max(0, contextEnd - contextStart));
                    List<HighlightItem> fragments = new List<HighlightItem>();
                    for (int index = 0; index < words.Count; index++)
                    {
                        OcrWord word = words[index];
                        string text = word.text ?? string.Empty;
                        int spanStart = starts[index];
                        int spanEnd = spanStart + text.Length;
                        int overlapStart = Math.Max(entity.start, spanStart);
                        int overlapEnd = Math.Min(entity.end, spanEnd);
                        if (overlapStart >= overlapEnd || text.Length == 0)
                            continue;

                        double left = (overlapStart - spanStart) / (double)text.Length;
                        double right = (overlapEnd - spanStart) / (double)text.Length;
                        HighlightItem item = new HighlightItem();
                        item.term = entity.text;
                        item.context = entityContext;
                        item.kind = isTask ? "task" : "concept";
                        item.title = entity.title;
                        item.time_text = entity.time_text;
                        item.start_iso = entity.start_iso;
                        item.end_iso = entity.end_iso;
                        item.utc_offset = entity.utc_offset;
                        item.needs_confirmation = isTask || entity.needs_confirmation;
                        item.x = (int)Math.Round(word.x + word.w * left) - 1;
                        item.y = (int)Math.Round(word.y) - 1;
                        item.w = Math.Max(3, (int)Math.Round(word.w * (right - left)) + 2);
                        item.h = Math.Max(3, (int)Math.Round(word.h) + 2);
                        string signature =
                            item.kind + "|" + item.term + "|" + item.x + "|" + item.y + "|" + item.w + "|" + item.h;
                        if (seen.Add(signature))
                            fragments.Add(item);
                    }

                    HighlightItem merged = null;
                    foreach (HighlightItem fragment in fragments)
                    {
                        if (merged != null &&
                            Math.Abs(fragment.y - merged.y) <= Math.Max(fragment.h, merged.h) / 2 &&
                            fragment.x <= merged.x + merged.w + 8)
                        {
                            int right = Math.Max(merged.x + merged.w, fragment.x + fragment.w);
                            int bottom = Math.Max(merged.y + merged.h, fragment.y + fragment.h);
                            merged.x = Math.Min(merged.x, fragment.x);
                            merged.y = Math.Min(merged.y, fragment.y);
                            merged.w = right - merged.x;
                            merged.h = bottom - merged.y;
                        }
                        else
                        {
                            merged = fragment;
                            highlights.Add(merged);
                        }
                    }
                }
            }
            return new ScanResponse
            {
                ok = true,
                highlights = highlights,
                analysis_mode = analysis == null ? null : analysis.analysis_mode,
                analysis_duration_ms = analysis == null ? 0 : analysis.analysis_duration_ms
            };
        }

        private static string SeparatorBetweenWords(OcrWord previous, OcrWord current)
        {
            string left = previous.text ?? string.Empty;
            string right = current.text ?? string.Empty;
            if (left.Length == 0 || right.Length == 0)
                return String.Empty;
            double previousBottom = previous.y + previous.h;
            double currentBottom = current.y + current.h;
            double verticalGap = Math.Max(previous.y, current.y) -
                                 Math.Min(previousBottom, currentBottom);
            double lineHeight = Math.Max(1.0, Math.Min(previous.h, current.h));
            if (verticalGap > lineHeight * 0.55)
                return "\n";
            bool leftWordLike = ContainsLatinOrDigit(left);
            bool rightWordLike = ContainsLatinOrDigit(right);
            return leftWordLike && rightWordLike ? " " : String.Empty;
        }

        private static bool ContainsLatinOrDigit(string value)
        {
            foreach (char character in value)
            {
                if ((character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9'))
                    return true;
            }
            return false;
        }

        private void EnsureLegacyOcr()
        {
            lock (serviceLock)
            {
                if (!IsHealthy("http://127.0.0.1:8878/health", 350))
                    StartPython("ocr_service.py");
                WaitForHealth("http://127.0.0.1:8878/health", 30000);
            }
        }

        private ScanResponse ScanWithLegacyOcr(NativeRect rect)
        {
            Dictionary<string, int> payload = new Dictionary<string, int>();
            payload["x"] = rect.Left;
            payload["y"] = rect.Top;
            payload["w"] = rect.Width;
            payload["h"] = rect.Height;
            EnsureCloudConsent(false);
            return PostJson<ScanResponse>("http://127.0.0.1:8878/scan", payload, 150000);
        }

    }
}
