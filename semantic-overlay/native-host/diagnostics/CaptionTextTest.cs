using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class CaptionTextTest
    {
        [STAThread]
        public static int Main()
        {
            if (OverlayContext.TrimAudioOverlap("with your family", "Your family and drive.", true) != "and drive." ||
                OverlayContext.TrimAudioOverlap("with your family", "Your family and drive.", false) != "Your family and drive." ||
                OverlayContext.TrimAudioOverlap("yes", "Yes yes please.", true) != "Yes yes please." ||
                OverlayContext.TrimAudioOverlap("we need snacks", "We have monkeys.", true) != "We have monkeys." ||
                OverlayContext.TrimAudioOverlap("现在讨论这个方案", "这个方案明天开始。", true) != "明天开始。" ||
                OverlayContext.TrimAudioOverlap("好好", "好好看看。", true) != "好好看看。")
                throw new InvalidOperationException("Audio overlap merge lost new or deliberately repeated words.");
            if (!OverlayContext.CanMergeAudioChunk(true, 2, 1) ||
                OverlayContext.CanMergeAudioChunk(false, 2, 1) ||
                OverlayContext.CanMergeAudioChunk(true, 3, 1) ||
                OverlayContext.CanMergeAudioChunk(true, 1, -1))
                throw new InvalidOperationException("Overlap merge crossed a missing chunk or new session.");
            string first = OverlayContext.NormalizeTranscriptIdentity("One API，讨论会！");
            string second = OverlayContext.NormalizeTranscriptIdentity("one api 讨论会");
            if (!String.Equals(first, second, StringComparison.Ordinal))
                throw new InvalidOperationException("Formatting-only transcript changes were not normalized.");
            if (OverlayContext.NormalizeTranscriptIdentity("，。！？ …").Length != 0)
                throw new InvalidOperationException("Punctuation-only transcript was accepted.");
            DateTime chunkTime = DateTime.Now;
            if (!OverlayContext.IsRepeatedAudioResult("YES", chunkTime, "YES", chunkTime) ||
                OverlayContext.IsRepeatedAudioResult("YES", chunkTime.AddSeconds(2), "YES", chunkTime))
                throw new InvalidOperationException("Distinct repeated speech was discarded or same-chunk replay accepted.");
            var speech = OverlayContext.SplitCaptionSpeech(
                "  First sentence. Second sentence! 这是第三句。 版本 3.5 仍正常");
            if (speech.Count != 4 || speech[0].Text != "First sentence." ||
                speech[1].Text != "Second sentence!" ||
                speech[2].Text != "这是第三句。" ||
                speech[3].Text != "版本 3.5 仍正常" ||
                speech[1].Offset != 18 || speech[3].Offset != 42)
                throw new InvalidOperationException("Speech segments or source offsets are incorrect.");
            // Baseline tolerance must not be a non-transitive sorting comparator.
            var buildCaption = typeof(OverlayContext).GetMethod("BuildCaptionText",
                BindingFlags.Static | BindingFlags.NonPublic);
            var firstWord = new OcrWord { text = "one", x = 30, y = 0, w = 10, h = 20 };
            var secondWord = new OcrWord { text = "two", x = 20, y = 6, w = 10, h = 20 };
            var thirdWord = new OcrWord { text = "three", x = 10, y = 12, w = 10, h = 20 };
            var variants = new[] { new[] { firstWord, secondWord, thirdWord },
                new[] { firstWord, thirdWord, secondWord }, new[] { secondWord, firstWord, thirdWord },
                new[] { secondWord, thirdWord, firstWord }, new[] { thirdWord, firstWord, secondWord },
                new[] { thirdWord, secondWord, firstWord } };
            string stable = null;
            foreach (var variant in variants)
            {
                object[] input = new object[] { new System.Collections.Generic.List<OcrWord>(variant), null };
                string rendered = (string)buildCaption.Invoke(null, input);
                if (stable == null) stable = rendered;
                if (rendered != stable || rendered.Length == 0 ||
                    ((System.Collections.Generic.List<OcrWord>)input[1]).Count != 3)
                    throw new InvalidOperationException("OCR baseline ordering depends on input permutation.");
            }
            var backlog = new CaptionAudioBacklog(2);
            DateTime started = new DateTime(2024, 2, 11, 10, 0, 0);
            backlog.Enqueue(new byte[] { 1 }, started);
            CaptionAudioChunk active = backlog.TakeNext();
            active.HasOverlap = true;
            active.Sequence = 42;
            backlog.Enqueue(new byte[] { 2 }, started.AddSeconds(1));
            backlog.Enqueue(new byte[] { 3 }, started.AddSeconds(2));
            CaptionAudioChunk overflow = backlog.Enqueue(new byte[] { 4 }, started.AddSeconds(3));
            if (overflow == null || overflow.Wav[0] != 2 || backlog.PendingCount != 2 ||
                !backlog.HoldForRetry(active))
                throw new InvalidOperationException("Pending overflow or same-chunk retry was lost.");
            CaptionAudioChunk retry = backlog.TakeNext();
            if (!Object.ReferenceEquals(retry, active) || retry.Attempts != 2 ||
                !retry.HasOverlap || retry.Sequence != 42 ||
                backlog.HoldForRetry(retry) || backlog.TakeNext().Wav[0] != 3)
                throw new InvalidOperationException("Retry did not precede later speech or exceed its bound.");
            var ordered = new CaptionOrderedCompletions();
            if (!OverlayContext.CanStartCaptionRequest(1, 1) ||
                OverlayContext.CanStartCaptionRequest(2, 0) ||
                OverlayContext.CanStartCaptionRequest(0, 3))
                throw new Exception("Caption worker or ordered buffer exceeded its bound.");
            var delivered = new System.Collections.Generic.List<int>();
            var firstChunk = new CaptionAudioChunk { Sequence = 1 };
            var secondChunk = new CaptionAudioChunk { Sequence = 2 };
            ordered.Add(secondChunk, delegate { delivered.Add(2); return true; });
            ordered.Drain(delegate { return true; });
            if (delivered.Count != 0 || ordered.Count != 1)
                throw new Exception("Later transcription overtook earlier audio.");
            ordered.Add(firstChunk, delegate { return false; });
            ordered.Drain(delegate { return true; });
            if (delivered.Count != 0 || ordered.Count != 1)
                throw new Exception("Retry failed to retain its source position.");
            ordered.Add(firstChunk, delegate { delivered.Add(1); return true; });
            ordered.Drain(delegate { return true; });
            if (String.Join(",", delivered) != "1,2" || ordered.Count != 0)
                throw new Exception("Retry recovery reordered buffered transcriptions.");
            ordered.Add(new CaptionAudioChunk { Sequence = 4 }, delegate { delivered.Add(4); return true; });
            ordered.Add(new CaptionAudioChunk { Sequence = 3 }, delegate { return true; });
            ordered.Drain(delegate { return true; });
            if (String.Join(",", delivered) != "1,2,4")
                throw new Exception("Explicit dropped segment blocked later audio.");
            ordered.Add(new CaptionAudioChunk { Sequence = 6 }, delegate { throw new Exception("Cancelled result applied"); });
            int cancelled = 0;
            ordered.Clear(delegate(string outcome, int ms, int attempts) { if (outcome == "cancelled") cancelled++; });
            ordered.Drain(delegate { return true; });
            if (cancelled != 1 || ordered.Count != 0) throw new Exception("Buffered result survived session close.");
            Console.WriteLine("caption-ordered-parallel-retry-gap-cancel-ok");
            using (var lyric = new CaptionLyricForm(String.Empty))
            {
                var target = new NativeRect { Left = 100, Top = 80, Right = 1380, Bottom = 800 };
                lyric.ShowAudioLines("First sentence.", "Second sentence!", 18, target);
                if (!NativeMethods.IsWindowVisible(lyric.Handle) ||
                    lyric.Width < target.Width / 2 || lyric.Width > target.Width * 3 / 4 ||
                    lyric.Height < 150 || lyric.Bottom > target.Bottom - 80)
                    throw new InvalidOperationException("Live subtitle box is hidden or covers too much of the meeting.");
                PropertyInfo style = typeof(CaptionLyricForm).GetProperty("CreateParams",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if ((((CreateParams)style.GetValue(lyric, null)).ExStyle &
                     NativeMethods.WsExTransparent) != 0)
                    throw new InvalidOperationException("Audio subtitle is still mouse transparent.");
                bool historyRequested = false;
                lyric.HistoryRequested += delegate { historyRequested = true; };
                typeof(CaptionLyricForm).GetMethod("OnMouseDown",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(lyric, new object[] {
                        new MouseEventArgs(MouseButtons.Left, 1, lyric.Width - 20, 10, 0) });
                if (!historyRequested)
                    throw new InvalidOperationException("Caption history affordance did not respond.");
                MethodInfo mouseDown = typeof(CaptionLyricForm).GetMethod("OnMouseDown",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo mouseMove = typeof(CaptionLyricForm).GetMethod("OnMouseMove",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo mouseUp = typeof(CaptionLyricForm).GetMethod("OnMouseUp",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo mouseStart = typeof(CaptionLyricForm).GetField("mouseStart",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Rectangle beforeDrag = lyric.Bounds;
                mouseDown.Invoke(lyric, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 40, 10, 0) });
                mouseStart.SetValue(lyric, new Point(Cursor.Position.X + 55, Cursor.Position.Y + 35));
                mouseMove.Invoke(lyric, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 40, 10, 0) });
                mouseUp.Invoke(lyric, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 40, 10, 0) });
                if (lyric.Location == beforeDrag.Location)
                    throw new InvalidOperationException("Audio subtitle header drag did not move the box.");
                Rectangle beforeResize = lyric.Bounds;
                mouseDown.Invoke(lyric, new object[] { new MouseEventArgs(MouseButtons.Left, 1,
                    lyric.Width - 10, lyric.Height - 10, 0) });
                mouseStart.SetValue(lyric, new Point(Cursor.Position.X - 60, Cursor.Position.Y - 35));
                mouseMove.Invoke(lyric, new object[] { new MouseEventArgs(MouseButtons.Left, 1,
                    lyric.Width - 10, lyric.Height - 10, 0) });
                mouseUp.Invoke(lyric, new object[] { new MouseEventArgs(MouseButtons.Left, 1,
                    lyric.Width - 10, lyric.Height - 10, 0) });
                if (lyric.Width <= beforeResize.Width || lyric.Height <= beforeResize.Height)
                    throw new InvalidOperationException("Audio subtitle corner drag did not resize the box.");
                typeof(CaptionLyricForm).GetMethod("SetAudioBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(lyric, new object[] {
                        new Rectangle(target.Left + 100, target.Top + 130, 800, 240) });
                Rectangle positioned = lyric.Bounds;
                lyric.PositionFor(target);
                if (Math.Abs(lyric.Left - positioned.Left) > 1 ||
                    Math.Abs(lyric.Top - positioned.Top) > 1 ||
                    lyric.Size != positioned.Size)
                    throw new InvalidOperationException("User subtitle layout was overwritten by tracking.");
                System.Drawing.Rectangle word;
                if (!lyric.TryGetCurrentRange(18, 6, out word) ||
                    lyric.TryGetCurrentRange(0, 5, out word))
                    throw new InvalidOperationException("Boxed caption term coordinates lost the source offset.");
                string preview = Environment.GetEnvironmentVariable("CAPTION_UI_PREVIEW_PATH");
                if (!String.IsNullOrWhiteSpace(preview))
                {
                    using (var bitmap = new System.Drawing.Bitmap(lyric.Width, lyric.Height))
                    {
                        lyric.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(preview, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                lyric.ShowLines("older", "current", target);
                if ((((CreateParams)style.GetValue(lyric, null)).ExStyle &
                     NativeMethods.WsExTransparent) == 0)
                    throw new InvalidOperationException("OCR lyric lost mouse pass-through.");
            }
            string layoutFixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "caption-layout-test.json");
            var layoutTarget = new NativeRect { Left = 50, Top = 60, Right = 1450, Bottom = 900 };
            Rectangle savedBounds;
            using (var lyric = new CaptionLyricForm(layoutFixture))
            {
                lyric.ShowAudioLines("First.", "Second.", 7, layoutTarget);
                typeof(CaptionLyricForm).GetMethod("SetAudioBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(lyric, new object[] {
                        new Rectangle(240, 220, 850, 250) });
                savedBounds = lyric.Bounds;
                typeof(CaptionLyricForm).GetMethod("SaveAudioLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(lyric, null);
            }
            using (var lyric = new CaptionLyricForm(layoutFixture))
            {
                lyric.ShowAudioLines("First.", "Second.", 7, layoutTarget);
                if (Math.Abs(lyric.Left - savedBounds.Left) > 1 ||
                    Math.Abs(lyric.Top - savedBounds.Top) > 1 || lyric.Size != savedBounds.Size)
                    throw new InvalidOperationException("Saved subtitle position or size was not restored.");
            }
            if (!OverlayContext.IsChatProcessName("ChatGPT") ||
                !OverlayContext.IsChatProcessName("WeChat") ||
                OverlayContext.IsChatProcessName("explorer"))
                throw new InvalidOperationException("Conversation-window scan routing is incorrect.");
            Console.WriteLine("caption-text-ok");
            if (TermColor.ForTerm("OneAPI") != TermColor.ForTerm(" one api ") ||
                TermColor.ForTerm("ＯｎｅＡＰＩ") != TermColor.ForTerm("OneAPI"))
                throw new InvalidOperationException("Equivalent term colors changed.");
            if (TermColor.ForTerm("OneAPI") == TermColor.ForTerm("bootcamp") ||
                TermColor.ForTerm("C") == TermColor.ForTerm("C++") ||
                TermColor.ForTerm("C++") == TermColor.ForTerm("C#"))
                throw new InvalidOperationException("Distinct fixture terms share a color.");
            foreach (string term in new[] { "OneAPI", "bootcamp", "C", "C++", "C#" }) {
                var color = TermColor.ForTerm(term);
                Console.WriteLine(term + ":" + color.R + "," + color.G + "," + color.B);
            }
            Console.WriteLine("term-colors-ok");
            if (Environment.GetEnvironmentVariable("OUTLOOK_UI_PREVIEW") == "1") {
                System.Windows.Forms.Application.EnableVisualStyles();
                var draft = new System.Collections.Generic.Dictionary<string, object> {
                    { "title", "OneAPI bootcamp 讨论会（本地界面预览）" },
                    { "start", "2026-09-20T14:00" }, { "end", "2026-09-20T15:00" }, { "utc_offset", "+08:00" }
                };
                using (var form = new OutlookCalendarForm(draft, payload => new CalendarResponse {
                    ok = true, state = "clear", check_token = "fixture", message = "本地预览：未连接真实账户。" })) {
                    var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                    timer.Tick += delegate {
                        timer.Stop();
                        using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height)) {
                            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                            bitmap.Save("_outlook_ui.png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                        form.Close();
                    };
                    form.Shown += delegate { timer.Start(); };
                    form.ShowDialog();
                    timer.Dispose();
                }
                Console.WriteLine("outlook-ui-preview-ok");
            }
            return 0;
        }
    }
}
