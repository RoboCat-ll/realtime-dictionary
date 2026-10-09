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
    internal sealed class CalendarResponse
    {
        public string user_code { get; set; }
        public string check_token { get; set; }
        public string event_id { get; set; }
        public string web_url { get; set; }
        public string start { get; set; }
        public string end { get; set; }
        public string utc_offset { get; set; }
        public string state { get; set; }
        public List<string> conflicts { get; set; }
        public List<string> duplicates { get; set; }
        public List<string> missing { get; set; }
        public bool ok { get; set; }
        public string error { get; set; }
        public string ics { get; set; }
        public string filename { get; set; }
        public string message { get; set; }
        public bool year_inferred { get; set; }
    }

    internal sealed class OutlookCalendarForm : Form
    {
        private static string lastClientId = ""; // Public ID only, remembered for this process.
        private readonly Dictionary<string, object> draft;
        private readonly Func<Dictionary<string, object>, CalendarResponse> request;
        private readonly TextBox clientId = new TextBox { Width = 480, Text = lastClientId };
        private readonly TextBox status = new TextBox { Multiline = true, ReadOnly = true, Width = 480, Height = 130,
            ScrollBars = ScrollBars.Vertical, Text = "首次使用请填写应用 ID 并连接。\r\n如果本次运行已登录，可直接检查默认日历。\r\n注册方法见安装目录中的 OUTLOOK_SETUP.md。" };
        private readonly Button login = new Button { Text = "连接微软日历", AutoSize = true };
        private readonly Button check = new Button { Text = "检查默认日历冲突", AutoSize = true };
        private readonly Button create = new Button { Text = "确认写入微软日历", AutoSize = true, Enabled = false };
        private readonly Button disconnect = new Button { Text = "断开日历连接", AutoSize = true };
        private readonly Button open = new Button { Text = "打开日历中的事件", AutoSize = true, Enabled = false };
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 5000 };
        private string ticket;
        private string eventUrl;
        private bool busy;

        public OutlookCalendarForm(Dictionary<string, object> value, Func<Dictionary<string, object>, CalendarResponse> sender)
        {
            draft = new Dictionary<string, object>(value);
            request = sender;
            Text = "微软日历：检查并创建";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(18) };
            Controls.Add(layout);
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(480, 0), Text =
                value["title"] + "\r\n" + value["start"] + " 至 " + value["end"] + " (UTC" + value["utc_offset"] + ")\r\n" +
                "读取并写入登录账户的默认日历；不发送邀请。\r\n登录仅本次程序运行有效，退出后需重新登录。" });
            layout.Controls.Add(new Label { AutoSize = true, Text = "首次使用：填写自己注册的应用程序（客户端）ID" });
            layout.Controls.Add(clientId);
            layout.Controls.Add(login);
            layout.Controls.Add(status);
            layout.Controls.Add(check);
            layout.Controls.Add(create);
            layout.Controls.Add(open);
            layout.Controls.Add(disconnect);
            login.Click += async delegate {
                if (busy) return;
                poll.Stop(); ticket = null; create.Enabled = false;
                var result = await Send("outlook_login");
                if (IsDisposed || result == null || !result.ok) return;
                lastClientId = clientId.Text.Trim();
                poll.Start();
                try { Process.Start(new ProcessStartInfo("https://microsoft.com/devicelogin") { UseShellExecute = true }); }
                catch { status.AppendText("\r\n请在浏览器打开 https://microsoft.com/devicelogin"); }
            };
            poll.Tick += async delegate {
                if (busy) return;
                var result = await Send("outlook_poll");
                if (IsDisposed) return;
                if (result == null || !result.ok || result.state != "pending") poll.Stop();
            };
            check.Click += async delegate {
                if (busy) return;
                ticket = null; create.Enabled = false;
                var result = await Send("outlook_check");
                if (IsDisposed || result == null || !result.ok) return;
                ticket = (result.state == "clear" || result.state == "conflict") ? result.check_token : null;
                create.Enabled = !String.IsNullOrEmpty(ticket);
            };
            create.Click += async delegate {
                if (busy || String.IsNullOrEmpty(ticket)) return;
                if (MessageBox.Show(this, status.Text + "\r\n\r\n" + draft["title"] + "\r\n" +
                    draft["start"] + " 至 " + draft["end"] + " (UTC" + draft["utc_offset"] + ")\r\n确认写入上面显示的微软默认日历？",
                    "确认创建日程", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var result = await Send("outlook_create");
                if (IsDisposed) return;
                if (result != null && result.ok && !String.IsNullOrEmpty(result.event_id)) {
                    ticket = null; create.Enabled = false;
                    eventUrl = result.web_url;
                    open.Enabled = SafeEventUrl(eventUrl);
                }
            };
            disconnect.Click += async delegate {
                if (busy) return;
                poll.Stop(); ticket = null; create.Enabled = false; open.Enabled = false;
                await Send("outlook_disconnect");
            };
            open.Click += delegate {
                if (!SafeEventUrl(eventUrl)) return;
                try { Process.Start(new ProcessStartInfo(eventUrl) { UseShellExecute = true }); }
                catch { status.AppendText("\r\n浏览器未能打开，请在 Outlook 日历中查看。"); }
            };
            FormClosing += delegate(object source, FormClosingEventArgs args) { if (busy) args.Cancel = true; };
            FormClosed += delegate { poll.Stop(); poll.Dispose(); };
        }

        private static bool SafeEventUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                (uri.Host == "outlook.live.com" || uri.Host == "outlook.office.com" || uri.Host == "outlook.office365.com");
        }

        private async Task<CalendarResponse> Send(string operation)
        {
            busy = true;
            login.Enabled = check.Enabled = disconnect.Enabled = clientId.Enabled = create.Enabled = false;
            var payload = new Dictionary<string, object>(draft);
            payload["operation"] = operation;
            payload["client_id"] = clientId.Text.Trim();
            payload["check_token"] = ticket;
            payload["confirmed"] = operation == "outlook_create";
            try {
                var result = await Task.Factory.StartNew(() => request(payload));
                if (IsDisposed) return null;
                // Keep the user code visible while device login is pending.
                if (result == null) status.Text = "未收到结果，请重试。";
                else if (!result.ok) status.Text = result.error;
                else if (operation != "outlook_poll" || result.state != "pending") {
                    status.Text = result.message;
                    if (result.conflicts != null && result.conflicts.Count > 0)
                        status.AppendText("\r\n冲突：" + String.Join("、", result.conflicts.ToArray()));
                }
                return result;
            }
            catch { if (!IsDisposed) status.Text = "日历请求未完成；如正在创建，请重试同一份日程以确认结果。"; return null; }
            finally {
                busy = false;
                if (!IsDisposed) {
                    login.Enabled = check.Enabled = disconnect.Enabled = clientId.Enabled = true;
                    create.Enabled = !String.IsNullOrEmpty(ticket);
                }
            }
        }
    }

    internal sealed class CalendarForm : Form
    {
        private readonly TextBox title = new TextBox();
        private readonly TextBox start = new TextBox();
        private readonly TextBox end = new TextBox();
        private readonly TextBox offset = new TextBox();
        private readonly Label status = new Label();
        private readonly Button export = new Button();
        private readonly Button check = new Button();
        private readonly Button import = new Button();
        private string calendarIcs;
        private bool checkedDraft;
        private bool busy;

        private readonly Dictionary<Control, Label> draftFields = new Dictionary<Control, Label>();
        private readonly Label draftSummary = new Label { AutoSize = true, MaximumSize = new Size(480, 0) };
        private readonly Label inferenceNotice = new Label { AutoSize = true, MaximumSize = new Size(480, 0),
            ForeColor = Color.FromArgb(180, 90, 25), Visible = false };
        private readonly Button editDraft = new Button { Text = "修改已识别信息", AutoSize = true };
        private bool draftExpanded;
        private readonly List<Control> clarificationInputs = new List<Control>();
        private readonly ComboBox durationChoices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
        private readonly Label durationLabel = new Label { Text = "持续多久？选择后自动填写结束时间", AutoSize = true };
        private int selectedDurationMinutes;
        private bool updatingDurationEnd;

        private void ApplyDraftOnUi(Action action)
        {
            if (IsDisposed || Disposing) return;
            MethodInvoker apply = delegate { if (!IsDisposed && !Disposing) action(); };
            if (!InvokeRequired) { apply(); return; }
            try { BeginInvoke(apply); }
            catch (InvalidOperationException) { /* The user closed the draft. */ }
        }

        private void UpdateDurationEnd()
        {
            if (selectedDurationMinutes == 0) return;
            DateTime begins;
            updatingDurationEnd = true;
            try {
                end.Text = DateTime.TryParseExact(start.Text.Trim(), "yyyy-MM-ddTHH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out begins)
                    ? begins.AddMinutes(selectedDurationMinutes).ToString("yyyy-MM-ddTHH:mm") : "";
            }
            catch (ArgumentOutOfRangeException) {
                end.Text = "";
                status.Text = "结束日期超出范围，请修改开始时间。";
            }
            finally { updatingDurationEnd = false; }
        }

        public CalendarForm(HighlightItem item, Func<Dictionary<string, object>, CalendarResponse> exporter,
            Func<LocalReminderRequest, LocalReminderResult> reminderCreator, Action showReminders,
            Action<LocalReminderResult> reminderCreated)
        {
            Text = "确认并设置提醒";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Microsoft YaHei UI", 10f);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoSize = true;
            layout.ColumnCount = 1;
            layout.Padding = new Padding(20);
            layout.Dock = DockStyle.Fill;
            Controls.Add(layout);
            Label original = new Label();
            original.AutoSize = true;
            original.MaximumSize = new Size(480, 0);
            original.Text = "发现一个安排：" + (item.time_text ?? item.term) +
                "\r\n核对下面的信息后，即可创建本地提醒。不会自动写入日历。";
            layout.Controls.Add(original);
            layout.Controls.Add(draftSummary);
            layout.Controls.Add(inferenceNotice);
            layout.Controls.Add(editDraft);
            editDraft.Click += delegate {
                draftExpanded = !draftExpanded;
                RefreshDraftPresentation();
            };
            TextBox supplement = new TextBox();
            supplement.Width = 470;
            supplement.MaxLength = 1000;
            Label supplementLabel = new Label { Text = "补充缺失信息（例：持续1小时）",
                AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
            layout.Controls.Add(supplementLabel);
            layout.Controls.Add(supplement);
            Button applySupplement = new Button { Text = "整理补充信息", AutoSize = true };
            layout.Controls.Add(applySupplement);
            clarificationInputs.AddRange(new Control[] { supplementLabel, supplement, applySupplement });
            Func<bool, Task> clarifyDraft = async initial =>
            {
                if (busy) return;
                busy = true;
                checkedDraft = false;
                export.Enabled = false;
                applySupplement.Enabled = check.Enabled = import.Enabled = false;
                foreach (TextBox field in new[] { title, start, end, offset, supplement }) field.Enabled = false;
                status.Text = "正在本地解析日期…";
                durationChoices.Enabled = false;
                Action releaseDraft = delegate {
                    busy = false;
                    applySupplement.Enabled = check.Enabled = import.Enabled = true;
                    durationChoices.Enabled = true;
                    foreach (TextBox field in new[] { title, start, end, offset, supplement }) field.Enabled = true;
                };
                try
                {
                    // Supplement the visible draft, not a superseded source date.
                    string draftTime = item.time_text ?? item.term ?? "";
                    DateTime knownStart;
                    if (DateTime.TryParseExact(start.Text.Trim(), "yyyy-MM-ddTHH:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out knownStart)) {
                        draftTime = knownStart.ToString("yyyy年M月d号HH:mm",
                            System.Globalization.CultureInfo.InvariantCulture);
                        if (Regex.IsMatch(offset.Text.Trim(), @"^[+-]\d{2}:\d{2}$"))
                            draftTime += " UTC" + offset.Text.Trim();
                    }
                    var payload = new Dictionary<string, object> {
                        { "operation", "clarify" }, { "time_text", draftTime },
                        { "supplement", supplement.Text.Trim() }
                    };
                    CalendarResponse response = await Task.Factory.StartNew(() => exporter(payload)).ConfigureAwait(false);
                    ApplyDraftOnUi(delegate {
                    try {
                    if (response == null || !response.ok)
                    {
                        status.Text = response == null ? "整理失败，请手动填写或重试。" : response.error;
                        return;
                    }
                    if ((!initial || String.IsNullOrWhiteSpace(start.Text)) && !String.IsNullOrWhiteSpace(response.start))
                        start.Text = response.start;
                    if ((!initial || String.IsNullOrWhiteSpace(end.Text)) && !String.IsNullOrWhiteSpace(response.end))
                        end.Text = response.end;
                    if ((!initial || String.IsNullOrWhiteSpace(offset.Text)) && !String.IsNullOrWhiteSpace(response.utc_offset))
                        offset.Text = response.utc_offset;
                    status.ForeColor = response.missing != null && response.missing.Count > 0
                        ? Color.FromArgb(180, 90, 25) : Color.FromArgb(48, 90, 150);
                    status.Text = response.message;
                    inferenceNotice.Text = response.year_inferred
                        ? "年份未注明：按当前日期取下一次到来的月日，请核对上方完整日期。" : "";
                    if ((response.message ?? "").Contains("开始时间已过去"))
                        inferenceNotice.Text += " 开始时间已过去，请核对；不会自动顺延到明年。";
                    inferenceNotice.Visible = inferenceNotice.Text.Length > 0;
                    RefreshDraftPresentation();
                    }
                    catch (Exception) { status.Text = "整理失败，请手动填写或重试。"; }
                    finally { releaseDraft(); }
                    });
                }
                catch (Exception) { ApplyDraftOnUi(delegate {
                    status.Text = "整理失败，请手动填写或检查服务连接。";
                    releaseDraft();
                }); }
            };
            applySupplement.Click += async delegate { await clarifyDraft(false); };
            Shown += async delegate {
                if (String.IsNullOrWhiteSpace(start.Text)) await clarifyDraft(true);
            };
            title.Text = item.title ?? "";
            start.Text = item.start_iso ?? "";
            end.Text = item.end_iso ?? "";
            offset.Text = String.IsNullOrWhiteSpace(item.utc_offset) ? "+08:00" : item.utc_offset;
            offset.Visible = false;
            layout.Controls.Add(offset);
            title.AccessibleName = "Reminder title";
            start.AccessibleName = "Reminder start";
            end.AccessibleName = "Reminder end";
            offset.AccessibleName = "Reminder UTC offset";
            title.MaxLength = 120;
            AddDraftField(layout, "事项", title);
            AddDraftField(layout, "开始（例：2026-09-20T14:00）", start);
            AddDraftField(layout, "结束（例：2026-09-20T15:00）", end);
            durationChoices.Items.AddRange(new object[] { "30 分钟", "1 小时", "2 小时", "手动填写结束时间" });
            layout.Controls.Add(durationLabel);
            layout.Controls.Add(durationChoices);
            start.TextChanged += delegate { UpdateDurationEnd(); RefreshDraftSummary(); };
            end.TextChanged += delegate {
                if (!updatingDurationEnd) {
                    selectedDurationMinutes = 0;
                    durationChoices.SelectedIndex = -1;
                }
            };
            durationChoices.SelectedIndexChanged += delegate {
                if (busy) return;
                if (durationChoices.SelectedIndex == 3) {
                    selectedDurationMinutes = 0;
                    RefreshDraftPresentation(); end.Focus(); return;
                }
                DateTime begins;
                if (!DateTime.TryParseExact(start.Text.Trim(), "yyyy-MM-ddTHH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out begins)) {
                    status.Text = "请先补齐有效的开始时间，再选择时长。"; return;
                }
                int[] minutes = { 30, 60, 120 };
                int choice = durationChoices.SelectedIndex;
                if (choice < 0 || choice >= minutes.Length) return;
                selectedDurationMinutes = minutes[choice];
                UpdateDurationEnd();
                RefreshDraftPresentation();
            };
            ComboBox reminderLead = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            reminderLead.Items.AddRange(new object[] { "准时提醒", "提前 5 分钟", "提前 10 分钟", "提前 30 分钟", "提前 60 分钟" });
            reminderLead.SelectedIndex = 2;
            AddField(layout, "本地通知时间", reminderLead);
            FlowLayoutPanel primaryActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            Button reminderButton = new Button { Text = "确认创建本地提醒", AutoSize = true };
            Button viewReminders = new Button { Text = "查看全部提醒", AutoSize = true, Visible = false };
            Button done = new Button { Text = "完成", AutoSize = true, Visible = false };
            reminderButton.AccessibleName = "Create local reminder";
            viewReminders.AccessibleName = "View local reminders";
            done.AccessibleName = "Close reminder confirmation";
            primaryActions.Controls.Add(reminderButton);
            primaryActions.Controls.Add(viewReminders);
            primaryActions.Controls.Add(done);
            layout.Controls.Add(primaryActions);
            viewReminders.Click += delegate { if (showReminders != null) showReminders(); };
            done.Click += delegate { Close(); };
            reminderButton.Click += delegate {
                if (busy || !PromptForMissingFields()) return;
                int[] leads = { 0, 5, 10, 30, 60 };
                LocalReminderResult result = reminderCreator(new LocalReminderRequest {
                    title = title.Text.Trim(), start = start.Text.Trim(), end = end.Text.Trim(),
                    utc_offset = offset.Text.Trim(), lead_minutes = leads[Math.Max(0, reminderLead.SelectedIndex)]
                });
                if (result == null || !result.ok)
                {
                    status.ForeColor = Color.FromArgb(180, 45, 45);
                    status.Text = result == null ? "本地提醒创建失败。" : result.error;
                    return;
                }
                status.ForeColor = Color.FromArgb(25, 120, 70);
                status.Text = "✓ 提醒已创建\r\n" + title.Text.Trim() + "\r\n" +
                    start.Text.Trim().Replace('T', ' ') + "（" + reminderLead.SelectedItem + "）\r\n" +
                    "实时字典在托盘运行时会准时通知；重启后记录仍会保留。";
                reminderButton.Enabled = false;
                applySupplement.Enabled = false;
                reminderLead.Enabled = false;
                durationChoices.Enabled = editDraft.Enabled = false;
                foreach (TextBox field in new[] { title, start, end, offset, supplement }) field.Enabled = false;
                viewReminders.Visible = true;
                done.Visible = true;
                if (reminderCreated != null) reminderCreated(result);
            };
            status.AutoSize = true;
            status.MaximumSize = new Size(480, 0);
            layout.Controls.Add(status);
            Button moreButton = new Button { Text = "其他方式：Outlook / 日历文件 ▾", AutoSize = true,
                FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(105, 112, 124),
                Margin = new Padding(3, 16, 3, 3) };
            TableLayoutPanel optionalLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1,
                Visible = false, Padding = new Padding(12, 2, 0, 0) };
            layout.Controls.Add(moreButton);
            layout.Controls.Add(optionalLayout);
            moreButton.Click += delegate {
                optionalLayout.Visible = !optionalLayout.Visible;
                moreButton.Text = optionalLayout.Visible
                    ? "其他方式：Outlook / 日历文件 ▴"
                    : "其他方式：Outlook / 日历文件 ▾";
            };
            Button outlookButton = new Button { Text = "可选：写入 Outlook / Microsoft 365 日历…", AutoSize = true };
            optionalLayout.Controls.Add(outlookButton);
            outlookButton.Click += delegate {
                if (busy || !PromptForMissingFields()) return;
                var payload = new Dictionary<string, object> {
                    { "title", title.Text.Trim() }, { "start", start.Text.Trim() },
                    { "end", end.Text.Trim() }, { "utc_offset", offset.Text.Trim() }
                };
                using (var dialog = new OutlookCalendarForm(payload, exporter)) dialog.ShowDialog(this);
            };
            Label info = new Label();
            info.Text = "需要日历文件时，可导入已有日历快照检查冲突，再确认导出。\r\n目前支持 UTC 固定事件；重复日程或其他时间格式需人工核对。";
            info.AutoSize = true;
            optionalLayout.Controls.Add(info);
            import.Text = "导入已有日历 (.ics)…";
            import.AutoSize = true;
            optionalLayout.Controls.Add(import);
            import.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Filter = "日历文件 (*.ics)|*.ics";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    checkedDraft = false;
                    calendarIcs = null;
                    export.Enabled = false;
                    try
                    {
                        if (new FileInfo(dialog.FileName).Length > 1024 * 1024)
                            throw new InvalidOperationException("日历文件不能超过 1 MiB。");
                        calendarIcs = File.ReadAllText(dialog.FileName, new UTF8Encoding(false, true));
                        checkedDraft = false;
                        export.Enabled = false;
                        status.Text = "日历已加载到内存，请补齐时间后点击检查。";
                    }
                    catch (Exception error) { status.Text = "导入失败：" + error.GetType().Name; }
                }
            };
            check.Text = "检查信息和时间冲突";
            check.AutoSize = true;
            optionalLayout.Controls.Add(check);
            check.Click += async delegate
            {
                if (busy) return;
                if (!PromptForMissingFields()) return;
                checkedDraft = false;
                export.Enabled = false;
                busy = true;
                check.Enabled = import.Enabled = false;
                foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = false;
                status.Text = "正在检查导入的日历…";
                Action releaseCheck = delegate {
                    busy = false;
                    check.Enabled = import.Enabled = true;
                    foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = true;
                };
                try
                {
                    var payload = new Dictionary<string, object> {
                        { "operation", "check" }, { "title", title.Text.Trim() },
                        { "start", start.Text.Trim() }, { "end", end.Text.Trim() },
                        { "utc_offset", offset.Text.Trim() }, { "calendar_ics", calendarIcs }
                    };
                    CalendarResponse result = await Task.Factory.StartNew(() => exporter(payload)).ConfigureAwait(false);
                    ApplyDraftOnUi(delegate {
                    try {
                    if (result == null || !result.ok)
                    {
                        status.Text = result == null ? "检查失败，请重试。" : result.error;
                        return;
                    }
                    status.Text = result.message;
                    if (result.conflicts != null && result.conflicts.Count > 0)
                        status.Text += "\r\n冲突：" + String.Join("、", result.conflicts.ToArray());
                    checkedDraft = result.state == "clear" || result.state == "conflict";
                    export.Enabled = checkedDraft;
                    }
                    catch (Exception) { status.Text = "检查失败，请重试。"; }
                    finally { releaseCheck(); }
                    });
                }
                catch (Exception) { ApplyDraftOnUi(delegate {
                    status.Text = "检查失败，请确认本地服务正在运行并重试。";
                    releaseCheck();
                }); }
            };
            export.Text = "确认并导出日历文件…";
            export.AutoSize = true;
            export.Enabled = false;
            optionalLayout.Controls.Add(export);
            foreach (TextBox field in new[] { title, start, end, offset })
                field.TextChanged += delegate { if (!busy) { checkedDraft = false; export.Enabled = false; ShowMissingFields(); } };
            Shown += delegate { RefreshDraftPresentation(); if (!busy) ShowMissingFields(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (busy) args.Cancel = true;
            };
            export.Click += async delegate
            {
                if (busy) return;
                if (!checkedDraft) return;
                if (MessageBox.Show(this, status.Text + "\r\n\r\n" + title.Text + "\r\n" +
                        start.Text + " 至 " + end.Text + " (UTC" + offset.Text + ")\r\n确认导出此日程？",
                        "确认日程", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                busy = true;
                check.Enabled = import.Enabled = false;
                export.Enabled = false;
                status.Text = "正在校验日程…";
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["title"] = title.Text.Trim();
                payload["start"] = start.Text.Trim();
                payload["end"] = end.Text.Trim();
                payload["utc_offset"] = offset.Text.Trim();
                payload["confirmed"] = true;
                foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = false;
                bool saved = false;
                Action releaseExport = delegate {
                    busy = false;
                    check.Enabled = import.Enabled = true;
                    export.Enabled = !saved;
                    foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = true;
                };
                try
                {
                    CalendarResponse response = await Task.Factory.StartNew(() => exporter(payload)).ConfigureAwait(false);
                    ApplyDraftOnUi(delegate {
                    try {
                    if (response == null || !response.ok)
                    {
                        status.Text = response == null ? "服务没有返回结果，请重试。" : response.error;
                        return;
                    }
                    using (SaveFileDialog dialog = new SaveFileDialog())
                    {
                        dialog.Filter = "日历文件 (*.ics)|*.ics";
                        dialog.FileName = response.filename;
                        dialog.DefaultExt = "ics";
                        dialog.OverwritePrompt = true;
                        if (dialog.ShowDialog(this) != DialogResult.OK)
                        {
                            status.Text = "已取消保存，未生成文件。";
                            return;
                        }
                        File.WriteAllText(dialog.FileName, response.ics, new UTF8Encoding(false));
                        saved = true;
                        status.Text = "文件已保存，请在日历软件中导入。\r\n重复导入的处理取决于日历软件。";
                    }
                    }
                    catch (Exception) { status.Text = "导出失败，请检查保存位置是否可写。"; }
                    finally { releaseExport(); }
                    });
                }
                catch (Exception)
                {
                    ApplyDraftOnUi(delegate {
                        status.Text = "导出失败，请检查实时字典是否启动及保存位置是否可写。";
                        releaseExport();
                    });
                }
            };
        }

        private bool PromptForMissingFields()
        {
            return UpdateMissingFields(true);
        }

        private bool UpdateMissingFields(bool focusFirst)
        {
            var fields = new[] { title, start, end };
            var names = new[] { "事项", "完整开始时间", "完整结束时间" };
            var missing = new List<string>();
            TextBox first = null;
            for (int index = 0; index < fields.Length; index++)
            {
                if (String.IsNullOrWhiteSpace(fields[index].Text))
                {
                    missing.Add(names[index]);
                    if (first == null) first = fields[index];
                }
            }
            if (missing.Count > 0)
            {
                status.ForeColor = Color.FromArgb(180, 90, 25);
                status.Text = "还缺少：" + String.Join("、", missing.ToArray()) +
                    "。\r\n可直接填写，或在“补充说明”中补充后点“整理补充信息”。";
                if (missing.Count == 1 && String.IsNullOrWhiteSpace(end.Text) && !draftExpanded)
                    status.Text = "还差时长。请选择持续多久，或选择“手动填写结束时间”。";
                if (focusFirst) first.Focus();
                return false;
            }
            return true;
        }

        private void ShowMissingFields()
        {
            RefreshDraftSummary();
            if (UpdateMissingFields(false))
            {
                status.ForeColor = Color.FromArgb(48, 90, 150);
                status.Text = "信息已完整。点击“确认创建本地提醒”后才会保存。";
            }
        }

        private void RefreshDraftPresentation()
        {
            RefreshDraftSummary();
            editDraft.Text = draftExpanded ? "收起已识别信息" : "修改已识别信息";
            foreach (var field in draftFields) {
                bool visible = draftExpanded || String.IsNullOrWhiteSpace(field.Key.Text);
                field.Key.Visible = field.Value.Visible = visible;
            }
            bool missing = draftFields.Keys.Any(field => String.IsNullOrWhiteSpace(field.Text));
            bool onlyEndMissing = String.IsNullOrWhiteSpace(end.Text) &&
                !String.IsNullOrWhiteSpace(title.Text) && !String.IsNullOrWhiteSpace(start.Text) &&
                !String.IsNullOrWhiteSpace(offset.Text);
            // One explicit duration fills the sole missing field; do not show
            // three competing ways to enter the same information by default.
            foreach (Control control in clarificationInputs) control.Visible = draftExpanded || (missing && !onlyEndMissing);
            if (onlyEndMissing && !draftExpanded && durationChoices.SelectedIndex != 3) {
                end.Visible = false;
                draftFields[end].Visible = false;
            }
            durationLabel.Visible = durationChoices.Visible = draftExpanded || String.IsNullOrWhiteSpace(end.Text);
            durationLabel.Text = onlyEndMissing ? "还差时长：选择后自动补齐结束时间" : "持续多久？选择后自动填写结束时间";
        }

        private void RefreshDraftSummary()
        {
            draftSummary.Text = "事项：" + (String.IsNullOrWhiteSpace(title.Text) ? "待补充" : title.Text) +
                "\r\n开始：" + (String.IsNullOrWhiteSpace(start.Text) ? "待补充" : start.Text.Replace('T', ' ')) +
                "\r\n结束：" + (String.IsNullOrWhiteSpace(end.Text) ? "待补充" : end.Text.Replace('T', ' ')) +
                (offset.Text == "+08:00" ? "（北京时间）" : "（原文指定时间）");
        }

        private void AddDraftField(TableLayoutPanel layout, string text, Control field)
        {
            Label label = new Label { Text = text, AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
            layout.Controls.Add(label);
            field.Width = 470;
            layout.Controls.Add(field);
            draftFields[field] = label;
        }

        private static void AddField(TableLayoutPanel layout, string label, Control field)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
            field.Width = 470;
            layout.Controls.Add(field);
        }
    }

}
