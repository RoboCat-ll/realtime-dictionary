using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal sealed class LocalReminderRequest
    {
        public string title { get; set; }
        public string start { get; set; }
        public string end { get; set; }
        public string utc_offset { get; set; }
        public int lead_minutes { get; set; }
    }

    internal sealed class LocalReminderResult
    {
        public bool ok { get; set; }
        public string error { get; set; }
        public string message { get; set; }
        public string id { get; set; }
    }

    internal sealed class LocalReminderItem
    {
        public string id { get; set; }
        public string title { get; set; }
        public string start { get; set; }
        public string end { get; set; }
        public string utc_offset { get; set; }
        public int lead_minutes { get; set; }
        public string start_utc { get; set; }
        public string due_utc { get; set; }

        public override string ToString()
        {
            string lead = lead_minutes == 0 ? "准时提醒" : "提前 " + lead_minutes + " 分钟";
            return start.Replace('T', ' ') + "  " + title + "  （" + lead + "）";
        }
    }

    internal sealed class LocalReminderStore
    {
        private const int Limit = 500;
        private readonly string path;
        private readonly object gate = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private List<LocalReminderItem> items = new List<LocalReminderItem>();

        public LocalReminderStore(string filePath)
        {
            path = filePath;
            Load(DateTime.UtcNow);
        }

        internal LocalReminderStore(string filePath, DateTime nowUtc)
        {
            path = filePath;
            Load(nowUtc);
        }

        public int Count { get { lock (gate) return items.Count; } }

        public List<LocalReminderItem> Snapshot()
        {
            lock (gate)
                return items.OrderBy(item => ParseUtc(item.due_utc)).Select(Clone).ToList();
        }

        public LocalReminderResult Create(LocalReminderRequest request, DateTime nowUtc)
        {
            if (request == null)
                return Fail("提醒内容无效。");
            string title = (request.title ?? "").Trim();
            if (title.Length == 0 || title.Length > 120 || title.Any(character => Char.IsControl(character)))
                return Fail("事项需填写 1～120 个单行字符。");
            if (request.lead_minutes != 0 && request.lead_minutes != 5 &&
                request.lead_minutes != 10 && request.lead_minutes != 30 && request.lead_minutes != 60)
                return Fail("提醒提前时间无效。");
            DateTimeOffset start;
            DateTimeOffset end;
            if (!TryDate(request.start, request.utc_offset, out start))
                return Fail("开始时间或 UTC 时差无效，请填写完整日期时间。");
            if (!TryDate(request.end, request.utc_offset, out end))
                return Fail("结束时间或 UTC 时差无效，请填写完整日期时间。");
            if (end <= start)
                return Fail("结束时间必须晚于开始时间。");
            DateTime startUtc = start.UtcDateTime;
            if (startUtc <= nowUtc.AddSeconds(1))
                return Fail("开始时间必须晚于当前时间。");
            if (startUtc > nowUtc.AddYears(10))
                return Fail("提醒时间不能超过未来 10 年。");
            DateTime dueUtc = startUtc.AddMinutes(-request.lead_minutes);
            bool immediate = dueUtc <= nowUtc;
            if (immediate)
                dueUtc = nowUtc.AddSeconds(1);
            string normalized = NormalizeTitle(title);
            lock (gate)
            {
                if (items.Any(existing => NormalizeTitle(existing.title) == normalized &&
                    String.Equals(existing.start_utc, Iso(startUtc), StringComparison.Ordinal)))
                    return Fail("相同事项和开始时间的提醒已经存在，没有重复创建。");
                if (items.Count >= Limit)
                    return Fail("本地提醒已达到 500 条，请先在托盘的提醒列表中删除旧项目。");
                LocalReminderItem item = new LocalReminderItem {
                    id = Guid.NewGuid().ToString("N"), title = title,
                    start = request.start, end = request.end, utc_offset = request.utc_offset,
                    lead_minutes = request.lead_minutes, start_utc = Iso(startUtc), due_utc = Iso(dueUtc)
                };
                items.Add(item);
                try { Save(); }
                catch { items.Remove(item); return Fail("提醒未能保存，请检查用户配置目录是否可写。"); }
                return new LocalReminderResult { ok = true, id = item.id,
                    message = immediate
                        ? "提醒已保存；所选提前时间已经过去，将立即提醒。请保持实时字典在托盘运行。"
                        : "本地提醒已保存。请保持实时字典在托盘运行；重启后提醒仍会保留。" };
            }
        }

        public LocalReminderItem NextDue(DateTime nowUtc)
        {
            lock (gate)
            {
                int removed = items.RemoveAll(item => ParseUtc(item.due_utc) < nowUtc.AddHours(-24));
                if (removed > 0) { try { Save(); } catch { } }
                return items.Where(item => ParseUtc(item.due_utc) <= nowUtc &&
                    ParseUtc(item.due_utc) >= nowUtc.AddHours(-24))
                    .OrderBy(item => ParseUtc(item.due_utc)).Select(Clone).FirstOrDefault();
            }
        }

        public void Dismiss(string id)
        {
            lock (gate)
            {
                items.RemoveAll(item => item.id == id);
                Save();
            }
        }

        public void Snooze(string id, DateTime nowUtc)
        {
            lock (gate)
            {
                LocalReminderItem item = items.FirstOrDefault(value => value.id == id);
                if (item == null) return;
                item.due_utc = Iso(nowUtc.AddMinutes(10));
                Save();
            }
        }

        private void Load(DateTime nowUtc)
        {
            lock (gate)
            {
                try
                {
                    if (File.Exists(path))
                        items = serializer.Deserialize<List<LocalReminderItem>>(
                            File.ReadAllText(path, Encoding.UTF8)) ?? new List<LocalReminderItem>();
                }
                catch { items = new List<LocalReminderItem>(); }
                items = items.Where(item => Valid(item) && ParseUtc(item.due_utc) >= nowUtc.AddHours(-24) &&
                    ParseUtc(item.start_utc) <= nowUtc.AddYears(10)).Take(Limit).ToList();
                try { if (File.Exists(path)) Save(); } catch { }
            }
        }

        private void Save()
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, serializer.Serialize(items), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static bool Valid(LocalReminderItem item)
        {
            DateTime ignored;
            return item != null && !String.IsNullOrWhiteSpace(item.id) && !String.IsNullOrWhiteSpace(item.title) &&
                DateTime.TryParseExact(item.start_utc, "o", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out ignored) &&
                DateTime.TryParseExact(item.due_utc, "o", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out ignored);
        }

        private static bool TryDate(string value, string offset, out DateTimeOffset result)
        {
            return DateTimeOffset.TryParseExact((value ?? "") + (offset ?? ""),
                "yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
        }

        private static string NormalizeTitle(string value)
        {
            return new string(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
                .Where(character => !Char.IsWhiteSpace(character)).ToArray());
        }

        private static DateTime ParseUtc(string value)
        {
            DateTime result;
            return DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out result) ? result.ToUniversalTime() : DateTime.MinValue;
        }

        private static string Iso(DateTime value) { return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static LocalReminderItem Clone(LocalReminderItem item)
        {
            return new LocalReminderItem { id = item.id, title = item.title, start = item.start, end = item.end,
                utc_offset = item.utc_offset, lead_minutes = item.lead_minutes,
                start_utc = item.start_utc, due_utc = item.due_utc };
        }
        private static LocalReminderResult Fail(string error) { return new LocalReminderResult { ok = false, error = error }; }
    }

    internal sealed class LocalReminderManager : IDisposable
    {
        private readonly LocalReminderStore store;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private ReminderAlertForm current;
        public event Action CountChanged;

        public LocalReminderManager()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary", "reminders.json");
            store = new LocalReminderStore(path);
            timer.Tick += delegate { CheckDue(); };
            timer.Start();
        }

        public int Count { get { return store.Count; } }

        public LocalReminderResult Create(LocalReminderRequest request)
        {
            LocalReminderResult result = store.Create(request, DateTime.UtcNow);
            if (result.ok)
            {
                if (CountChanged != null) CountChanged();
                CheckDue();
            }
            return result;
        }

        public void ShowList()
        {
            using (ReminderListForm form = new ReminderListForm(store, delegate {
                if (CountChanged != null) CountChanged();
            })) form.ShowDialog();
        }

        private void CheckDue()
        {
            if (current != null && !current.IsDisposed) return;
            LocalReminderItem item = store.NextDue(DateTime.UtcNow);
            if (item == null) return;
            current = new ReminderAlertForm(item);
            current.Dismissed += delegate {
                try { store.Dismiss(item.id); } catch { }
                current = null;
                if (CountChanged != null) CountChanged();
                CheckDue();
            };
            current.Snoozed += delegate {
                try { store.Snooze(item.id, DateTime.UtcNow); } catch { }
                current = null;
                CheckDue();
            };
            current.ShowInactive();
        }

        public void Dispose()
        {
            timer.Stop(); timer.Dispose();
            if (current != null) current.Dispose();
        }
    }

    internal sealed class ReminderAlertForm : Form
    {
        private readonly System.Windows.Forms.Timer soundTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        private int soundCount;
        public event Action Dismissed;
        public event Action Snoozed;

        public ReminderAlertForm(LocalReminderItem item)
        {
            Text = "实时提醒";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            ClientSize = new Size(420, 190);
            Padding = new Padding(20);
            Font = new Font("Microsoft YaHei UI", 10f);
            Controls.Add(new Label { Text = "⏰ 会议或任务提醒", AutoSize = true,
                Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), ForeColor = Color.FromArgb(38, 94, 210),
                Location = new Point(20, 18) });
            Controls.Add(new Label { Text = item.title, AutoEllipsis = true, MaximumSize = new Size(380, 48),
                AutoSize = true, Font = new Font(Font.FontFamily, 13f, FontStyle.Bold), Location = new Point(20, 55) });
            Controls.Add(new Label { Text = item.start.Replace('T', ' ') + "  UTC" + item.utc_offset,
                AutoSize = true, ForeColor = Color.FromArgb(80, 86, 96), Location = new Point(20, 106) });
            Button dismiss = new Button { Text = "知道了", Size = new Size(105, 34), Location = new Point(285, 138) };
            Button snooze = new Button { Text = "10分钟后提醒", Size = new Size(135, 34), Location = new Point(140, 138) };
            Controls.Add(snooze); Controls.Add(dismiss);
            dismiss.Click += delegate { soundTimer.Stop(); Hide(); if (Dismissed != null) Dismissed(); Dispose(); };
            snooze.Click += delegate { soundTimer.Stop(); Hide(); if (Snoozed != null) Snoozed(); Dispose(); };
            soundTimer.Tick += delegate { if (++soundCount >= 6) soundTimer.Stop(); else SystemSounds.Exclamation.Play(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate; return value; }
        }

        public void ShowInactive()
        {
            Rectangle area = Screen.FromHandle(NativeMethods.GetForegroundWindow()).WorkingArea;
            Location = new Point(area.Right - Width - 18, area.Bottom - Height - 18);
            NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost, Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            SystemSounds.Exclamation.Play(); soundCount = 1; soundTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen pen = new Pen(Color.FromArgb(82, 148, 255), 2)) args.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }

        protected override void Dispose(bool disposing) { if (disposing) soundTimer.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class ReminderListForm : Form
    {
        private readonly LocalReminderStore store;
        private readonly ListBox list = new ListBox { Width = 560, Height = 260 };
        private readonly Action changed;

        public ReminderListForm(LocalReminderStore value, Action onChanged)
        {
            store = value; changed = onChanged;
            Text = "本地提醒"; StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Font = new Font("Microsoft YaHei UI", 10f);
            FlowLayoutPanel layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, Padding = new Padding(16) };
            Controls.Add(layout);
            layout.Controls.Add(new Label { AutoSize = true, Text = "提醒保存在本机。实时字典需在托盘运行，才能准时弹出通知。" });
            layout.Controls.Add(list);
            Button remove = new Button { Text = "删除选中提醒", AutoSize = true };
            layout.Controls.Add(remove);
            remove.Click += delegate {
                LocalReminderItem item = list.SelectedItem as LocalReminderItem;
                if (item == null) return;
                if (MessageBox.Show(this, "删除提醒“" + item.title + "”？", "本地提醒",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { store.Dismiss(item.id); RefreshItems(); if (changed != null) changed(); }
                catch { MessageBox.Show(this, "删除失败，请检查用户配置目录。", "本地提醒"); }
            };
            RefreshItems();
        }

        private void RefreshItems()
        {
            list.Items.Clear();
            foreach (LocalReminderItem item in store.Snapshot()) list.Items.Add(item);
        }
    }
}
