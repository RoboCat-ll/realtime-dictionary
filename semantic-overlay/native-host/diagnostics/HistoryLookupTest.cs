using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
namespace SemanticOverlay.NativeHost
{
    internal static class HistoryLookupTest
    {
        static T Field<T>(object value, string name) {
            return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        }
        static void Wait(Func<bool> done) {
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
            if (!done()) throw new Exception("Timed out waiting for lookup UI");
        }
        [STAThread]
        static int Main() {
            try {
                using (var form = new CaptionHistoryForm()) {
                    string receivedContext = null;
                    form.Lookup = delegate(string term, string context) {
                        receivedContext = context;
                        if (term == "bootcamp") return new LookupResponse {
                            term = term, explanation = "API 是应用接口。", entities = new List<AnalysisEntity> {
                                new AnalysisEntity { text = "API", start = 0, end = 3 }
                            }
                        };
                        return new LookupResponse { term = term, explanation = "用于程序之间的通信。" };
                    };
                    form.SetEntries(new List<CaptionEntry> { new CaptionEntry {
                        timestamp = DateTime.Now, text = "与 OneAPI 同事参加 bootcamp 讨论会"
                    }});
                    form.Show();
                    var text = Field<RichTextBox>(form, "transcript");
                    text.Select(text.Text.IndexOf("bootcamp"), 8);
                    Field<Button>(form, "lookupButton").PerformClick();
                    var label = Field<LinkLabel>(form, "explanation");
                    Wait(() => label.Links.Count == 1);
                    if (!receivedContext.Contains("OneAPI")) throw new Exception("Context was lost");
                    typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(label, new object[] { new LinkLabelLinkClickedEventArgs(label.Links[0]) });
                    Wait(() => label.Text == "用于程序之间的通信。");
                    Field<Button>(form, "backButton").PerformClick();
                    if (label.Text != "API 是应用接口。" || label.Links.Count != 1)
                        throw new Exception("Back navigation failed");
                    bool receivedTask = false;
                    form.Analyze = delegate(string context) {
                        if (context.StartsWith("[")) throw new Exception("Recording timestamp leaked into meeting text");
                        return new AnalyzeResponse { actions = new List<AnalysisEntity> {
                            new AnalysisEntity { text = "9月3号下午14:00", time_text = "9月3号下午14:00",
                                title = "与 OneAPI 同事进行 bootcamp 讨论会", needs_confirmation = true }
                        }};
                    };
                    form.EditTaskRequested += delegate(HighlightItem candidate) {
                        receivedTask = candidate.needs_confirmation && candidate.title.Contains("bootcamp");
                    };
                    text.Select(text.Text.IndexOf("bootcamp"), 8);
                    Field<Button>(form, "taskButton").PerformClick();
                    Wait(() => receivedTask);
                    if (!receivedTask) throw new Exception("History task extraction failed");
                    form.Lookup = delegate(string term, string context) {
                        Thread.Sleep(150);
                        return new LookupResponse { explanation = "迟到的回复不应重新出现" };
                    };
                    Field<Button>(form, "lookupButton").PerformClick();
                    form.SetEntries(new List<CaptionEntry>());
                    DateTime until = DateTime.UtcNow.AddMilliseconds(350);
                    while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
                    if (label.Text.Length > 0 || Field<Button>(form, "backButton").Enabled)
                        throw new Exception("Cleared history was repopulated by late response");
                    form.Hide();
                }
                Console.WriteLine("PASS: history lookup/context/nested/back/task extraction/clear during pending request");
                return 0;
            } catch (Exception e) { Console.WriteLine(e); return 1; }
        }
    }
}
