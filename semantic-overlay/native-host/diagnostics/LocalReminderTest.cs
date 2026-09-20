using System;
using System.IO;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class LocalReminderTest
    {
        [STAThread]
        public static int Main()
        {
            string directory = Path.Combine(Path.GetTempPath(), "RealtimeDictionary-ReminderTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "reminders.json");
            DateTime now = new DateTime(2026, 9, 14, 2, 0, 0, DateTimeKind.Utc);
            try
            {
                LocalReminderRequest request = new LocalReminderRequest {
                    title = "OneAPI bootcamp 讨论会", start = "2026-09-14T10:30",
                    end = "2026-09-14T11:30", utc_offset = "+08:00", lead_minutes = 10
                };
                LocalReminderStore store = new LocalReminderStore(path, now);
                LocalReminderResult created = store.Create(request, now);
                if (!created.ok || !File.Exists(path) || store.Count != 1)
                    throw new InvalidOperationException("Reminder was not persisted.");
                if (store.Create(request, now).ok || store.Count != 1)
                    throw new InvalidOperationException("Duplicate reminder was accepted.");
                store = new LocalReminderStore(path, now);
                if (store.NextDue(new DateTime(2026, 9, 14, 2, 19, 59, DateTimeKind.Utc)) != null)
                    throw new InvalidOperationException("Reminder fired early.");
                LocalReminderItem due = store.NextDue(new DateTime(2026, 9, 14, 2, 20, 0, DateTimeKind.Utc));
                if (due == null || due.id != created.id)
                    throw new InvalidOperationException("Reminder did not fire at the timezone-adjusted due time.");
                store.Snooze(due.id, new DateTime(2026, 9, 14, 2, 20, 0, DateTimeKind.Utc));
                if (store.NextDue(new DateTime(2026, 9, 14, 2, 29, 59, DateTimeKind.Utc)) != null ||
                    store.NextDue(new DateTime(2026, 9, 14, 2, 30, 0, DateTimeKind.Utc)) == null)
                    throw new InvalidOperationException("Ten-minute snooze was not persisted.");
                store.Dismiss(due.id);
                if (store.Count != 0 || new LocalReminderStore(path, now).Count != 0)
                    throw new InvalidOperationException("Dismissed reminder remained persisted.");
                request.start = "2026-09-14T09:00";
                request.end = "2026-09-14T10:00";
                if (store.Create(request, now).ok)
                    throw new InvalidOperationException("Past reminder was accepted.");
                VerifyConnectedReminderFlow();
                Console.WriteLine("local-reminder-ok");
                if (Environment.GetEnvironmentVariable("REMINDER_UI_PREVIEW") == "1")
                {
                    Application.EnableVisualStyles();
                    LocalReminderItem preview = new LocalReminderItem {
                        id = "preview", title = "OneAPI bootcamp 讨论会",
                        start = "2026-09-14T14:00", utc_offset = "+08:00"
                    };
                    using (ReminderAlertForm form = new ReminderAlertForm(preview))
                    {
                        Timer timer = new Timer { Interval = 600 };
                        timer.Tick += delegate {
                            timer.Stop();
                            using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                                bitmap.Save("_reminder_ui.png", System.Drawing.Imaging.ImageFormat.Png);
                            }
                            form.Close();
                        };
                        form.Shown += delegate { timer.Start(); };
                        form.ShowDialog();
                        timer.Dispose();
                    }
                    Console.WriteLine("reminder-ui-preview-ok");
                }
                return 0;
            }
            finally
            {
                string resolved = Path.GetFullPath(directory);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsafe test cleanup path.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }

        private static void VerifyConnectedReminderFlow()
        {
            LocalReminderRequest captured = null;
            bool callback = false;
            HighlightItem candidate = new HighlightItem {
                term = "2026年9月20日下午14:00", time_text = "2026年9月20日下午14:00",
                title = "OneAPI bootcamp 讨论会", start_iso = "2026-09-20T14:00",
                end_iso = "2026-09-20T15:00", utc_offset = "+08:00", kind = "task"
            };
            using (CalendarForm form = new CalendarForm(candidate,
                delegate { return new CalendarResponse { ok = false, error = "not used" }; },
                delegate(LocalReminderRequest value) {
                    captured = value;
                    return new LocalReminderResult { ok = true, id = "flow", message = "saved" };
                }, delegate { }, delegate { callback = true; }))
            {
                form.Show();
                Application.DoEvents();
                TextBox start = Find<TextBox>(form, "Reminder start");
                TextBox end = Find<TextBox>(form, "Reminder end");
                TextBox offset = Find<TextBox>(form, "Reminder UTC offset");
                Button create = Find<Button>(form, "Create local reminder");
                if (start == null || end == null || offset == null || create == null ||
                    start.Text != candidate.start_iso || end.Text != candidate.end_iso ||
                    offset.Text != candidate.utc_offset)
                    throw new InvalidOperationException("Analyzed reminder fields were not carried into confirmation.");
                if (Environment.GetEnvironmentVariable("REMINDER_FLOW_UI_PREVIEW") == "1")
                {
                    using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                        bitmap.Save("_reminder_flow_ui.png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                create.PerformClick();
                Application.DoEvents();
                if (captured == null || !callback || create.Enabled ||
                    captured.title != candidate.title || captured.start != candidate.start_iso)
                    throw new InvalidOperationException("Reminder confirmation did not reach the completed state.");
                Button view = Find<Button>(form, "View local reminders");
                if (view == null || !view.Visible)
                    throw new InvalidOperationException("Completed reminder cannot open the reminder list.");
                form.Close();
            }
        }

        private static T Find<T>(Control root, string accessibleName) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                T match = child as T;
                if (match != null && match.AccessibleName == accessibleName) return match;
                match = Find<T>(child, accessibleName);
                if (match != null) return match;
            }
            return null;
        }
    }
}
