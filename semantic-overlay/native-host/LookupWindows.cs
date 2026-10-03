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
    internal sealed class DefinitionForm : Form
    {
        private readonly Label titleLabel;
        private readonly LinkLabel bodyLabel;
        private readonly Button closeButton;
        private readonly Button backButton;
        private readonly Button retryButton;
        private readonly Button copyButton;
        private readonly Button editTaskButton;
        private readonly LinkLabel feedbackLink;
        private HighlightItem taskCandidate;
        public event Action<HighlightItem> EditTaskRequested;
        private readonly LinkLabel sourceLink;
        private string currentExplanation;
        public event Action Dismissed;
        public event Action<string> TermClicked;
        public event Action BackRequested;
        public event Action RetryRequested;
        public event Action<string> FeedbackSubmitted;

        public DefinitionForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.White;
            ClientSize = new Size(390, 150);
            DoubleBuffered = true;

            titleLabel = new Label();
            titleLabel.Location = new Point(18, 14);
            titleLabel.Size = new Size(322, 28);
            titleLabel.ForeColor = Color.FromArgb(32, 78, 180);
            titleLabel.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11.0f, FontStyle.Bold);
            titleLabel.AutoEllipsis = true;
            Controls.Add(titleLabel);

            backButton = new Button();
            backButton.Text = "‹";
            backButton.AccessibleName = "Back to previous definition";
            backButton.FlatStyle = FlatStyle.Flat;
            backButton.FlatAppearance.BorderSize = 0;
            backButton.BackColor = Color.White;
            backButton.ForeColor = Color.FromArgb(105, 112, 124);
            backButton.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 14.0f, FontStyle.Regular);
            backButton.Location = new Point(302, 7);
            backButton.Size = new Size(38, 32);
            backButton.TabStop = false;
            backButton.Visible = false;
            backButton.Click += delegate
            {
                if (BackRequested != null)
                    BackRequested();
            };
            Controls.Add(backButton);

            closeButton = new Button();
            closeButton.Text = "×";
            closeButton.AccessibleName = "Close definition";
            closeButton.FlatStyle = FlatStyle.Flat;
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.BackColor = Color.White;
            closeButton.ForeColor = Color.FromArgb(105, 112, 124);
            closeButton.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12.0f, FontStyle.Regular);
            closeButton.Location = new Point(348, 7);
            closeButton.Size = new Size(34, 32);
            closeButton.TabStop = false;
            closeButton.Click += delegate
            {
                if (Dismissed != null)
                    Dismissed();
            };
            Controls.Add(closeButton);

            bodyLabel = new LinkLabel();
            bodyLabel.Location = new Point(18, 52);
            bodyLabel.Size = new Size(354, 80);
            bodyLabel.ForeColor = Color.FromArgb(48, 52, 60);
            bodyLabel.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10.0f, FontStyle.Regular);
            bodyLabel.LinkColor = Color.FromArgb(42, 107, 218);
            bodyLabel.ActiveLinkColor = Color.FromArgb(20, 77, 173);
            bodyLabel.VisitedLinkColor = Color.FromArgb(42, 107, 218);
            bodyLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            bodyLabel.AutoSize = false;
            bodyLabel.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                if (TermClicked != null)
                {
                    string linkedText = args.Link.LinkData as string;
                    if (string.IsNullOrEmpty(linkedText) && args.Link.Start >= 0 &&
                        args.Link.Start + args.Link.Length <= bodyLabel.Text.Length)
                        linkedText = bodyLabel.Text.Substring(args.Link.Start, args.Link.Length);
                    TermClicked(linkedText ?? string.Empty);
                }
            };
            Controls.Add(bodyLabel);

            feedbackLink = new LinkLabel();
            feedbackLink.Text = "反馈：有用 · 不需要标 · 解释不对";
            feedbackLink.AutoSize = false;
            feedbackLink.Size = new Size(350, 24);
            feedbackLink.LinkColor = Color.FromArgb(90, 96, 106);
            feedbackLink.ActiveLinkColor = Color.FromArgb(32, 78, 180);
            feedbackLink.LinkBehavior = LinkBehavior.HoverUnderline;
            feedbackLink.Visible = false;
            feedbackLink.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                string value = args.Link.LinkData as string;
                if (String.IsNullOrWhiteSpace(value)) return;
                feedbackLink.Text = "已记录，谢谢";
                feedbackLink.Links.Clear();
                if (FeedbackSubmitted != null) FeedbackSubmitted(value);
            };
            Controls.Add(feedbackLink);

            sourceLink = new LinkLabel();
            sourceLink.Text = "查看来源";
            sourceLink.AutoSize = true;
            sourceLink.LinkColor = Color.FromArgb(42, 107, 218);
            sourceLink.Visible = false;
            sourceLink.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                string url = args.Link.LinkData as string;
                Uri parsed;
                if (Uri.TryCreate(url, UriKind.Absolute, out parsed) &&
                    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = parsed.AbsoluteUri;
                    info.UseShellExecute = true;
                    Process.Start(info);
                }
            };
            Controls.Add(sourceLink);

            retryButton = new Button();
            retryButton.Text = "换个解释";
            retryButton.FlatStyle = FlatStyle.Flat;
            retryButton.FlatAppearance.BorderColor = Color.FromArgb(218, 222, 230);
            retryButton.BackColor = Color.White;
            retryButton.Size = new Size(104, 30);
            retryButton.Visible = false;
            retryButton.Click += delegate
            {
                retryButton.Enabled = false;
                retryButton.Text = "正在重写…";
                if (RetryRequested != null)
                    RetryRequested();
            };
            Controls.Add(retryButton);

            copyButton = new Button();
            copyButton.Text = "复制解释";
            copyButton.FlatStyle = FlatStyle.Flat;
            copyButton.FlatAppearance.BorderColor = Color.FromArgb(218, 222, 230);
            copyButton.BackColor = Color.White;
            copyButton.Size = new Size(108, 30);
            copyButton.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(currentExplanation))
                    return;
                try
                {
                    Clipboard.SetText(currentExplanation);
                    copyButton.Text = "已复制";
                }
                catch
                {
                    copyButton.Text = "复制失败";
                }
            };
            Controls.Add(copyButton);
            editTaskButton = new Button();
            editTaskButton.Text = "补全并设置提醒…";
            editTaskButton.AutoSize = true;
            editTaskButton.Visible = false;
            editTaskButton.Click += delegate
            {
                if (taskCandidate != null && EditTaskRequested != null)
                    EditTaskRequested(taskCandidate);
            };
            Controls.Add(editTaskButton);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
                parameters.ClassStyle |= NativeMethods.CsDropShadow;
                return parameters;
            }
        }

        public void ShowLoading(string term, Rectangle anchor, bool canGoBack, bool refresh)
        {
            SetContent(
                term,
                refresh ? "正在换一种解释…" : "正在查询…",
                null,
                null,
                anchor,
                canGoBack,
                false,
                false);
        }

        public void ShowDefinition(
            string term,
            string explanation,
            List<AnalysisEntity> entities,
            List<string> sources,
            Rectangle anchor,
            bool canGoBack,
            bool canRefresh)
        {
            SetContent(term, explanation, entities, sources, anchor, canGoBack, canRefresh, true);
        }

        public void ShowPreview(
            string term,
            string explanation,
            Rectangle anchor,
            bool canGoBack)
        {
            SetContent(term, explanation, null, null, anchor, canGoBack, false, false);
        }

        public void ShowTaskCandidate(HighlightItem item, Rectangle anchor)
        {
            string timeText = String.IsNullOrWhiteSpace(item.time_text) ? item.term : item.time_text;
            while (timeText.Contains(": ") || timeText.Contains("： "))
                timeText = timeText.Replace(": ", ":").Replace("： ", "：");
            string title = String.IsNullOrWhiteSpace(item.title) ? "待确认事项" : item.title;
            string explanation =
                "时间：" + timeText + "\r\n" +
                "事项：" + title + "\r\n" +
                "状态：等待确认，尚未写入任何日历。";
            SetContent("发现一个安排", explanation, null, null, anchor, false, false, false);
            taskCandidate = item;
            editTaskButton.Location = new Point(18, copyButton.Top);
            editTaskButton.Visible = true;
            copyButton.Text = "复制日程";
        }

        private void SetContent(
            string term,
            string explanation,
            List<AnalysisEntity> entities,
            List<string> sources,
            Rectangle anchor,
            bool canGoBack,
            bool canRefresh,
            bool showFeedback)
        {
            titleLabel.Text = term ?? string.Empty;
            taskCandidate = null;
            editTaskButton.Visible = false;
            bodyLabel.Text = explanation ?? string.Empty;
            currentExplanation = bodyLabel.Text;
            copyButton.Text = "复制解释";
            retryButton.Text = "换个解释";
            retryButton.Enabled = true;
            retryButton.Visible = canRefresh;
            feedbackLink.Text = "反馈：有用 · 不需要标 · 解释不对";
            feedbackLink.Links.Clear();
            feedbackLink.Links.Add(3, 2, "useful");
            feedbackLink.Links.Add(8, 4, "unnecessary_highlight");
            feedbackLink.Links.Add(15, 4, "wrong_explanation");
            feedbackLink.Visible = showFeedback;
            bodyLabel.Links.Clear();
            if (entities != null)
            {
                int occupiedUntil = -1;
                foreach (AnalysisEntity entity in entities)
                {
                    if (entity == null || string.IsNullOrEmpty(entity.text) ||
                        entity.start < 0 || entity.end <= entity.start ||
                        entity.end > bodyLabel.Text.Length || entity.start < occupiedUntil ||
                        !string.Equals(
                            bodyLabel.Text.Substring(entity.start, entity.end - entity.start),
                            entity.text,
                            StringComparison.Ordinal))
                        continue;
                    bodyLabel.Links.Add(entity.start, entity.end - entity.start, entity.text);
                    occupiedUntil = entity.end;
                }
            }
            backButton.Visible = canGoBack;
            Size measured = TextRenderer.MeasureText(
                bodyLabel.Text,
                bodyLabel.Font,
                new Size(bodyLabel.Width, 230),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int bodyHeight = Math.Max(48, Math.Min(230, measured.Height + 6));
            bodyLabel.Height = bodyHeight;
            string sourceUrl = null;
            if (sources != null)
            {
                foreach (string candidate in sources)
                {
                    Uri parsed;
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out parsed) &&
                        (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                    {
                        sourceUrl = parsed.AbsoluteUri;
                        break;
                    }
                }
            }
            int footerTop = bodyLabel.Top + bodyHeight + 8;
            feedbackLink.Location = new Point(18, footerTop);
            sourceLink.Location = new Point(286, footerTop + 3);
            sourceLink.Visible = sourceUrl != null;
            sourceLink.Links.Clear();
            if (sourceUrl != null)
                sourceLink.Links.Add(0, sourceLink.Text.Length, sourceUrl);
            int actionTop = footerTop + (showFeedback || sourceUrl != null ? 28 : 0);
            retryButton.Location = new Point(150, actionTop);
            copyButton.Location = new Point(264, actionTop);
            ClientSize = new Size(390, actionTop + copyButton.Height + 12);
            PositionFor(anchor);
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopMost,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            Invalidate();
        }

        public void PositionFor(Rectangle anchor)
        {
            Rectangle working = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left;
            int y = anchor.Bottom + 9;
            if (x + Width > working.Right - 8)
                x = working.Right - Width - 8;
            if (x < working.Left + 8)
                x = working.Left + 8;
            if (y + Height > working.Bottom - 8)
                y = anchor.Top - Height - 9;
            if (y < working.Top + 8)
                y = working.Top + 8;
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(Color.FromArgb(218, 222, 230), 1))
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class ApiKeyForm : Form
    {
        private readonly ComboBox providerBox;
        private readonly TextBox baseUrlBox;
        private readonly TextBox modelBox;
        private readonly TextBox keyBox;
        private readonly Button testButton;
        private readonly Button saveButton;
        private readonly Label statusLabel;
        private readonly Func<string, string, string, KeyValidationResult> validator;
        private string validatedSignature;

        public string ApiKey
        {
            get { return keyBox.Text; }
        }

        public string BaseUrl
        {
            get { return baseUrlBox.Text.Trim(); }
        }

        public string Model
        {
            get { return modelBox.Text.Trim(); }
        }

        public ApiKeyForm(Func<string, string, string, KeyValidationResult> validator)
        {
            this.validator = validator;
            Text = "配置解释模型";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(520, 360);
            Font = SystemFonts.MessageBoxFont;

            Label title = new Label();
            title.Text = "配置解释与语音模型服务";
            title.AutoSize = true;
            title.Font = new Font(Font, FontStyle.Bold);
            title.Location = new Point(18, 18);
            Controls.Add(title);

            Label note = new Label();
            note.Text = "该服务负责中文解释和语音转写；密钥不会写进安装包。";
            note.AutoSize = true;
            note.ForeColor = Color.FromArgb(90, 96, 106);
            note.Location = new Point(18, 48);
            Controls.Add(note);

            Label providerLabel = new Label();
            providerLabel.Text = "服务商";
            providerLabel.AutoSize = true;
            providerLabel.Location = new Point(21, 84);
            Controls.Add(providerLabel);

            providerBox = new ComboBox();
            providerBox.DropDownStyle = ComboBoxStyle.DropDownList;
            providerBox.Items.Add("硅基流动（推荐）");
            providerBox.Items.Add("DeepSeek 官方");
            providerBox.Items.Add("其他 OpenAI 兼容服务");
            providerBox.Location = new Point(92, 79);
            providerBox.Size = new Size(407, 27);
            Controls.Add(providerBox);

            Label baseUrlLabel = new Label();
            baseUrlLabel.Text = "接口地址";
            baseUrlLabel.AutoSize = true;
            baseUrlLabel.Location = new Point(21, 123);
            Controls.Add(baseUrlLabel);

            baseUrlBox = new TextBox();
            baseUrlBox.Location = new Point(92, 118);
            baseUrlBox.Size = new Size(407, 26);
            Controls.Add(baseUrlBox);

            Label modelLabel = new Label();
            modelLabel.Text = "模型名称";
            modelLabel.AutoSize = true;
            modelLabel.Location = new Point(21, 161);
            Controls.Add(modelLabel);

            modelBox = new TextBox();
            modelBox.Location = new Point(92, 156);
            modelBox.Size = new Size(407, 26);
            Controls.Add(modelBox);

            Label keyLabel = new Label();
            keyLabel.Text = "API Key";
            keyLabel.AutoSize = true;
            keyLabel.Location = new Point(21, 201);
            Controls.Add(keyLabel);

            keyBox = new TextBox();
            keyBox.Location = new Point(92, 196);
            keyBox.Size = new Size(407, 26);
            keyBox.UseSystemPasswordChar = true;
            Controls.Add(keyBox);

            statusLabel = new Label();
            statusLabel.Text = "请先测试连接";
            statusLabel.AutoEllipsis = true;
            statusLabel.Location = new Point(21, 236);
            statusLabel.Size = new Size(478, 42);
            statusLabel.ForeColor = Color.FromArgb(90, 96, 106);
            Controls.Add(statusLabel);

            testButton = new Button();
            testButton.Text = "测试连接";
            testButton.Location = new Point(243, 304);
            testButton.Size = new Size(88, 32);
            testButton.Click += TestConnection;
            Controls.Add(testButton);

            saveButton = new Button();
            saveButton.Text = "保存并应用";
            saveButton.DialogResult = DialogResult.OK;
            saveButton.Enabled = false;
            saveButton.Location = new Point(339, 304);
            saveButton.Size = new Size(88, 32);
            Controls.Add(saveButton);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(435, 304);
            cancel.Size = new Size(64, 32);
            Controls.Add(cancel);

            AcceptButton = testButton;
            CancelButton = cancel;
            providerBox.SelectedIndexChanged += delegate
            {
                if (providerBox.SelectedIndex == 0)
                    baseUrlBox.Text = "https://api.siliconflow.cn/v1";
                else if (providerBox.SelectedIndex == 1)
                    baseUrlBox.Text = "https://api.deepseek.com";
                baseUrlBox.ReadOnly = providerBox.SelectedIndex != 2;
                if (providerBox.SelectedIndex == 0)
                    modelBox.Text = "Qwen/Qwen2.5-7B-Instruct";
                else if (providerBox.SelectedIndex == 1)
                    modelBox.Text = "deepseek-v4-flash";
                InvalidateValidation();
            };
            keyBox.TextChanged += delegate { InvalidateValidation(); };
            baseUrlBox.TextChanged += delegate { InvalidateValidation(); };
            modelBox.TextChanged += delegate { InvalidateValidation(); };
            providerBox.SelectedIndex = 0;
            Shown += delegate { keyBox.Focus(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (DialogResult == DialogResult.OK &&
                    !String.Equals(validatedSignature, CurrentSignature(), StringComparison.Ordinal))
                {
                    args.Cancel = true;
                    MessageBox.Show(
                        this,
                        "请先测试当前服务商、模型和 API Key。",
                        "实时字典",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    keyBox.Focus();
                }
            };
        }

        private string CurrentSignature()
        {
            return keyBox.Text.Trim() + "\n" + BaseUrl + "\n" + Model;
        }

        private void InvalidateValidation()
        {
            validatedSignature = null;
            if (saveButton != null)
                saveButton.Enabled = false;
            if (statusLabel != null)
            {
                statusLabel.Text = "请先测试连接";
                statusLabel.ForeColor = Color.FromArgb(90, 96, 106);
            }
        }

        private void TestConnection(object sender, EventArgs args)
        {
            string key = keyBox.Text.Trim();
            if (key.Length < 8)
            {
                statusLabel.Text = "请输入有效的 API Key";
                statusLabel.ForeColor = Color.Firebrick;
                return;
            }
            if (BaseUrl.Length == 0 || Model.Length == 0)
            {
                statusLabel.Text = "接口地址和模型名称不能为空";
                statusLabel.ForeColor = Color.Firebrick;
                return;
            }
            testButton.Enabled = false;
            saveButton.Enabled = false;
            providerBox.Enabled = false;
            baseUrlBox.Enabled = false;
            modelBox.Enabled = false;
            keyBox.Enabled = false;
            statusLabel.Text = "正在连接解释模型服务…";
            statusLabel.ForeColor = Color.FromArgb(90, 96, 106);
            string baseUrl = BaseUrl;
            string model = Model;
            Task.Factory.StartNew(delegate { return validator(key, baseUrl, model); })
                .ContinueWith(delegate(Task<KeyValidationResult> task)
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            testButton.Enabled = true;
                            providerBox.Enabled = true;
                            baseUrlBox.Enabled = true;
                            modelBox.Enabled = true;
                            keyBox.Enabled = true;
                            if (task.IsFaulted || task.Result == null || !task.Result.ok)
                            {
                                validatedSignature = null;
                                statusLabel.Text = task.IsFaulted
                                    ? "连接失败：" + task.Exception.GetBaseException().Message
                                    : task.Result.message;
                                statusLabel.ForeColor = Color.Firebrick;
                                keyBox.Focus();
                                return;
                            }
                            validatedSignature = key + "\n" + baseUrl + "\n" + model;
                            saveButton.Enabled = true;
                            AcceptButton = saveButton;
                            statusLabel.Text = task.Result.message;
                            statusLabel.ForeColor = Color.FromArgb(25, 125, 70);
                        }));
                    }
                    catch
                    {
                    }
                });
        }
    }

}
