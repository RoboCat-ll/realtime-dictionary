using System;

namespace SemanticOverlay.NativeHost
{
    internal static class CaptionTextTest
    {
        [STAThread]
        public static int Main()
        {
            string first = OverlayContext.NormalizeTranscriptIdentity("One API，讨论会！");
            string second = OverlayContext.NormalizeTranscriptIdentity("one api 讨论会");
            if (!String.Equals(first, second, StringComparison.Ordinal))
                throw new InvalidOperationException("Formatting-only transcript changes were not normalized.");
            if (OverlayContext.NormalizeTranscriptIdentity("，。！？ …").Length != 0)
                throw new InvalidOperationException("Punctuation-only transcript was accepted.");
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
