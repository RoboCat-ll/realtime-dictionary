using System;
using System.Collections.Generic;
using System.IO;
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
                    form.Present(new List<CaptionEntry> { new CaptionEntry {
                        timestamp = DateTime.Now, text = "与 OneAPI 同事参加 bootcamp 讨论会"
                    }});
                    if (!form.Visible || !form.TopMost)
                        throw new Exception("Explicit subtitle history action remained behind the meeting player");
                    var text = Field<RichTextBox>(form, "transcript");
                    var label = Field<LinkLabel>(form, "explanation");
                    int translationCalls = 0;
                    string translatedSource = null;
                    form.Translate = delegate(string source) {
                        translationCalls++;
                        translatedSource = source;
                        return new CaptionTranslationResponse { translation = source == "bootcamp"
                            ? "训练营。" : "参加训练营讨论会。" };
                    };
                    if (translationCalls != 0) throw new Exception("History translated without a click");
                    Field<Button>(form, "translateButton").PerformClick();
                    Wait(() => label.Text.Contains("参加训练营讨论会"));
                    if (translationCalls != 1 || translatedSource !=
                        "与 OneAPI 同事参加 bootcamp 讨论会")
                        throw new Exception("Current-line translation lost or added source text");
                    text.Select(text.Text.IndexOf("bootcamp"), 8);
                    Field<Button>(form, "translateButton").PerformClick();
                    Wait(() => label.Text.Contains("AI 译文") && label.Text.Contains("训练营。") &&
                        !label.Text.Contains("讨论会"));
                    if (translationCalls != 2 || translatedSource != "bootcamp")
                        throw new Exception("Selected passage translation ignored the explicit selection");
                    Field<Button>(form, "lookupButton").PerformClick();
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
                    form.Present(new List<CaptionEntry>());
                    if (!form.Visible || !form.TopMost)
                        throw new Exception("Empty subtitle history did not open visibly");
                }
                using (var form = new CaptionHistoryForm()) {
                    var entries = new List<CaptionEntry>();
                    DateTime stamp = new DateTime(2026, 9, 25, 14, 0, 0);
                    for (int index = 0; index < 90; index++)
                        entries.Add(new CaptionEntry { timestamp = stamp.AddSeconds(index),
                            text = "caption-" + index.ToString("00") });
                    form.SetEntries(entries);
                    form.Show();
                    Application.DoEvents();
                    RichTextBox text = Field<RichTextBox>(form, "transcript");
                    int selected = text.Text.IndexOf("caption-05", StringComparison.Ordinal);
                    text.Select(selected, "caption-05".Length);
                    text.ScrollToCaret();
                    int first = NativeMethods.SendMessage(text.Handle,
                        NativeMethods.EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    entries.Add(new CaptionEntry { timestamp = stamp.AddSeconds(90),
                        text = "caption-90" });
                    form.SetEntries(entries);
                    int after = NativeMethods.SendMessage(text.Handle,
                        NativeMethods.EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    if (text.SelectedText != "caption-05" || after != first ||
                        !text.Text.Contains("caption-90"))
                        throw new Exception("Live captions interrupted reading or lost the selection");
                    text.Select(text.TextLength, 0);
                    text.ScrollToCaret();
                    entries.Add(new CaptionEntry { timestamp = stamp.AddSeconds(91),
                        text = "caption-91" });
                    form.SetEntries(entries);
                    if (text.SelectionStart != text.TextLength ||
                        !text.Text.Contains("caption-91"))
                        throw new Exception("History did not follow new captions from the bottom");
                    entries.Add(new CaptionEntry { timestamp = stamp.AddSeconds(92),
                        text = new string('x', 1001) });
                    form.SetEntries(entries);
                    Field<Button>(form, "translateButton").PerformClick();
                    if (!Field<LinkLabel>(form, "explanation").Text.Contains("1000"))
                        throw new Exception("Overlong caption was silently truncated for translation");
                }
                string archiveRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "caption-history-diagnostic");
                var archive = new CaptionHistoryArchive(archiveRoot);
                DateTime day = new DateTime(2024, 2, 11, 10, 0, 0);
                string marker = "saved-caption-" + Guid.NewGuid().ToString("N");
                archive.BeginSession(day, "会议窗口 A");
                var firstArchiveEntry = new CaptionEntry { timestamp = day.AddSeconds(8),
                    text = marker + " one", raw_text = marker + " raw ASR" };
                if (!archive.Append(firstArchiveEntry))
                    throw new Exception("First dated caption could not be saved");
                archive.Append(new CaptionEntry { timestamp = day.AddSeconds(11),
                    text = marker + " two" });
                archive.Append(new CaptionEntry { timestamp = day.AddSeconds(9),
                    text = marker + " missing audio", is_gap = true });
                string[] sessionFiles = Directory.GetFiles(Path.Combine(archiveRoot,
                    day.ToString("yyyy-MM-dd")), "*.jsonl");
                File.AppendAllText(Array.Find(sessionFiles,
                    path => Path.GetFileNameWithoutExtension(path) == firstArchiveEntry.session_key),
                    "{damaged line}\n");
                archive.Append(new CaptionEntry { timestamp = day.AddSeconds(15),
                    text = marker + " after damaged line" });
                archive.BeginSession(day.AddHours(2), "会议窗口 B");
                archive.Append(new CaptionEntry { timestamp = day.AddHours(2).AddSeconds(2),
                    text = marker + " other session" });
                var restored = new CaptionHistoryArchive(archiveRoot).LoadDate(day);
                var markerEntries = restored.FindAll(e => e.text.Contains(marker));
                if (markerEntries.Count != 5 ||
                    markerEntries[0].raw_text != marker + " raw ASR" ||
                    !markerEntries[1].is_gap || markerEntries[1].timestamp != day.AddSeconds(9) ||
                    restored.Find(e => e.text == marker + " other session").session_label
                        != "12:00 · 会议窗口 B" ||
                    new CaptionHistoryArchive(archiveRoot).LoadDate(day.AddDays(-1)).Count != 0)
                    throw new Exception("Dated archive lost a caption or session boundary");
                using (var form = new CaptionHistoryForm()) {
                    form.ArchiveDateRequested = archive.LoadDate;
                    form.PresentArchive();
                    Field<DateTimePicker>(form, "archiveDatePicker").Value = day;
                    DateTime archiveDeadline = DateTime.UtcNow.AddSeconds(5);
                    while (Field<bool>(form, "archiveLoading") && DateTime.UtcNow < archiveDeadline) {
                        Application.DoEvents(); System.Threading.Thread.Sleep(10);
                    }
                    if (Field<bool>(form, "archiveLoading")) throw new Exception("Date archive read did not finish");
                    if (!form.Visible || !form.TopMost ||
                        !Field<RichTextBox>(form, "transcript").Text.Contains(marker + " one") ||
                        !Field<RichTextBox>(form, "transcript").Text.Contains(
                            "原始识别：" + marker + " raw ASR") ||
                        !Field<RichTextBox>(form, "transcript").Text.Contains("⚠ [2024-02-11 10:00:09]") ||
                        !Field<Label>(form, "notice").Text.Contains("处缺口") ||
                        !Field<RichTextBox>(form, "transcript").Text.Contains("会议窗口 B"))
                        throw new Exception("Date picker did not expose stored caption sessions");
                    Field<DateTimePicker>(form, "archiveDatePicker").Value = day.AddDays(-1);
                    if (Field<RichTextBox>(form, "transcript").Text.Contains(marker))
                        throw new Exception("Changing date did not change the viewed archive");
                }
                Console.WriteLine("PASS: history lookup/context/nested/back/task extraction/clear during pending request");
                Console.WriteLine("PASS: history append preserves reading selection and follows bottom");
                Console.WriteLine("PASS: dated caption archive, corrupt-line recovery and date browser");
                return 0;
            } catch (Exception e) { Console.WriteLine(e); return 1; }
        }
    }
}
