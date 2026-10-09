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
    internal sealed class CaptionAudioChunk
    {
        public byte[] Wav { get; set; }
        public DateTime CapturedAt { get; set; }
        public int Attempts { get; set; }
        public bool HasOverlap { get; set; }
        public int Sequence { get; set; }
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private int completed;
        public int ElapsedMilliseconds { get { return (int)Math.Min(600000, elapsed.ElapsedMilliseconds); } }
        public void Complete(string outcome, Action<string, int, int> record)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            elapsed.Stop();
            if (record != null) record(outcome, ElapsedMilliseconds, Attempts);
        }
    }

    internal sealed class CaptionAudioBacklog
    {
        private readonly Queue<CaptionAudioChunk> pending = new Queue<CaptionAudioChunk>();
        private readonly int pendingLimit;
        private CaptionAudioChunk retry;

        public CaptionAudioBacklog(int pendingLimit)
        {
            if (pendingLimit < 1) throw new ArgumentOutOfRangeException("pendingLimit");
            this.pendingLimit = pendingLimit;
        }

        public int PendingCount { get { return pending.Count; } }
        public bool HasWork { get { return retry != null || pending.Count > 0; } }

        public CaptionAudioChunk Enqueue(byte[] wav, DateTime capturedAt, bool hasOverlap = false, int sequence = 0)
        {
            CaptionAudioChunk dropped = pending.Count >= pendingLimit ? pending.Dequeue() : null;
            pending.Enqueue(new CaptionAudioChunk { Wav = wav, CapturedAt = capturedAt,
                HasOverlap = hasOverlap, Sequence = sequence });
            return dropped;
        }

        public CaptionAudioChunk TakeNext()
        {
            CaptionAudioChunk next = retry;
            retry = null;
            if (next == null && pending.Count > 0) next = pending.Dequeue();
            if (next != null) next.Attempts++;
            return next;
        }

        public bool HoldForRetry(CaptionAudioChunk failed)
        {
            if (failed == null || failed.Attempts >= 2 || retry != null) return false;
            retry = failed;
            return true;
        }

        public void Clear(Action<string, int, int> record = null)
        {
            while (pending.Count > 0) pending.Dequeue().Complete("cancelled", record);
            if (retry != null) retry.Complete("cancelled", record);
            retry = null;
        }
    }

    // All access is on the UI thread. A later result waits for an earlier retry.
    internal sealed class CaptionOrderedCompletions
    {
        private readonly SortedDictionary<int, Tuple<CaptionAudioChunk, Func<bool>>> ready =
            new SortedDictionary<int, Tuple<CaptionAudioChunk, Func<bool>>>();
        private int next = 1;
        internal int Count { get { return ready.Count; } }
        internal void Add(CaptionAudioChunk chunk, Func<bool> deliver)
        {
            if (chunk.Sequence < next || ready.ContainsKey(chunk.Sequence))
                throw new InvalidOperationException("Duplicate or stale caption completion.");
            ready.Add(chunk.Sequence, Tuple.Create(chunk, deliver));
        }
        internal void Drain(Func<bool> current)
        {
            Tuple<CaptionAudioChunk, Func<bool>> item;
            while (current() && ready.TryGetValue(next, out item))
            {
                ready.Remove(next);
                bool final = item.Item2();
                if (!current() || !final) return;
                next++;
            }
        }
        internal void Clear(Action<string, int, int> record = null)
        {
            foreach (var item in ready.Values) item.Item1.Complete("cancelled", record);
            ready.Clear();
            next = 1;
        }
    }

    internal sealed class CaptionEntry
    {
        public DateTime timestamp { get; set; }
        public string text { get; set; }
        public string raw_text { get; set; }
        public bool is_gap { get; set; }
        public string session_key { get; set; }
        public string session_label { get; set; }
    }

    internal sealed class CaptionArchiveReadResult
    {
        public List<CaptionEntry> Entries = new List<CaptionEntry>();
        public int SkippedFiles;
        public int SkippedLines;
        public bool Truncated;
        public CaptionArchiveCursor Next;
        public bool Incomplete { get { return SkippedFiles > 0 || SkippedLines > 0 || Truncated; } }
    }

    internal sealed class CaptionHistoryArchive
    {
        private const int MaxDailyEntries = 20000;
        private readonly string root;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private string currentFile;
        private DateTime currentStart;
        private string currentLabel;

        public CaptionHistoryArchive(string directory)
        {
            root = directory;
        }

        public static string DefaultDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "RealtimeDictionary", "caption-history"); }
        }

        public DateTime CurrentDate { get { return currentStart.Date; } }

        public int DeleteDate(DateTime date)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string directory = Path.GetFullPath(Path.Combine(fullRoot, date.ToString("yyyy-MM-dd")));
            if (!directory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("历史路径无效。");
            if (!Directory.Exists(directory)) return 0;
            if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不能删除链接目录内的历史。");
            string[] files = Directory.GetFiles(directory, "*.jsonl");
            foreach (string file in files)
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("不能删除链接文件。");
            int deleted = 0;
            foreach (string file in files) { File.Delete(file); deleted++; }
            return deleted;
        }

        public void BeginSession(DateTime startedAt, string sourceLabel)
        {
            currentStart = startedAt;
            currentLabel = String.IsNullOrWhiteSpace(sourceLabel) ? "会议字幕" : sourceLabel.Trim();
            string name = startedAt.ToString("yyyyMMdd-HHmmss-fffffff") + "-" +
                Guid.NewGuid().ToString("N") + ".jsonl";
            currentFile = Path.Combine(root, startedAt.ToString("yyyy-MM-dd"), name);
        }

        public bool Append(CaptionEntry entry, bool replaceLast = false)
        {
            if (entry == null || String.IsNullOrWhiteSpace(entry.text)) return false;
            if (currentFile == null) BeginSession(entry.timestamp, "会议字幕");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(currentFile));
                var record = new Dictionary<string, object> {
                    { "kind", entry.is_gap ? "gap" : (replaceLast ? "replace_last" : "line") },
                    { "timestamp", entry.timestamp.ToString("o") },
                    { "text", entry.text },
                    { "session_started", currentStart.ToString("o") },
                    { "source", currentLabel }
                };
                if (!entry.is_gap && !String.IsNullOrWhiteSpace(entry.raw_text) &&
                    !String.Equals(entry.raw_text, entry.text, StringComparison.Ordinal))
                    record["raw_text"] = entry.raw_text;
                File.AppendAllText(currentFile, serializer.Serialize(record) + Environment.NewLine,
                    new UTF8Encoding(false));
                entry.session_key = Path.GetFileNameWithoutExtension(currentFile);
                entry.session_label = currentStart.ToString("HH:mm") + " · " + currentLabel;
                return true;
            }
            catch { return false; }
        }

        public List<CaptionEntry> LoadDate(DateTime date)
        {
            return LoadDateWithStatus(date).Entries;
        }

        public CaptionArchiveReadResult LoadPage(DateTime date, CaptionArchiveCursor cursor)
        {
            return CaptionArchivePages.Read(Path.Combine(root, date.ToString("yyyy-MM-dd")), cursor);
        }

        public CaptionArchiveReadResult LoadDateWithStatus(DateTime date)
        {
            var serializer = new JavaScriptSerializer();
            var loaded = new CaptionArchiveReadResult();
            var result = loaded.Entries;
            string directory = Path.Combine(root, date.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(directory)) return loaded;
            string[] files;
            try { files = Directory.GetFiles(directory, "*.jsonl"); }
            catch { loaded.SkippedFiles++; return loaded; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                var session = new List<CaptionEntry>();
                try
                {
                    if (new FileInfo(file).Length > 20 * 1024 * 1024) { loaded.SkippedFiles++; continue; }
                    foreach (string line in File.ReadLines(file, Encoding.UTF8))
                    {
                        if (line.Length == 0) continue;
                        if (line.Length > 10000) { loaded.SkippedLines++; continue; }
                        try
                        {
                            var record = serializer.Deserialize<Dictionary<string, object>>(line);
                            if (record == null || !record.ContainsKey("kind") ||
                                !record.ContainsKey("timestamp") || !record.ContainsKey("text")) { loaded.SkippedLines++; continue; }
                            string text = record["text"] as string;
                            DateTime stamp;
                            if (String.IsNullOrWhiteSpace(text) || text.Length > 2000 ||
                                !DateTime.TryParse(record["timestamp"] as string, out stamp)) { loaded.SkippedLines++; continue; }
                            string started = record.ContainsKey("session_started")
                                ? record["session_started"] as string : null;
                            DateTime start;
                            if (!DateTime.TryParse(started, out start)) start = stamp;
                            string source = record.ContainsKey("source")
                                ? record["source"] as string : null;
                            string kind = record["kind"] as string;
                            if (kind != "line" && kind != "replace_last" && kind != "gap") { loaded.SkippedLines++; continue; }
                            string rawText = record.ContainsKey("raw_text")
                                ? record["raw_text"] as string : null;
                            if (rawText != null && rawText.Length > 2000) rawText = null;
                            var entry = new CaptionEntry { timestamp = stamp, text = text,
                                is_gap = kind == "gap",
                                raw_text = kind == "gap" ? null : rawText,
                                session_key = Path.GetFileNameWithoutExtension(file),
                                session_label = start.ToString("HH:mm") + " · " +
                                    (String.IsNullOrWhiteSpace(source) ? "会议字幕" : source) };
                            if (kind == "replace_last" && session.Count > 0 &&
                                !session[session.Count - 1].is_gap)
                                session[session.Count - 1] = entry;
                            else
                                session.Add(entry);
                        }
                        catch { loaded.SkippedLines++; }
                    }
                }
                catch { loaded.SkippedFiles++; }
                result.AddRange(session.OrderBy(entry => entry.timestamp));
                if (result.Count > MaxDailyEntries) {
                    loaded.Truncated = true;
                    result.RemoveRange(0, result.Count - MaxDailyEntries);
                }
            }
            return loaded;
        }
    }

    internal sealed class CaptionSpeechSegment
    {
        public string Text { get; set; }
        public int Offset { get; set; }
    }

    internal sealed class CaptionOcrLine
    {
        private readonly List<OcrWord> words = new List<OcrWord>();
        public string text { get; private set; }
        public double centerY { get; private set; }
        public double centerX { get; private set; }
        public double height { get; private set; }
        public double width { get; private set; }
        public int Score { get; private set; }
        public List<OcrWord> Words
        {
            get { return new List<OcrWord>(words); }
        }

        public void Add(OcrWord word)
        {
            words.Add(word);
            RecalculateBounds();
        }

        public void Finish()
        {
            words.Sort(delegate(OcrWord left, OcrWord right) {
                return left.x.CompareTo(right.x);
            });
            StringBuilder builder = new StringBuilder();
            OcrWord previousWord = null;
            foreach (OcrWord word in words)
            {
                string value = (word.text ?? String.Empty).Trim();
                if (value.Length == 0)
                    continue;
                if (builder.Length > 0 && NeedsSpace(previousWord, word,
                        builder[builder.Length - 1], value[0]))
                    builder.Append(' ');
                builder.Append(value);
                previousWord = word;
            }
            text = builder.ToString().Trim();
            Score = Math.Min(500, text.Length) * 2 + Math.Min(20, words.Count) * 7;
        }

        private void RecalculateBounds()
        {
            double left = Double.MaxValue;
            double top = Double.MaxValue;
            double right = Double.MinValue;
            double bottom = Double.MinValue;
            foreach (OcrWord word in words)
            {
                left = Math.Min(left, word.x);
                top = Math.Min(top, word.y);
                right = Math.Max(right, word.x + word.w);
                bottom = Math.Max(bottom, word.y + word.h);
            }
            width = Math.Max(1, right - left);
            height = Math.Max(1, bottom - top);
            centerX = left + width / 2.0;
            centerY = top + height / 2.0;
        }

        private static bool NeedsSpace(OcrWord previous, OcrWord current, char left, char right)
        {
            if (!IsAsciiWord(left) || !IsAsciiWord(right))
                return false;
            if (previous == null || current == null)
                return true;
            double gap = current.x - (previous.x + previous.w);
            double fragmentThreshold = Math.Max(
                2.0, Math.Min(previous.h, current.h) * 0.18);
            return gap > fragmentThreshold;
        }

        private static bool IsAsciiWord(char value)
        {
            return (value >= 'a' && value <= 'z') ||
                   (value >= 'A' && value <= 'Z') ||
                   (value >= '0' && value <= '9');
        }
    }

    internal sealed class CaptionLyricForm : Form
    {
        private static readonly Color Chroma = Color.Black;
        private const int MinAudioWidth = 340;
        private const int MinAudioHeight = 135;
        private readonly string layoutPath;
        private string previousLine = String.Empty;
        private string currentLine = String.Empty;
        private int sourceTop = -1;
        private bool audioBox;
        private int currentSourceOffset;
        private int audioWidth = 720;
        private int audioHeight = 170;
        private double audioX = 1;
        private double audioY = 1;
        private bool userLayout;
        private NativeRect audioTarget;
        private bool draggingAudio;
        private bool resizingAudio;
        private bool audioMoved;
        private Point mouseStart;
        private Rectangle boundsStart;

        public event Action HistoryRequested;

        public void SetSourceTop(int relativeTop) { sourceTop = relativeTop; }

        public CaptionLyricForm() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RealtimeDictionary", "caption-layout.json")) { }

        internal CaptionLyricForm(string savedLayoutPath)
        {
            layoutPath = savedLayoutPath;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Chroma;
            TransparencyKey = Chroma;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            LoadAudioLayout();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExToolWindow |
                                      NativeMethods.WsExNoActivate;
                if (!audioBox) parameters.ExStyle |= NativeMethods.WsExTransparent;
                return parameters;
            }
        }

        public void ShowLines(string previous, string current, NativeRect target)
        {
            bool changedMode = audioBox;
            audioBox = false;
            if (changedMode && IsHandleCreated) RecreateHandle();
            currentSourceOffset = 0;
            UpdateLines(previous, current, target);
        }

        public void ShowAudioLines(string previous, string current, int sourceOffset, NativeRect target)
        {
            if (!audioBox)
            {
                audioBox = true;
                if (IsHandleCreated) RecreateHandle();
            }
            currentSourceOffset = sourceOffset;
            UpdateLines(previous, current, target);
        }

        private void UpdateLines(string previous, string current, NativeRect target)
        {
            string normalizedPrevious = (previous ?? String.Empty).Trim();
            string normalizedCurrent = (current ?? String.Empty).Trim();
            if (String.Equals(normalizedPrevious, normalizedCurrent, StringComparison.Ordinal))
                normalizedPrevious = String.Empty;
            if (!String.Equals(previousLine, normalizedPrevious, StringComparison.Ordinal) ||
                !String.Equals(currentLine, normalizedCurrent, StringComparison.Ordinal))
            {
                previousLine = normalizedPrevious;
                currentLine = normalizedCurrent;
                Invalidate();
            }
            PositionFor(target);
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                Handle, NativeMethods.HwndTopMost, Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        public void PositionFor(NativeRect target)
        {
            if (audioBox)
            {
                Rectangle targetBounds = new Rectangle(target.Left, target.Top,
                    Math.Max(1, target.Width), Math.Max(1, target.Height));
                Rectangle monitor = Screen.FromRectangle(targetBounds).WorkingArea;
                Rectangle visible = Rectangle.Intersect(targetBounds, monitor);
                if (visible.Width < MinAudioWidth + 24 ||
                    visible.Height < MinAudioHeight + 72)
                    visible = monitor;
                audioTarget = new NativeRect { Left = visible.Left, Top = visible.Top,
                    Right = visible.Right, Bottom = visible.Bottom };
                int maxWidth = Math.Max(180, visible.Width - 24);
                int maxHeight = Math.Max(110, visible.Height - 72);
                int defaultWidth = Math.Min(720, Math.Max(MinAudioWidth, visible.Width * 52 / 100));
                int cardWidth = Math.Min(maxWidth, userLayout ? audioWidth : defaultWidth);
                int cardHeight = Math.Min(maxHeight, userLayout ? audioHeight : 170);
                int availableX = Math.Max(0, visible.Width - cardWidth - 16);
                int availableY = Math.Max(0, visible.Height - cardHeight - 56);
                int cardX = userLayout ? visible.Left + 8 + (int)Math.Round(audioX * availableX)
                    : visible.Right - cardWidth - 20;
                int cardY = userLayout ? visible.Top + 48 + (int)Math.Round(audioY * availableY)
                    : visible.Bottom - cardHeight - 105;
                Bounds = new Rectangle(
                    Math.Max(visible.Left + 8, Math.Min(cardX, visible.Right - cardWidth - 8)),
                    Math.Max(visible.Top + 48, Math.Min(cardY, visible.Bottom - cardHeight - 8)),
                    cardWidth, cardHeight);
                return;
            }
            int width = Math.Max(420, Math.Min(1100, target.Width * 82 / 100));
            int height = 112;
            int x = target.Left + (target.Width - width) / 2;
            // Most meeting products already place their own caption bar at the bottom.
            // Keep the transparent lyric just above that area instead of duplicating
            // text directly on top of the source caption.
            int y = target.Top + target.Height * 52 / 100;
            if (sourceTop >= height + 36)
                y = Math.Min(y, target.Top + sourceTop - height - 12);
            y = Math.Max(target.Top + 24, Math.Min(y, target.Bottom - height - 48));
            Bounds = new Rectangle(x, y, width, height);
        }

        private static double ClampFraction(double value)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) ? 0 :
                Math.Max(0, Math.Min(1, value));
        }

        private void LoadAudioLayout()
        {
            if (String.IsNullOrEmpty(layoutPath) || !File.Exists(layoutPath)) return;
            try
            {
                var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(layoutPath, Encoding.UTF8));
                if (values == null || !values.ContainsKey("width") || !values.ContainsKey("height") ||
                    !values.ContainsKey("x") || !values.ContainsKey("y")) return;
                int width = Convert.ToInt32(values["width"]);
                int height = Convert.ToInt32(values["height"]);
                double x = Convert.ToDouble(values["x"]);
                double y = Convert.ToDouble(values["y"]);
                if (width < MinAudioWidth || width > 1200 || height < MinAudioHeight || height > 420)
                    return;
                audioWidth = width;
                audioHeight = height;
                audioX = ClampFraction(x);
                audioY = ClampFraction(y);
                userLayout = true;
            }
            catch { /* Corrupt layout never prevents captions; default remains usable. */ }
        }

        private void SaveAudioLayout()
        {
            if (String.IsNullOrEmpty(layoutPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(layoutPath));
                var values = new Dictionary<string, object> {
                    { "version", 1 }, { "width", audioWidth }, { "height", audioHeight },
                    { "x", audioX }, { "y", audioY }
                };
                File.WriteAllText(layoutPath, new JavaScriptSerializer().Serialize(values),
                    new UTF8Encoding(false));
            }
            catch { /* Keep the current in-memory placement if disk is unavailable. */ }
        }

        private void SetAudioBounds(Rectangle desired)
        {
            int width = Math.Max(Math.Min(MinAudioWidth, audioTarget.Width - 24),
                Math.Min(desired.Width, Math.Max(180, audioTarget.Width - 24)));
            int height = Math.Max(Math.Min(MinAudioHeight, audioTarget.Height - 72),
                Math.Min(desired.Height, Math.Max(110, audioTarget.Height - 72)));
            int leftMin = audioTarget.Left + 8;
            int topMin = audioTarget.Top + 48;
            int leftMax = Math.Max(leftMin, audioTarget.Right - width - 8);
            int topMax = Math.Max(topMin, audioTarget.Bottom - height - 8);
            int left = Math.Max(leftMin, Math.Min(desired.Left, leftMax));
            int top = Math.Max(topMin, Math.Min(desired.Top, topMax));
            Bounds = new Rectangle(left, top, width, height);
            audioWidth = width;
            audioHeight = height;
            audioX = leftMax == leftMin ? 0 : ClampFraction((double)(left - leftMin) / (leftMax - leftMin));
            audioY = topMax == topMin ? 0 : ClampFraction((double)(top - topMin) / (topMax - topMin));
            userLayout = true;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (!audioBox || args.Button != MouseButtons.Left) return;
            if (args.Y < 30 && args.X >= Width - 92)
            {
                if (HistoryRequested != null) HistoryRequested();
                return;
            }
            resizingAudio = args.X >= Width - 28 && args.Y >= Height - 28;
            draggingAudio = !resizingAudio && args.Y < 32;
            if (!draggingAudio && !resizingAudio) return;
            audioMoved = false;
            mouseStart = Cursor.Position;
            boundsStart = Bounds;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            if (!audioBox) return;
            if (!draggingAudio && !resizingAudio)
            {
                Cursor = args.X >= Width - 28 && args.Y >= Height - 28
                    ? Cursors.SizeNWSE : args.Y < 32 && args.X < Width - 92
                    ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            Point cursor = Cursor.Position;
            int dx = cursor.X - mouseStart.X;
            int dy = cursor.Y - mouseStart.Y;
            if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
            Rectangle desired = resizingAudio
                ? new Rectangle(boundsStart.Left, boundsStart.Top,
                    boundsStart.Width + dx, boundsStart.Height + dy)
                : new Rectangle(boundsStart.Left + dx, boundsStart.Top + dy,
                    boundsStart.Width, boundsStart.Height);
            SetAudioBounds(desired);
            audioMoved = true;
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (!draggingAudio && !resizingAudio) return;
            draggingAudio = resizingAudio = false;
            Capture = false;
            if (audioMoved) SaveAudioLayout();
        }

        private Rectangle AudioCurrentBounds()
        {
            int top = String.IsNullOrEmpty(previousLine) ? 38 : 38 + (Height - 46) / 2;
            return new Rectangle(15, top, Math.Max(1, Width - 30), Math.Max(1, Height - top - 8));
        }

        private float AudioFontSize()
        {
            return Math.Max(18f, Math.Min(29f, Math.Min(Width / 32f, Height / 7f)));
        }

        public bool TryGetCurrentRange(int start, int length, out Rectangle screen)
        {
            screen = Rectangle.Empty;
            if (audioBox) start -= currentSourceOffset;
            if (start < 0 || length <= 0 || start + length > currentLine.Length || !Visible) return false;
            Rectangle bounds = audioBox
                ? AudioCurrentBounds()
                : new Rectangle(8, 48, Math.Max(1, Width - 16), 57);
            using (Graphics graphics = CreateGraphics())
            using (Font font = new Font("Microsoft YaHei UI", audioBox ? AudioFontSize() : 25f,
                       FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = audioBox ? StringAlignment.Near : StringAlignment.Center;
                format.LineAlignment = audioBox ? StringAlignment.Near : StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.SetMeasurableCharacterRanges(new[] { new CharacterRange(start, length) });
                Region[] regions = graphics.MeasureCharacterRanges(currentLine, font, bounds, format);
                if (regions.Length == 0) return false;
                RectangleF measured = regions[0].GetBounds(graphics);
                foreach (Region region in regions) region.Dispose();
                if (measured.Width < 1 || measured.Height < 1) return false;
                Rectangle client = Rectangle.Ceiling(measured);
                client.Inflate(2, 1);
                screen = RectangleToScreen(client);
                return true;
            }
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            args.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (audioBox)
            {
                using (GraphicsPath card = new GraphicsPath())
                using (Brush background = new SolidBrush(Color.FromArgb(224, 25, 30, 39)))
                using (Brush heading = new SolidBrush(Color.FromArgb(175, 215, 225, 238)))
                using (Brush older = new SolidBrush(Color.FromArgb(195, 225, 230, 237)))
                using (Brush newest = new SolidBrush(Color.White))
                using (Font headingFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular,
                           GraphicsUnit.Pixel))
                using (Font lineFont = new Font("Microsoft YaHei UI", AudioFontSize(), FontStyle.Regular,
                           GraphicsUnit.Pixel))
                using (StringFormat format = new StringFormat())
                {
                    card.AddRectangle(new Rectangle(0, 0, Width - 1, Height - 1));
                    args.Graphics.FillPath(background, card);
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    args.Graphics.DrawString("实时字幕 · 拖动标题移动 / 右下角缩放", headingFont, heading,
                        new RectangleF(14, 8, Width - 114, 20), format);
                    args.Graphics.DrawString("记录 ›", headingFont, newest,
                        new RectangleF(Width - 88, 8, 80, 20), format);
                    args.Graphics.DrawString(previousLine, lineFont, older,
                        new RectangleF(14, 34, Width - 28,
                            String.IsNullOrEmpty(previousLine) ? 0 : (Height - 46) / 2), format);
                    args.Graphics.DrawString(currentLine, lineFont, newest,
                        AudioCurrentBounds(), format);
                    args.Graphics.DrawLine(Pens.LightGray, Width - 20, Height - 8,
                        Width - 8, Height - 20);
                    args.Graphics.DrawLine(Pens.LightGray, Width - 13, Height - 8,
                        Width - 8, Height - 13);
                }
                return;
            }
            Rectangle previousBounds = new Rectangle(8, 6, Math.Max(1, Width - 16), 42);
            Rectangle currentBounds = new Rectangle(8, 48, Math.Max(1, Width - 16), 57);
            DrawOutlinedText(args.Graphics, previousLine, previousBounds, 18f,
                Color.FromArgb(210, 225, 225, 225), 3.2f);
            DrawOutlinedText(args.Graphics, currentLine, currentBounds, 25f,
                Color.White, 4.2f);
        }

        private static void DrawOutlinedText(
            Graphics graphics,
            string text,
            Rectangle bounds,
            float size,
            Color fill,
            float outline)
        {
            if (String.IsNullOrWhiteSpace(text))
                return;
            using (FontFamily family = new FontFamily("Microsoft YaHei UI"))
            using (StringFormat format = new StringFormat())
            using (GraphicsPath path = new GraphicsPath())
            using (Pen border = new Pen(Color.FromArgb(235, 18, 18, 18), outline) {
                LineJoin = LineJoin.Round
            })
            using (Brush brush = new SolidBrush(fill))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                float fittedSize = size;
                using (Font probe = new Font(family, fittedSize, FontStyle.Bold, GraphicsUnit.Point))
                {
                    SizeF measured = graphics.MeasureString(text, probe, Int32.MaxValue, format);
                    if (measured.Width > bounds.Width - 12)
                        fittedSize = Math.Max(14f, fittedSize * (bounds.Width - 12) / measured.Width);
                }
                float emSize = graphics.DpiY * fittedSize / 72f;
                path.AddString(text, family, (int)FontStyle.Bold, emSize, bounds, format);
                graphics.DrawPath(border, path);
                graphics.FillPath(brush, path);
            }
        }
    }

    internal sealed class CaptionHistoryForm : Form
    {
        public Func<string, string, LookupResponse> Lookup;
        public Func<string, CaptionTranslationResponse> Translate;
        public Func<string, AnalyzeResponse> Analyze;
        public Func<DateTime, List<CaptionEntry>> ArchiveDateRequested;
        public Func<DateTime, CaptionArchiveReadResult> ArchiveStatusRequested;
        public Func<DateTime, CaptionArchiveCursor, CaptionArchiveReadResult> ArchivePageRequested;
        public Func<DateTime, int> DeleteDateRequested;
        public Func<List<CaptionEntry>> SessionEntriesRequested;
        public bool ArchiveEnabled = true;
        private bool viewingSession;
        private int archiveGeneration;
        private bool archiveLoading;
        private readonly List<CaptionEntry> pendingLive = new List<CaptionEntry>();
        private readonly List<CaptionArchiveCursor> pageStarts = new List<CaptionArchiveCursor>();
        private CaptionArchiveCursor nextPage;
        private readonly Button previousPageButton = new Button { Text = "上一页", AutoSize = true };
        private readonly Button nextPageButton = new Button { Text = "下一页", AutoSize = true };
        private string archiveWarning = String.Empty;
        private List<CaptionEntry> visibleEntries = new List<CaptionEntry>();
        public event Action<HighlightItem> EditTaskRequested;
        private readonly Button taskButton = new Button();
        private int taskVersion;
        private readonly FlowLayoutPanel explanationPanel = new FlowLayoutPanel();
        private readonly LinkLabel explanation = new LinkLabel();
        private readonly Button lookupButton = new Button();
        private readonly Button translateButton = new Button();
        private readonly Button backButton = new Button();
        private readonly List<LookupResponse> history = new List<LookupResponse>();
        private LookupResponse current;
        private int lookupVersion;
        private int translationVersion;
        private readonly RichTextBox transcript;
        private readonly DateTimePicker archiveDatePicker = new DateTimePicker();
        private readonly Label notice = new Label();
        private readonly Button copyButton;
        private readonly Button clearButton;
        private readonly Button exportButton;
        public event Action ClearRequested;

        public CaptionHistoryForm()
        {
            Text = "字幕记录 · 按日期回看";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 520);
            MinimumSize = new Size(520, 340);
            Font = new Font("Microsoft YaHei UI", 10f);
            ShowInTaskbar = true;

            notice.Dock = DockStyle.Top;
            notice.Height = 42;
            notice.Padding = new Padding(10, 10, 10, 4);
            notice.Text = "字幕按录制日期自动保存在本机；选词查解释或按需翻译。";

            FlowLayoutPanel dateBar = new FlowLayoutPanel();
            dateBar.Dock = DockStyle.Top;
            dateBar.Height = 43;
            dateBar.Padding = new Padding(9, 7, 0, 0);
            dateBar.WrapContents = true;
            SizeChanged += delegate { dateBar.Height = ClientSize.Width < 680 ? 96 : 43; };
            dateBar.Controls.Add(new Label { Text = "录制日期", AutoSize = true,
                Margin = new Padding(0, 5, 8, 0) });
            archiveDatePicker.Format = DateTimePickerFormat.Custom;
            archiveDatePicker.CustomFormat = "yyyy-MM-dd";
            archiveDatePicker.Width = 156;
            archiveDatePicker.MaxDate = DateTime.Today;
            archiveDatePicker.Value = DateTime.Today;
            archiveDatePicker.ValueChanged += delegate { viewingSession = false; ResetQueryView(); RefreshArchiveDate(); };
            dateBar.Controls.Add(archiveDatePicker);
            Button currentSession = new Button { Text = "本次字幕", AutoSize = true };
            currentSession.Click += delegate {
                archiveGeneration++;
                archiveLoading = false;
                pendingLive.Clear();
                previousPageButton.Enabled = nextPageButton.Enabled = false;
                viewingSession = true;
                ResetQueryView();
                if (SessionEntriesRequested != null) SetEntries(SessionEntriesRequested());
                notice.Text = "正在查看本次运行的字幕；关闭保存后，这些内容不会写入历史。";
            };
            Button deleteDate = new Button { Text = "删除当天历史", AutoSize = true };
            deleteDate.Click += delegate {
                if (DeleteDateRequested == null) return;
                DateTime date = archiveDatePicker.Value.Date;
                if (MessageBox.Show(this, "删除 " + date.ToString("yyyy-MM-dd") +
                    " 的全部本地字幕文件？删除后无法恢复；继续录制只保存后续新内容。",
                    "删除字幕历史", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try {
                    int count = DeleteDateRequested(date);
                    RefreshArchiveDate(true);
                    notice.Text = "已删除 " + count + " 个当天字幕文件。";
                } catch {
                    notice.Text = "删除未全部完成，部分文件可能已删除；请检查磁盘权限。";
                }
            };
            dateBar.Controls.Add(currentSession);
            dateBar.Controls.Add(deleteDate);
            Button previousDay = new Button { Text = "前一天", AutoSize = true };
            Button nextDay = new Button { Text = "后一天", AutoSize = true };
            previousDay.Click += delegate { archiveDatePicker.Value = archiveDatePicker.Value.Date.AddDays(-1); };
            nextDay.Click += delegate {
                if (archiveDatePicker.Value.Date < DateTime.Today)
                    archiveDatePicker.Value = archiveDatePicker.Value.Date.AddDays(1);
            };
            dateBar.Controls.Add(previousDay);
            dateBar.Controls.Add(nextDay);
            previousPageButton.Enabled = nextPageButton.Enabled = false;
            previousPageButton.Click += delegate {
                if (archiveLoading || pageStarts.Count < 2) return;
                pageStarts.RemoveAt(pageStarts.Count - 1);
                ResetQueryView();
                LoadArchivePage(false, pageStarts[pageStarts.Count - 1]);
            };
            nextPageButton.Click += delegate {
                if (archiveLoading || nextPage == null) return;
                pageStarts.Add(nextPage);
                ResetQueryView();
                LoadArchivePage(false, nextPage);
            };
            dateBar.Controls.Add(previousPageButton);
            dateBar.Controls.Add(nextPageButton);
            dateBar.Height = ClientSize.Width < 850 ? 96 : 43;
            SizeChanged += delegate { dateBar.Height = ClientSize.Width < 850 ? 96 : 43; };

            transcript = new RichTextBox();
            transcript.Dock = DockStyle.Fill;
            transcript.ReadOnly = true;
            transcript.BackColor = Color.White;
            transcript.BorderStyle = BorderStyle.FixedSingle;
            transcript.DetectUrls = true;
            transcript.HideSelection = false;
            lookupButton.Text = "解释选中文字";
            lookupButton.AutoSize = true;
            lookupButton.Enabled = false;
            transcript.SelectionChanged += delegate
            {
                int length = transcript.SelectedText.Trim().Length;
                lookupButton.Enabled = length > 0 && length <= 80;
                taskButton.Enabled = length > 0;
            };
            lookupButton.Click += async delegate
            {
                string term = transcript.SelectedText.Trim();
                if (term.Length == 0 || term.Length > 80) return;
                string context = SelectedContext(500);
                history.Clear();
                current = null;
                await LookupTerm(term, context);
            };
            translateButton.Text = "翻译当前句/选中段";
            translateButton.AutoSize = true;
            translateButton.Click += async delegate { await TranslateCurrentCaption(); };
            explanationPanel.Dock = DockStyle.Bottom;
            explanationPanel.Height = 160;
            explanationPanel.Visible = false;
            explanationPanel.AutoScroll = true;
            explanation.AutoSize = true;
            explanation.MaximumSize = new Size(650, 0);
            explanation.Padding = new Padding(10);
            explanationPanel.Controls.Add(explanation);
            explanationPanel.SizeChanged += delegate
            {
                explanation.MaximumSize = new Size(Math.Max(200, explanationPanel.ClientSize.Width - 30), 0);
            };
            explanation.LinkClicked += async delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                if (current == null || history.Count >= 3) return;
                string context = current.explanation;
                history.Add(current);
                await LookupTerm((string)args.Link.LinkData, context);
            };
            backButton.Text = "返回上个解释";
            backButton.AutoSize = true;
            backButton.Enabled = false;
            backButton.Click += delegate
            {
                if (history.Count == 0) return;
                lookupVersion++;
                current = history[history.Count - 1];
                history.RemoveAt(history.Count - 1);
                RenderExplanation();
            };
            VisibleChanged += delegate
            {
                if (!Visible) { lookupVersion++; translationVersion++; taskVersion++; }
            };
            taskButton.Text = "从所选内容设置提醒";
            taskButton.AutoSize = true;
            taskButton.Enabled = false;
            taskButton.Click += async delegate
            {
                string source = SelectedContext(2000);
                if (String.IsNullOrWhiteSpace(source)) return;
                int version = ++taskVersion;
                lookupVersion++;
                translationVersion++;
                taskButton.Enabled = false;
                ShowQueryDetails();
                explanation.Links.Clear();
                explanation.Text = "正在识别这句话的日程…";
                try
                {
                    if (Analyze == null) throw new InvalidOperationException();
                    AnalyzeResponse response = await Task.Factory.StartNew(() => Analyze(source));
                    if (version != taskVersion || !Visible || IsDisposed) return;
                    var actions = response == null ? null : response.actions;
                    if (actions == null || actions.Count == 0)
                    {
                        explanation.Text = "没有识别到明确的日程安排。请选择包含时间和行动的完整句子后重试。";
                        return;
                    }
                    if (actions.Count > 1)
                    {
                        explanation.Text = "检测到多个安排，请只选中要处理的那一句后重试。";
                        return;
                    }
                    AnalysisEntity action = actions[0];
                    explanation.Text = "已识别安排，尚未创建提醒。请补齐并确认。";
                    if (EditTaskRequested != null) EditTaskRequested(new HighlightItem {
                        term = action.text, time_text = action.time_text, title = action.title,
                        start_iso = action.start_iso, end_iso = action.end_iso,
                        utc_offset = action.utc_offset, kind = "task", needs_confirmation = true
                    });
                }
                catch (Exception)
                {
                    if (version == taskVersion && Visible && !IsDisposed)
                        explanation.Text = "日程识别失败，请确认服务连接后重试。";
                }
                finally { if (!IsDisposed) taskButton.Enabled = transcript.SelectionLength > 0; }
            };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 112;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Padding = new Padding(8);

            Button closeButton = new Button { Text = "关闭", AutoSize = true };
            clearButton = new Button { Text = "清空本次显示", AutoSize = true };
            copyButton = new Button { Text = "复制全部", AutoSize = true };
            exportButton = new Button { Text = "导出文本…", AutoSize = true };
            exportButton.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(transcript.Text)) return;
                using (SaveFileDialog dialog = new SaveFileDialog())
                {
                    dialog.Title = "保存字幕记录";
                    dialog.Filter = "文本文件 (*.txt)|*.txt";
                    dialog.DefaultExt = "txt";
                    dialog.AddExtension = true;
                    dialog.OverwritePrompt = true;
                    dialog.FileName = "字幕记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        File.WriteAllText(dialog.FileName, transcript.Text, new UTF8Encoding(true));
                        MessageBox.Show(this, "字幕记录已保存。", "字幕记录");
                    }
                    catch (Exception error)
                    {
                        MessageBox.Show(this, "保存失败，记录仍保留在窗口中：" + error.GetType().Name,
                            "字幕记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            };
            closeButton.Click += delegate { Hide(); };
            clearButton.Click += delegate
            {
                if (ClearRequested != null)
                    ClearRequested();
            };
            copyButton.Click += delegate
            {
                if (!String.IsNullOrWhiteSpace(transcript.Text))
                    Clipboard.SetText(transcript.Text);
            };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(exportButton);
            buttons.Controls.Add(lookupButton);
            buttons.Controls.Add(translateButton);
            buttons.Controls.Add(backButton);
            buttons.Controls.Add(taskButton);
            SizeChanged += delegate { buttons.Height = ClientSize.Width < 680 ? 150 : 112; };

            Controls.Add(transcript);
            Controls.Add(explanationPanel);
            Controls.Add(buttons);
            Controls.Add(notice);
            Controls.Add(dateBar);
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (args.CloseReason == CloseReason.UserClosing)
                {
                    args.Cancel = true;
                    archiveGeneration++;
                    archiveLoading = false;
                    pendingLive.Clear();
                    Hide();
                }
            };
        }

        public void Present(List<CaptionEntry> entries)
        {
            clearButton.Visible = true;
            SetEntries(entries);
            PresentVisible();
        }

        public void PresentArchive()
        {
            // Clearing the bounded in-memory list cannot clear persisted days;
            // hide that legacy action while browsing the saved archive.
            clearButton.Visible = false;
            if (archiveDatePicker.MaxDate.Date < DateTime.Today)
                archiveDatePicker.MaxDate = DateTime.Today;
            if (!Visible && archiveDatePicker.Value.Date != DateTime.Today)
                archiveDatePicker.Value = DateTime.Today;
            RefreshArchiveDate(true);
            PresentVisible();
        }

        public void RefreshArchiveDate(bool evenWhenHidden = false)
        {
            pageStarts.Clear();
            pageStarts.Add(null);
            LoadArchivePage(evenWhenHidden, null);
        }

        private void LoadArchivePage(bool evenWhenHidden, CaptionArchiveCursor cursor)
        {
            if ((ArchiveDateRequested == null && ArchiveStatusRequested == null && ArchivePageRequested == null) ||
                (!evenWhenHidden && !Visible)) return;
            int version = ++archiveGeneration;
            viewingSession = false;
            DateTime date = archiveDatePicker.Value.Date;
            Text = "字幕记录 · " + date.ToString("yyyy-MM-dd");
            archiveLoading = true;
            pendingLive.Clear();
            nextPage = null;
            previousPageButton.Enabled = nextPageButton.Enabled = false;
            SetEntries(new List<CaptionEntry>());
            notice.Text = "正在读取这一天的字幕…";
            if (!ArchiveEnabled && date == DateTime.Today && SessionEntriesRequested != null)
            {
                SetEntries(SessionEntriesRequested());
                archiveLoading = false;
                notice.Text = "字幕保存已关闭；当前显示本次运行内容。以前的历史仍可选择日期回看。";
                return;
            }
            var readPage = ArchivePageRequested;
            var readStatus = ArchiveStatusRequested;
            var readDate = ArchiveDateRequested;
            // Ensure a UI-thread handle before starting work; do not depend on
            // ambient SynchronizationContext (diagnostics and hidden tray forms
            // may not have a running Application.Run yet).
            IntPtr uiHandle = Handle;
            Task.Factory.StartNew(delegate {
                CaptionArchiveReadResult result = null;
                bool failed = false;
                try {
                    result = readPage != null ? readPage(date, cursor) :
                        (readStatus != null ? readStatus(date) : new CaptionArchiveReadResult { Entries = readDate(date) });
                } catch { failed = true; }
                if (IsDisposed || !IsHandleCreated) return;
                try {
                    BeginInvoke((Action)delegate { ApplyArchiveRead(result, failed, version, date, readPage != null); });
                } catch (InvalidOperationException) { /* Form was disposed while the read completed. */ }
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }

        private void ApplyArchiveRead(CaptionArchiveReadResult result, bool failed, int version, DateTime date, bool paged)
        {
            if (IsDisposed || version != archiveGeneration || viewingSession || date != archiveDatePicker.Value.Date) return;
            archiveLoading = false;
            var buffered = new List<CaptionEntry>(pendingLive);
            pendingLive.Clear();
            if (failed || result == null)
            {
                previousPageButton.Enabled = pageStarts.Count > 1;
                notice.Text = "这一天的字幕记录暂时无法读取；本次内存中的字幕仍可使用。";
                return;
            }
            SetEntries(result.Entries);
            nextPage = result.Next;
            if (result.Incomplete) archiveWarning = "历史未完整读取：" + result.SkippedFiles +
                " 个文件、" + result.SkippedLines + " 行跳过" +
                (result.Truncated ? "；仅显示最近 20000 条" : "") + "。当前显示可读取部分。";
            if (paged) archiveWarning += (archiveWarning.Length > 0 ? " " : "") +
                "第 " + pageStarts.Count + " 页（从早到晚）" +
                (nextPage != null ? "；还有记录，请点下一页。" : "；已到最后一页。");
            if (archiveWarning.Length > 0) notice.Text = archiveWarning;
            previousPageButton.Enabled = pageStarts.Count > 1;
            nextPageButton.Enabled = nextPage != null;
            foreach (var entry in buffered)
                if (!visibleEntries.Any(value => value.timestamp == entry.timestamp && value.text == entry.text &&
                    value.session_key == entry.session_key)) AppendLiveEntry(entry, date, true);
        }

        private void PresentVisible()
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            if (!Visible)
            {
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2),
                    area.Top + Math.Max(0, (area.Height - Height) / 2));
                Show();
            }
            // The tray closes after its Click event. The caller defers this
            // presentation so the menu cannot immediately hide the new window.
            TopMost = true;
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost,
                Left, Top, Width, Height, NativeMethods.SwpShowWindow);
            BringToFront();
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
        }

        private string SelectedContext(int limit)
        {
            string value = transcript.Text;
            int position = transcript.SelectionStart;
            int from = position == 0 ? 0 : value.LastIndexOf('\n', position - 1) + 1;
            int to = value.IndexOf('\n', Math.Min(value.Length, position + transcript.SelectionLength));
            if (to < 0) to = value.Length;
            if (to - from > limit) from = Math.Max(from, position - Math.Max(0, (limit - transcript.SelectionLength) / 2));
            string selected = value.Substring(from, Math.Min(limit, to - from));
            return System.Text.RegularExpressions.Regex.Replace(selected,
                @"(?m)^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] ", "");
        }

        private string TranslationSource()
        {
            string selected = transcript.SelectedText.Trim();
            string source = selected;
            if (source.Length == 0)
            {
                string value = transcript.Text;
                int position = Math.Min(transcript.SelectionStart, value.Length);
                int from = position == 0 ? 0 : value.LastIndexOf('\n', position - 1) + 1;
                int to = value.IndexOf('\n', position);
                if (to < 0) to = value.Length;
                source = value.Substring(from, to - from);
            }
            return System.Text.RegularExpressions.Regex.Replace(source,
                @"(?m)^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] ", "").Trim();
        }

        private async Task TranslateCurrentCaption()
        {
            ShowQueryDetails();
            string source = TranslationSource();
            if (source.Length == 0 || source.Length > 1000)
            {
                explanation.Links.Clear();
                explanation.Text = "请选取不超过 1000 个字符的一条或一段字幕再翻译。";
                return;
            }
            int version = ++translationVersion;
            lookupVersion++;
            taskVersion++;
            history.Clear();
            current = null;
            backButton.Enabled = false;
            translateButton.Enabled = false;
            explanation.Links.Clear();
            explanation.Text = "正在翻译所选字幕…";
            try
            {
                if (Translate == null) throw new InvalidOperationException();
                CaptionTranslationResponse response = await Task.Factory.StartNew(() => Translate(source));
                if (version != translationVersion || !Visible || IsDisposed) return;
                explanation.Text = response == null || String.IsNullOrWhiteSpace(response.translation)
                    ? "暂时没有可靠译文，请稍后重试。"
                    : "AI 译文（请核对）：\n" + response.translation;
            }
            catch (Exception)
            {
                if (version == translationVersion && Visible && !IsDisposed)
                    explanation.Text = "翻译失败，请检查模型服务后重试。原文仍保留在上方。";
            }
            finally
            {
                if (!IsDisposed && version == translationVersion)
                    translateButton.Enabled = transcript.TextLength > 0;
            }
        }

        private async Task LookupTerm(string term, string context)
        {
            ShowQueryDetails();
            taskVersion++;
            translationVersion++;
            int version = ++lookupVersion;
            explanation.Links.Clear();
            explanation.Text = "正在解释“" + term + "”…";
            backButton.Enabled = history.Count > 0;
            try
            {
                if (Lookup == null) throw new InvalidOperationException();
                if (context.Length > 500) context = context.Substring(0, 500);
                LookupResponse response = await Task.Factory.StartNew(() => Lookup(term, context));
                if (version != lookupVersion || IsDisposed || !Visible) return;
                current = response;
                RenderExplanation();
            }
            catch (Exception)
            {
                if (version == lookupVersion && !IsDisposed && Visible)
                    explanation.Text = "暂时无法查询，请稍后重新点击解释选中文字。";
            }
        }

        private void RenderExplanation()
        {
            explanation.Links.Clear();
            string body = current == null ? null : current.explanation;
            explanation.Text = String.IsNullOrWhiteSpace(body) ? "暂时没有可靠解释，请重试。" : body;
            if (current != null && current.entities != null && history.Count < 3 && body != null)
            {
                foreach (AnalysisEntity entity in current.entities)
                {
                    if (entity.start >= 0 && entity.end > entity.start && entity.end <= body.Length &&
                        body.Substring(entity.start, entity.end - entity.start) == entity.text)
                        explanation.Links.Add(entity.start, entity.end - entity.start, entity.text);
                }
            }
            backButton.Enabled = history.Count > 0;
        }

        private void ResetQueryView()
        {
            lookupVersion++; translationVersion++; taskVersion++;
            history.Clear(); current = null;
            explanation.Links.Clear(); explanation.Text = String.Empty;
            explanationPanel.Visible = false;
            backButton.Enabled = false;
            lookupButton.Enabled = taskButton.Enabled = false;
            transcript.DeselectAll();
        }

        private void ShowQueryDetails()
        {
            explanationPanel.Visible = true;
            Height = Math.Max(Height, Math.Min(650, Screen.FromControl(this).WorkingArea.Height));
        }

        public void AppendLiveEntry(CaptionEntry entry, DateTime archiveDate, bool saved)
        {
            if (!Visible || entry == null) return;
            DateTime shownDay = ArchiveEnabled ? archiveDate.Date : DateTime.Today;
            if (!viewingSession && (archiveDatePicker.Value.Date != shownDay || (ArchiveEnabled && !saved))) return;
            if (archiveLoading) {
                if (pendingLive.Count == 2000) pendingLive.RemoveAt(0);
                pendingLive.Add(entry);
                return;
            }
            if (!viewingSession && nextPage != null) return;
            int limit = !viewingSession && ArchivePageRequested != null ? 2000 : 20000;
            if (visibleEntries.Count >= limit || (visibleEntries.Count > 0 &&
                entry.timestamp < visibleEntries[visibleEntries.Count - 1].timestamp)) {
                string warning = archiveWarning;
                var next = new List<CaptionEntry>(visibleEntries); next.Add(entry);
                SetEntries(next.OrderBy(value => value.timestamp).Skip(Math.Max(0, next.Count - limit)).ToList());
                archiveWarning = warning + (next.Count > limit && !warning.Contains("当前仅显示最近") ?
                    " 当前仅显示最近 " + limit + " 条；前面的记录仍保存在本机。" : "");
                if (archiveWarning.Length > 0) notice.Text = archiveWarning;
                return;
            }
            string session = visibleEntries.Count == 0 ? null : visibleEntries[visibleEntries.Count - 1].session_key;
            StringBuilder added = new StringBuilder();
            if (!String.IsNullOrEmpty(entry.session_key) && session != entry.session_key) {
                if (transcript.TextLength > 0) added.AppendLine().AppendLine();
                added.Append("【").Append(entry.session_label ?? "会议字幕").Append("】");
            }
            if (transcript.TextLength > 0 || added.Length > 0) added.AppendLine();
            if (entry.is_gap) added.Append("⚠ ");
            added.Append('[').Append(entry.timestamp.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").Append(entry.text);
            if (!entry.is_gap && !String.IsNullOrWhiteSpace(entry.raw_text) && entry.raw_text != entry.text)
                added.AppendLine().Append("    ↳ 原始识别：").Append(entry.raw_text);
            visibleEntries.Add(entry);
            int gaps = visibleEntries.Count(value => value.is_gap);
            notice.Text = "共 " + (visibleEntries.Count - gaps) + " 条字幕、" + gaps + " 处缺口；选词查解释或按需翻译。";
            if (archiveWarning.Length > 0) notice.Text += " " + archiveWarning;
            translateButton.Enabled = visibleEntries.Count > gaps;
            ApplyTranscriptText(transcript.Text + added.ToString());
        }

        public void SetEntries(List<CaptionEntry> entries)
        {
            if (entries == null) entries = new List<CaptionEntry>();
            archiveWarning = String.Empty;
            visibleEntries = entries.Where(entry => entry != null).OrderBy(entry => entry.timestamp).ToList();
            entries = visibleEntries;
            int gapCount = entries.Count(entry => entry != null && entry.is_gap);
            notice.Text = entries.Count == 0
                ? "这一天暂无已保存的字幕。录制成功的字幕会自动归档在本机。"
                : "共 " + (entries.Count - gapCount) + " 条字幕、" + gapCount +
                    " 处缺口；向上滚动可看更早的场次。选词查解释或按需翻译。";
            if (entries.Count == 0)
            {
                lookupVersion++;
                translationVersion++;
                taskVersion++;
                history.Clear();
                current = null;
                explanation.Links.Clear();
                explanation.Text = "";
                backButton.Enabled = false;
            }
            StringBuilder builder = new StringBuilder();
            string shownSession = null;
            foreach (CaptionEntry entry in entries.OrderBy(item => item.timestamp))
            {
                if (!String.IsNullOrEmpty(entry.session_key) &&
                    !String.Equals(shownSession, entry.session_key, StringComparison.Ordinal))
                {
                    if (builder.Length > 0) builder.AppendLine().AppendLine();
                    builder.Append("【").Append(entry.session_label ?? "会议字幕").Append("】");
                    shownSession = entry.session_key;
                }
                if (builder.Length > 0)
                    builder.AppendLine();
                if (entry.is_gap) builder.Append("⚠ ");
                builder.Append('[');
                builder.Append(entry.timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                builder.Append("] ");
                builder.Append(entry.text);
                if (!entry.is_gap && !String.IsNullOrWhiteSpace(entry.raw_text) &&
                    !String.Equals(entry.raw_text, entry.text, StringComparison.Ordinal))
                    builder.AppendLine().Append("    ↳ 原始识别：").Append(entry.raw_text);
            }
            string updated = builder.ToString();
            copyButton.Enabled = clearButton.Enabled = exportButton.Enabled = updated.Length > 0;
            translateButton.Enabled = entries.Count > gapCount;
            ApplyTranscriptText(updated);
        }

        private void ApplyTranscriptText(string updated)
        {
            if (updated == transcript.Text) return;
            string previous = transcript.Text;
            bool preserveReading = Visible && transcript.IsHandleCreated && previous.Length > 0;
            int selectionStart = transcript.SelectionStart;
            int selectionLength = transcript.SelectionLength;
            int firstVisible = preserveReading
                ? NativeMethods.SendMessage(transcript.Handle,
                    NativeMethods.EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32()
                : 0;
            int bottomChar = preserveReading ? transcript.GetCharIndexFromPosition(
                new Point(2, Math.Max(2, transcript.ClientSize.Height - 3))) : 0;
            bool followBottom = !preserveReading || (selectionLength == 0 &&
                transcript.GetLineFromCharIndex(bottomChar) >=
                transcript.GetLineFromCharIndex(previous.Length) - 1);
            if (updated.StartsWith(previous, StringComparison.Ordinal))
                transcript.AppendText(updated.Substring(previous.Length));
            else
                transcript.Text = updated;
            if (followBottom)
            {
                transcript.Select(transcript.TextLength, 0);
                transcript.ScrollToCaret();
            }
            else
            {
                int start = Math.Min(selectionStart, transcript.TextLength);
                transcript.Select(start, Math.Min(selectionLength, transcript.TextLength - start));
                int nowFirst = NativeMethods.SendMessage(transcript.Handle,
                    NativeMethods.EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
                NativeMethods.SendMessage(transcript.Handle, NativeMethods.EmLineScroll,
                    IntPtr.Zero, new IntPtr(firstVisible - nowFirst));
            }
            copyButton.Enabled = transcript.TextLength > 0;
            clearButton.Enabled = transcript.TextLength > 0;
            exportButton.Enabled = transcript.TextLength > 0;
        }
    }

}
