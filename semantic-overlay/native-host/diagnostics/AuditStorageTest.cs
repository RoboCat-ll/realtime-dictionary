using System;
using System.IO;
using System.Drawing;

namespace SemanticOverlay.NativeHost
{
    internal static class AuditStorageTest
    {
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

        public static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "rd-storage-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                foreach (string invalid in new[] { "{broken", "null" }) {
                    string damaged = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(damaged, invalid);
                    var broken = new LocalReminderStore(damaged, DateTime.UtcNow);
                    Require(broken.LoadFailed && File.ReadAllText(damaged) == invalid, "Damaged reminder was overwritten");
                    Require(!broken.Create(null, DateTime.UtcNow).ok, "Damaged reminder store allowed writing");
                }
                DateTime now = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
                string path = Path.Combine(root, "reminders.json");
                var store = new LocalReminderStore(path, now);
                var result = store.Create(new LocalReminderRequest {
                    title = "Synthetic", start = "2026-10-06T10:00", end = "2026-10-06T11:00",
                    utc_offset = "+00:00", lead_minutes = 10 }, now);
                Require(result.ok, "Synthetic create failed");
                string originalDue = store.Snapshot()[0].due_utc;
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
                    bool failed = false;
                    try { store.Dismiss(result.id); } catch (IOException) { failed = true; }
                    Require(failed && store.Count == 1, "Failed dismiss mutated memory");
                    failed = false;
                    try { store.Snooze(result.id, now); } catch (IOException) { failed = true; }
                    Require(failed && store.Snapshot()[0].due_utc == originalDue, "Failed snooze mutated memory");
                    var unreadable = new LocalReminderStore(path, now);
                    Require(unreadable.LoadFailed, "Locked reminder was treated as empty success");
                }
                Require(new LocalReminderStore(path, now).Count == 1, "Reminder disk changed after failure");
                store.Dismiss(result.id);
                Require(new LocalReminderStore(path, now).Count == 0, "Successful dismissal was not saved");
                string prefs = Path.Combine(root, "prefs.json");
                var p = new PreferenceStore(prefs, null);
                p.SetAndSave("one", "1");
                using (var locked = new FileStream(prefs, FileMode.Open, FileAccess.Read, FileShare.None)) {
                    try { p.SetAndSave("two", "2"); } catch (IOException) { }
                    string value;
                    Require(!p.TryGetValue("two", out value), "Failed save published persisted state");
                    p.SetForSession("two", "2");
                    Require(p.TryGetValue("two", out value) && value == "2", "Session-only fallback unavailable");
                }
                Require(Directory.GetFiles(root, "*.tmp").Length == 0, "Temporary preference files leaked");
                File.WriteAllText(prefs, "{broken");
                var brokenPrefs = new PreferenceStore(prefs, delegate { });
                bool blocked = false;
                try { brokenPrefs.SetAndSave("one", "2"); } catch (InvalidOperationException) { blocked = true; }
                Require(blocked && File.ReadAllText(prefs) == "{broken", "Corrupt preferences overwritten");
                Require(MessageTextReader.IsCandidate("好", "Text", new Rectangle(10, 10, 120, 35),
                    new Rectangle(0, 0, 800, 600), new Point(30, 20), 1000), "One-character message rejected");
                Require(!MessageTextReader.IsCandidate("好", "Button", new Rectangle(10, 10, 120, 35),
                    new Rectangle(0, 0, 800, 600), new Point(30, 20), 1000), "Button accepted as message");
                string journal = Path.Combine(root, "journal.log");
                for (int i = 0; i < 30; i++) BoundedJournal.Append(journal, "synthetic line " + i, 100);
                Require(new FileInfo(journal).Length <= 100 && new FileInfo(journal + ".1").Length <= 100,
                    "Journal size is unbounded");
                string metricsPath = Path.Combine(root, "metrics.jsonl");
                var metrics = new UsageMetricsStore(metricsPath);
                for (int i = 0; i < 10; i++) {
                    var operation = metrics.BeginMessageOperation("wechat");
                    System.Threading.Thread.Sleep(2);
                    operation.Complete(i % 2 == 0 ? "read_failed" : "model", "bubble_ocr", i == 9);
                    operation.Complete("cancelled");
                }
                Require(File.ReadAllLines(metricsPath).Length == 10, "Operation terminal events missing or duplicated");
                Require(File.ReadAllText(metricsPath).Contains("read_failed") && metrics.FailedWrites == 0,
                    "Read failures were not measured");
                using (var locked = new FileStream(metricsPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    metrics.BeginMessageOperation("qq").Complete("busy");
                Require(metrics.FailedWrites == 1, "Metric write failure was not exposed");
                Console.WriteLine("audit-storage-input-ok");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
