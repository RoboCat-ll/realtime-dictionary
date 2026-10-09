using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Threading;
using System.Web.Script.Serialization;

namespace SemanticOverlay.NativeHost
{
    internal static class AuditCaptionHistoryTest
    {
        private static T Field<T>(object value, string name) {
            return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
        }
        private static void WaitLoad(CaptionHistoryForm form) {
            DateTime end = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(form, "archiveLoading") && DateTime.UtcNow < end) {
                Application.DoEvents(); Thread.Sleep(10);
            }
            if (Field<bool>(form, "archiveLoading")) throw new Exception("Archive read did not finish");
        }
        [STAThread]
        public static int Main()
        {
            Control.CheckForIllegalCrossThreadCalls = true;
            string root = Path.Combine(Path.GetTempPath(), "rd-caption-audit-" + Guid.NewGuid().ToString("N"));
            try {
                DateTime day = DateTime.Today;
                var archive = new CaptionHistoryArchive(root);
                archive.BeginSession(day, "Synthetic");
                var first = new CaptionEntry { timestamp = day.AddHours(1), text = "first" };
                if (!archive.Append(first)) throw new Exception("Archive write failed");
                string directory = Path.Combine(root, day.ToString("yyyy-MM-dd"));
                File.AppendAllText(Directory.GetFiles(directory, "*.jsonl")[0], "{broken}\n");
                using (var oversized = File.Create(Path.Combine(directory, "oversized.jsonl")))
                    oversized.SetLength(20 * 1024 * 1024 + 1);
                var result = archive.LoadDateWithStatus(day);
                if (result.Entries.Count != 1 || result.SkippedFiles != 1 || result.SkippedLines != 1 || !result.Incomplete)
                    throw new Exception("Archive omissions were silently hidden");
                using (var form = new CaptionHistoryForm()) {
                    int reads = 0;
                    form.ArchiveDateRequested = delegate { reads++; return result.Entries; };
                    form.ArchiveStatusRequested = delegate { reads++; return result; };
                    form.Show(); Application.DoEvents();
                    form.RefreshArchiveDate(true);
                    WaitLoad(form);
                    if (!Field<Label>(form, "notice").Text.Contains("未完整读取"))
                        throw new Exception("Archive warning not visible");
                    int before = reads;
                    var text = Field<RichTextBox>(form, "transcript");
                    text.Select(text.Text.IndexOf("first"), 5);
                    form.AppendLiveEntry(new CaptionEntry { timestamp = day.AddHours(1).AddSeconds(2), text = "second",
                        session_key = first.session_key, session_label = first.session_label }, day, true);
                    if (reads != before || text.SelectedText != "first" || !text.Text.Contains("second") ||
                        !Field<Label>(form, "notice").Text.Contains("未完整读取"))
                        throw new Exception("Live append reread disk, disturbed reading or lost archive warning");
                    Field<DateTimePicker>(form, "archiveDatePicker").Value = day.AddDays(-1);
                    WaitLoad(form);
                    before = reads;
                    string previous = text.Text;
                    form.AppendLiveEntry(new CaptionEntry { timestamp = day.AddHours(2), text = "new today" }, day, true);
                    if (reads != before || text.Text != previous) throw new Exception("Old-date view changed on live append");
                    form.Close();
                }
                // More than 2000 valid entries in a >20 MB file must be accessible
                // through bounded pages, including replace_last at the page edge.
                DateTime bigDay = day.AddDays(-2);
                string bigDirectory = Path.Combine(root, bigDay.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(bigDirectory);
                var json = new JavaScriptSerializer();
                using (var writer = new StreamWriter(Path.Combine(bigDirectory, "large.jsonl"))) {
                    writer.WriteLine(new string('x', 21 * 1024 * 1024));
                    for (int i = 0; i < 2001; i++) {
                        writer.WriteLine(json.Serialize(new { kind = "line", timestamp = bigDay.AddSeconds(i).ToString("o"), text = "line" + i }));
                        if (i == 1999) writer.WriteLine(json.Serialize(new { kind = "replace_last", timestamp = bigDay.AddSeconds(i).ToString("o"), text = "replaced" }));
                    }
                }
                CaptionArchiveCursor cursor = null;
                var recovered = new List<CaptionEntry>();
                int pages = 0;
                do {
                    var page = archive.LoadPage(bigDay, cursor);
                    if (page.Entries.Count > 2000) throw new Exception("Page memory bound exceeded");
                    recovered.AddRange(page.Entries);
                    cursor = page.Next;
                    if (++pages > 8) throw new Exception("Page cursor did not progress");
                } while (cursor != null);
                if (recovered.Count != 2001 || recovered[1999].text != "replaced" || recovered[2000].text != "line2000")
                    throw new Exception("Paging lost a valid record or page-edge replacement");
                using (var form = new CaptionHistoryForm())
                using (var release = new ManualResetEvent(false)) {
                    int uiThread = Thread.CurrentThread.ManagedThreadId;
                    int readerThread = uiThread;
                    form.ArchivePageRequested = delegate(DateTime date, CaptionArchiveCursor start) {
                        readerThread = Thread.CurrentThread.ManagedThreadId;
                        if (date == day) { release.WaitOne(3000); return new CaptionArchiveReadResult {
                            Entries = new List<CaptionEntry> { new CaptionEntry { timestamp = day, text = "stale today" } } }; }
                        return new CaptionArchiveReadResult { Entries = new List<CaptionEntry> {
                            new CaptionEntry { timestamp = date, text = "selected date" } } };
                    };
                    form.Show(); Application.DoEvents();
                    form.RefreshArchiveDate(true);
                    if (!Field<bool>(form, "archiveLoading")) throw new Exception("Read blocked UI instead of running asynchronously");
                    Field<DateTimePicker>(form, "archiveDatePicker").Value = day.AddDays(-1);
                    WaitLoad(form);
                    release.Set();
                    DateTime settle = DateTime.UtcNow.AddMilliseconds(150);
                    while (DateTime.UtcNow < settle) { Application.DoEvents(); Thread.Sleep(10); }
                    if (readerThread == uiThread || Field<RichTextBox>(form, "transcript").Text.Contains("stale today") ||
                        !Field<RichTextBox>(form, "transcript").Text.Contains("selected date"))
                        throw new Exception("Slow stale date read overwrote the selected date or ran on UI");
                    release.Reset();
                    Field<DateTimePicker>(form, "archiveDatePicker").Value = day;
                    form.AppendLiveEntry(new CaptionEntry { timestamp = day.AddMinutes(1), text = "arrived during load" }, day, true);
                    if (Field<List<CaptionEntry>>(form, "pendingLive").Count != 1)
                        throw new Exception("Expected one buffered caption; loading=" + Field<bool>(form, "archiveLoading") +
                            " visible=" + form.Visible + " date=" + Field<DateTimePicker>(form, "archiveDatePicker").Value);
                    release.Set();
                    WaitLoad(form);
                    if (!Field<RichTextBox>(form, "transcript").Text.Contains("arrived during load"))
                        throw new Exception("Caption arriving during background read was lost; notice=" + Field<Label>(form, "notice").Text +
                            " text=" + Field<RichTextBox>(form, "transcript").Text);
                    form.Close();
                }
                using (var form = new CaptionHistoryForm()) {
                    form.ArchivePageRequested = archive.LoadPage;
                    form.Show(); Application.DoEvents();
                    Field<DateTimePicker>(form, "archiveDatePicker").Value = bigDay;
                    WaitLoad(form);
                    int navigation = 0;
                    while (Field<Button>(form, "nextPageButton").Enabled) {
                        Field<Button>(form, "nextPageButton").PerformClick();
                        WaitLoad(form);
                        if (++navigation > 8) throw new Exception("Page UI failed to progress");
                    }
                    if (!Field<RichTextBox>(form, "transcript").Text.Contains("line2000") ||
                        !Field<Button>(form, "previousPageButton").Enabled)
                        throw new Exception("Last page not reachable through UI");
                    Field<Button>(form, "previousPageButton").PerformClick();
                    WaitLoad(form);
                    if (!Field<RichTextBox>(form, "transcript").Text.Contains("replaced") ||
                        !Field<Button>(form, "nextPageButton").Enabled)
                        throw new Exception("Previous page did not restore bounded history");
                    form.Close();
                }
                Console.WriteLine("audit-caption-history-status-and-incremental-ok");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
        }
    }
}
