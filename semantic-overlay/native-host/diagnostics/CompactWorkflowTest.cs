using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost {
    internal static class CompactWorkflowTest {
        static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, args); }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Wait(Func<bool> predicate) {
            DateTime end = DateTime.UtcNow.AddSeconds(4);
            while (!predicate() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
            Check(predicate(), "UI operation timed out");
        }
        static void Snapshot(Form form, string name) {
            form.Activate(); Application.DoEvents(); Thread.Sleep(200); Application.DoEvents();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(form.Location, Point.Empty, form.Size);
                bitmap.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name));
            }
        }
        [STAThread] static int Main() {
            try {
                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                using (var form = new SelectionAnalysisForm(null, null)) {
                    form.Show(); Application.DoEvents();
                    var sentence = Field<RichTextBox>(form, "sentence");
                    sentence.Text = "我们在讨论 RAG。";
                    Field<TextBox>(form, "body").Text = "这句话是在讨论一种结合检索与生成的技术。";
                    Field<Label>(form, "status").Text = "界面示例 · 使用注入数据";
                    Call(form, "RenderTerms", new List<SelectionTerm> {
                        new SelectionTerm { text = "RAG", explanation = "这里指检索增强生成。" } });
                    Call(form, "RenderTasks", null, null);
                    var layout = Field<TableLayoutPanel>(form, "layout");
                    Check(form.Height < 500 && layout.RowStyles[7].Height == 0, "Default card reserves unused term space");
                    Check(layout.RowStyles[8].Height == 0 && layout.RowStyles[9].Height == 0, "Empty schedule reserves space");
                    Check(Field<FlowLayoutPanel>(form, "terms").Controls.Count == 1, "Duplicate term buttons remain");
                    Snapshot(form, "compact-message-default.png");
                    Task show = (Task)Call(form, "ShowTerm", new SelectionTerm { text = "RAG", explanation = "这里指检索增强生成。" });
                    Wait(() => show.IsCompleted);
                    Check(Field<TextBox>(form, "termBody").Visible, "Explicit word explanation remains hidden");
                    Call(form, "SetSourceEditorVisible", true);
                    Check(Field<TextBox>(form, "source").Visible, "Correction editor inaccessible");
                    Call(form, "SetSourceEditorVisible", false);
                    Snapshot(form, "compact-message.png");
                    form.Close();
                }
                using (var form = new CalendarForm(new HighlightItem { title = "参加班会", time_text = "10月8号下午5点",
                    start_iso = "2026-10-08T17:00", end_iso = "", utc_offset = "+08:00" },
                    payload => { throw new Exception("Complete start must not trigger initial clarification"); }, null, null, null)) {
                    form.Show(); Application.DoEvents();
                    Check(!Field<TextBox>(form, "start").Visible && Field<TextBox>(form, "end").Visible,
                        "Only missing calendar fields should be expanded");
                    var duration = Field<ComboBox>(form, "durationChoices");
                    Check(duration.SelectedIndex == -1 && Field<TextBox>(form, "end").Text == "",
                        "Missing duration was silently invented");
                    Snapshot(form, "compact-calendar.png");
                    duration.SelectedIndex = 1; Application.DoEvents();
                    Check(Field<TextBox>(form, "end").Text == "2026-10-08T18:00" &&
                        !Field<TextBox>(form, "end").Visible, "Explicit duration did not fill and summarize the end");
                    Snapshot(form, "compact-calendar-confirm.png");
                    form.Close();
                }
                using (var release = new ManualResetEvent(false))
                using (var form = new CaptionHistoryForm()) {
                    int calls = 0;
                    var lines = new List<CaptionEntry> { new CaptionEntry { timestamp = DateTime.Now, text = "RAG improves retrieval." } };
                    form.ArchiveDateRequested = date => lines;
                    form.Lookup = (term, context) => { Interlocked.Increment(ref calls); release.WaitOne(4000);
                        return new LookupResponse { explanation = "obsolete result" }; };
                    form.Show(); form.SetEntries(lines); Application.DoEvents();
                    Check(!Field<FlowLayoutPanel>(form, "explanationPanel").Visible && calls == 0,
                        "Caption history shows empty details or sends an implicit query");
                    var transcript = Field<RichTextBox>(form, "transcript");
                    int index = transcript.Text.IndexOf("RAG", StringComparison.Ordinal);
                    transcript.Select(index, 3);
                    var updated = new List<CaptionEntry>(lines) { new CaptionEntry {
                        timestamp = DateTime.Now.AddSeconds(2), text = "Another sentence." } };
                    form.SetEntries(updated);
                    Check(transcript.SelectedText == "RAG", "Incoming subtitles discarded selected word");
                    Task pending = (Task)Call(form, "LookupTerm", "RAG", "RAG improves retrieval.");
                    Wait(() => calls == 1);
                    Field<DateTimePicker>(form, "archiveDatePicker").Value = DateTime.Today.AddDays(-1);
                    release.Set(); Wait(() => pending.IsCompleted);
                    Check(!Field<FlowLayoutPanel>(form, "explanationPanel").Visible &&
                        Field<LinkLabel>(form, "explanation").Text.Length == 0, "Old-date query leaked into new archive");
                    form.Width = 540; Application.DoEvents();
                    Snapshot(form, "compact-caption-history.png");
                    form.Hide(); form.Dispose();
                }
                Console.WriteLine("compact-message-progressive-details-and-caption-date-isolation-ok"); return 0;
            } catch (Exception error) { Console.WriteLine("FAIL compact workflow: " + error.Message); return 1; }
        }
    }
}
