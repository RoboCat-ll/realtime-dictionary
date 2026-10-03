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
    internal sealed class SelectionActionForm : Form
    {
        internal event Action ExplainRequested;
        internal string SelectedText { get; private set; }
        internal Rectangle SelectedRegion { get; private set; }
        internal DateTime ExpiresUtc { get; private set; }
        internal SelectionActionForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            // Form.TopMost activates during CreateHandle on .NET Framework.
            // Apply topmost only through SetWindowPos with SWP_NOACTIVATE.
            StartPosition = FormStartPosition.Manual;
            Size = new Size(126, 36); BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10); Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetWindowDisplayAffinity(Handle, 0x11);
        }
        protected override CreateParams CreateParams
        {
            get {
                var value = base.CreateParams;
                value.ExStyle |= NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
                value.ClassStyle |= NativeMethods.CsDropShadow;
                return value;
            }
        }
        internal static Rectangle DragRegion(Point start, Point end, Rectangle window)
        {
            // Drag endpoints are only an approximation; OCR text must be reviewed.
            int deltaX = Math.Abs(start.X - end.X);
            int deltaY = Math.Abs(start.Y - end.Y);
            if ((deltaX < 8 && deltaY < 12) || deltaY > 360 || deltaX > 1000)
                return Rectangle.Empty;
            int left = Math.Min(start.X, end.X) - 6;
            int right = Math.Max(start.X, end.X) + 6;
            if (right - left < 80)
            {
                int center = (left + right) / 2;
                left = center - 40;
                right = center + 40;
            }
            Rectangle region = Rectangle.FromLTRB(left,
                Math.Min(start.Y, end.Y) - 18, right,
                Math.Max(start.Y, end.Y) + 18);
            region.Intersect(window);
            return region.Width < 8 || region.Height < 10 ? Rectangle.Empty : region;
        }
        internal void Present(string text, Point endpoint, Rectangle region)
        {
            SelectedText = text; SelectedRegion = region;
            ExpiresUtc = DateTime.UtcNow.AddSeconds(8);
            Rectangle area = Screen.FromPoint(endpoint).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(endpoint.X, area.Right - Width)),
                endpoint.Y + 22 + Height <= area.Bottom ? endpoint.Y + 22 : Math.Max(area.Top, endpoint.Y - Height - 22));
            NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost, Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawRectangle(Pens.LightGray, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, "解释这段",
                Font, new Rectangle(8, 0, 91, Height), Color.FromArgb(30, 60, 140),
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, "×", Font, new Rectangle(100, 0, 25, Height), Color.Gray,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= 100) Hide();
            else if (ExplainRequested != null) ExplainRequested();
        }
    }

    internal sealed partial class SelectionAnalysisForm : Form
    {
        private readonly ServiceManager services;
        private readonly Action<HighlightItem, Form> openReminderEditor;
        private readonly Label heading = new Label {
            Text = "这句话的意思", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft };
        private readonly TextBox source = new TextBox { Multiline = true, MaxLength = 1000,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Label sourceNotice = new Label { Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label status = new Label { Dock = DockStyle.Fill,
            ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        private readonly TextBox body = new TextBox { Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.None, Dock = DockStyle.Fill, BackColor = Color.White };
        private readonly Label sentenceTitle = new Label { Text = "原句（可点击或选取词语）",
            Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft };
        private readonly RichTextBox sentence = new RichTextBox { Dock = DockStyle.Fill,
            ReadOnly = true, DetectUrls = false, HideSelection = false,
            BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = new Padding(0), BackColor = Color.FromArgb(246, 249, 253) };
        private readonly Button explainSelected = new Button { Text = "解释选中词语",
            AutoSize = true, Height = 30, Enabled = false, Margin = new Padding(3) };
        private readonly Button nearbyExplain = new Button { Text = "解释",
            Size = new Size(76, 28), TabStop = false, Visible = false,
            BackColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button expandTerm = new Button { Text = "展开解释",
            Width = 94, Dock = DockStyle.Right, Enabled = false };
        private readonly Func<string, string, string, LookupResponse> lookupTerm;
        private readonly List<Tuple<int, int, SelectionTerm>> linkedTerms =
            new List<Tuple<int, int, SelectionTerm>>();
        private readonly Font termLinkFont;
        private readonly FlowLayoutPanel terms = new FlowLayoutPanel { Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true };
        private readonly Panel termHeader = new Panel { Dock = DockStyle.Fill };
        private readonly Label termHeading = new Label { Text = "知识点 · 点击查看",
            Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft };
        private readonly TextBox termBody = new TextBox { Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.None, Dock = DockStyle.Fill, BackColor = Color.White,
            Text = "点击上方高亮词语查看它在这句话里的含义。" };
        private readonly Label taskHeading = new Label { Text = "工作与日程 · 待确认",
            Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 92, 190), TextAlign = ContentAlignment.MiddleLeft };
        private readonly FlowLayoutPanel tasks = new FlowLayoutPanel { Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true };
        private readonly LinkLabel editSource = new LinkLabel {
            Text = "识别有误？修改原文", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, LinkBehavior = LinkBehavior.HoverUnderline };
        private readonly Button analyze = new Button { Text = "确认并解释", Width = 112, Height = 30 };
        private readonly TableLayoutPanel layout;
        private int generation;
        private int termGeneration;
        private bool settingText;
        private bool sourceEditorVisible;
        private string passageText = String.Empty;
        private string passageExplanation = String.Empty;
        private string sourceApp = "other";
        private string textSource = "ocr";
        private string initialText = String.Empty;
        private string nearbySelectionText = String.Empty;
        private string currentTermText = String.Empty;
        private string briefTermExplanation = String.Empty;
        private string expandedTermExplanation = String.Empty;
        private bool termDetailsExpanded;
        private bool briefLookupFailed;
        private readonly Func<string, bool, bool, SelectionAnalysisResponse> analyzePassage;

        internal SelectionAnalysisForm(ServiceManager service,
            Action<HighlightItem, Form> reminderEditor,
            Func<string, string, string, LookupResponse> termLookup = null,
            Func<string, bool, bool, SelectionAnalysisResponse> passageAnalyzer = null)
        {
            services = service;
            lookupTerm = termLookup ?? delegate(string term, string context, string detail) {
                services.EnsureRunning();
                return services.Lookup(term, context, false, null, detail);
            };
            analyzePassage = passageAnalyzer ?? delegate(string text, bool correction, bool refresh) {
                services.EnsureRunning();
                return services.AnalyzeSelection(text, correction, refresh);
            };
            openReminderEditor = reminderEditor;
            Text = "消息解释";
            Font = new Font("Microsoft YaHei UI", 9);
            termLinkFont = new Font(sentence.Font, FontStyle.Underline);
            InitializeFloatShell();

            layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 14, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 0：标题已并入 grip 拖动条
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));  // 1：状态行
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // 2：整句含义（UpdateCardLayout 按内容调整）
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));  // 3：原句小标题
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // 4：原句（按内容调整）
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));  // 5：选词操作行
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 6：术语区由词语视图接管（v2）
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // 7：同上
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));  // 10：识别有误链接
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // 13：底部按钮行
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            actions.Controls.Add(analyze);
            MountHeading(heading);
            layout.Controls.Add(status, 0, 1);
            layout.Controls.Add(body, 0, 2);
            layout.Controls.Add(sentenceTitle, 0, 3);
            layout.Controls.Add(sentence, 0, 4);
            layout.Controls.Add(terms, 0, 5);
            termHeader.Controls.Add(termHeading);
            termHeader.Controls.Add(expandTerm);
            layout.Controls.Add(termHeader, 0, 6);
            layout.Controls.Add(termBody, 0, 7);
            layout.Controls.Add(taskHeading, 0, 8);
            layout.Controls.Add(tasks, 0, 9);
            layout.Controls.Add(editSource, 0, 10);
            layout.Controls.Add(source, 0, 11);
            layout.Controls.Add(sourceNotice, 0, 12);
            layout.Controls.Add(actions, 0, 13);
            Controls.Add(layout);
            layout.AutoScroll = true;
            BuildWordView();
            AttachGrip();
            UpdateCardLayout();
            Controls.Add(nearbyExplain);
            nearbyExplain.BringToFront();

            analyze.Click += async delegate { await RunAnalysis(true); };
            editSource.LinkClicked += delegate { SetSourceEditorVisible(!sourceEditorVisible); };
            sentence.SelectionChanged += delegate {
                explainSelected.Enabled = !String.IsNullOrWhiteSpace(sentence.SelectedText);
                HideNearbySelectionAction();
            };
            sentence.MouseUp += async delegate(object sender, MouseEventArgs args) {
                if (args.Button != MouseButtons.Left) return;
                if (sentence.SelectionLength != 0) { ShowNearbySelectionAction(); return; }
                SelectionTerm item = LinkedTermAt(sentence.GetCharIndexFromPosition(args.Location));
                if (item != null) await ShowTerm(item);
            };
            sentence.KeyUp += delegate(object sender, KeyEventArgs args) {
                if (args.KeyCode == Keys.Escape || (args.Control && args.KeyCode == Keys.Enter))
                    HideNearbySelectionAction();
                else ShowNearbySelectionAction();
            };
            sentence.VScroll += delegate { HideNearbySelectionAction(); };
            sentence.HScroll += delegate { HideNearbySelectionAction(); };
            LocationChanged += delegate { HideNearbySelectionAction(); };
            Resize += delegate { HideNearbySelectionAction(); };
            Deactivate += delegate { HideNearbySelectionAction(); };
            sentence.MouseDown += delegate { HideNearbySelectionAction(); };
            body.MouseDown += delegate { HideNearbySelectionAction(); };
            termBody.MouseDown += delegate { HideNearbySelectionAction(); };
            source.MouseDown += delegate { HideNearbySelectionAction(); };
            terms.MouseDown += delegate { HideNearbySelectionAction(); };
            nearbyExplain.Click += async delegate {
                string selected = nearbySelectionText;
                HideNearbySelectionAction();
                if (String.IsNullOrWhiteSpace(selected) ||
                    !String.Equals(selected, GetSelectedLookupTerm(), StringComparison.Ordinal)) return;
                await ShowTerm(new SelectionTerm { text = selected });
            };
            expandTerm.Click += async delegate { await ToggleTermDetails(); };
            sentence.KeyDown += async delegate(object sender, KeyEventArgs args) {
                if (!args.Control || args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                await ExplainSelectedTerm();
            };
            explainSelected.Click += async delegate { await ExplainSelectedTerm(); };
            source.TextChanged += delegate
            {
                if (settingText) return;
                generation++;
                termGeneration++;
                ResetTermDetails();
                ShowSentenceView();
                passageExplanation = String.Empty;
                passageText = source.Text.Trim();
                MarkExplanationStale();   // v2：旧结果置灰标注失效，不静默清空
                sentence.Text = source.Text;
                linkedTerms.Clear();
                terms.Controls.Clear();
                explainSelected.Enabled = false;
                tasks.Controls.Clear();
                SetTaskSectionVisible(false);
                termHeading.Text = "知识点 · 点击查看";
                termBody.Text = "重新解释后可查看词语注释。";
                analyze.Text = "确认并解释";
                analyze.Enabled = source.Text.Trim().Length > 0 && source.Text.Trim().Length <= 1000;
                status.Text = analyze.Enabled ? "原文已修改，解释已失效；确认后才会发送。" : "请输入 1–1000 个字符。";
            };
            FormClosed += delegate { generation++; termGeneration++; termLinkFont.Dispose(); };
        }

        internal void OpenText(string text, bool exactSelection, string textSource,
            string originatingApp)
        {
            OpenText(text, exactSelection, textSource, originatingApp, exactSelection);
        }

        internal void OpenText(string text, bool exactSelection, string textSource,
            string originatingApp, bool autoAnalyze)
        {
            sourceApp = originatingApp ?? "other";
            this.textSource = textSource ?? (exactSelection ? "accessibility" : "ocr");
            passageText = String.Empty;
            passageExplanation = String.Empty;
            body.Clear();
            ClearExplanationStale();
            ResetTermDetails();
            sentence.Text = String.Empty;
            linkedTerms.Clear();
            terms.Controls.Clear();
            explainSelected.Enabled = false;
            tasks.Controls.Clear();
            SetTaskSectionVisible(false);
            termHeading.Text = "知识点 · 点击查看";
            termBody.Text = "点击上方高亮词语查看它在这句话里的含义。";
            int request = ++generation;
            termGeneration++;
            string value = (text ?? String.Empty).Trim();
            initialText = value;
            settingText = true;
            source.Text = value.Length <= 1000 ? value : String.Empty;
            settingText = false;
            Show();
            PlaceInitialIfAnchored();   // v2：不抢焦点，出现在目标消息附近
            // On some QQ foreground transitions WinForms reports Visible while the
            // native window loses WS_VISIBLE. Keep the initial presentation honest.
            int visibilityChecks = 0;
            System.Windows.Forms.Timer visibilityTimer = new System.Windows.Forms.Timer { Interval = 250 };
            visibilityTimer.Tick += delegate
            {
                visibilityChecks++;
                // 浮框现在可随时关闭（Esc/✕/跟随策略）：Dispose 期间禁止触碰
                // Handle（读取会在销毁后重建句柄，导致 CreateHandle/Dispose 冲突）。
                if (!IsDisposed && !Disposing && WindowState != FormWindowState.Minimized &&
                    !NativeMethods.IsWindowVisible(Handle))
                {
                    NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
                    NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost,
                        Left, Top, Width, Height,
                        NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
                    if (services != null) services.Log("Selection panel native visibility restored");
                }
                if (IsDisposed || Disposing || visibilityChecks >= 6)
                {
                    visibilityTimer.Stop();
                    visibilityTimer.Dispose();
                }
            };
            FormClosed += delegate { visibilityTimer.Stop(); visibilityTimer.Dispose(); };
            visibilityTimer.Start();
            if (value.Length == 0)
            {
                sourceNotice.Text = "没有读到所选文字，请重新拖选。";
                status.Text = String.Empty;
                return;
            }
            if (value.Length > 1000)
            {
                sourceNotice.Text = "所选文字超过 1000 个字符，请缩小选区后重试。";
                status.Text = "内容没有被截断或发送。";
                analyze.Enabled = false;
                return;
            }
            analyze.Enabled = true;
            if (autoAnalyze)
            {
                SetSourceEditorVisible(false);
                sourceNotice.Text = exactSelection
                    ? "已读取这条消息的完整文字，只会发送这一条。"
                    : "已定位并识别整个消息气泡，正在解释；如有错误可直接修改。";
                analyze.Text = "重新解释";
                BeginInvoke(new Action(async delegate
                {
                    if (!IsDisposed && request == generation) await RunAnalysis(false);
                }));
            }
            else
            {
                SetSourceEditorVisible(true);
                sourceNotice.Text = "这是选区 OCR 结果。请先校对，确认后才会发送。";
                analyze.Text = "确认并解释";
                source.Focus();
                source.SelectionStart = source.TextLength;
            }
            if (services != null) services.Log("Selection panel opened (" + value.Length + " chars, " +
                (textSource ?? "unknown") + ", " + sourceApp + ")");
        }

        private async Task RunAnalysis(bool forceRefresh)
        {
            string value = source.Text.Trim();
            if (value.Length == 0)
            {
                status.Text = "请保留至少一个字符。";
                return;
            }
            if (value.Length > 1000)
            {
                status.Text = "所选文字超过 1000 个字符，请缩小选区。";
                return;
            }
            int request = ++generation;
            termGeneration++;
            passageText = value;
            ResetTermDetails();
            ShowSentenceView();
            passageExplanation = String.Empty;
            analyze.Enabled = false;
            terms.Controls.Clear();
            tasks.Controls.Clear();
            sentence.Text = value;
            SetTaskSectionVisible(false);
            linkedTerms.Clear();
            explainSelected.Enabled = false;
            sentenceTitle.Text = "原句";
            termHeading.Text = "知识点 · 点击查看";
            termBody.Text = "正在等待整句分析…";
            ClearExplanationStale();
            body.Text = "正在理解这段话…";
            status.Text = "只分析当前消息";
            Stopwatch watch = Stopwatch.StartNew();
            RequestProgress progress = new RequestProgress(this, delegate { return request == generation; },
                delegate(string message) { status.Text = message; }, "正在理解当前消息");
            SelectionAnalysisResponse response = null;
            Exception failure = null;
            try
            {
                response = await Task.Factory.StartNew(delegate
                {
                    bool allowCorrection = textSource == "bubble_ocr" || textSource == "ocr";
                    return analyzePassage(value, allowCorrection, forceRefresh);
                });
            }
            catch (Exception error)
            {
                failure = error;
            }
            // 无同步上下文时 await 续写会落在线程池：结果应用一律经 UI 线程编组，
            // 并在 UI 线程上重新评估失效守卫，与用户编辑严格串行（迟到结果不得覆盖当前内容）。
            if (IsDisposed || Disposing || request != generation)
            {
                progress.Dispose();
                return;
            }
            if (InvokeRequired)
            {
                BeginInvoke(new Action(delegate {
                    ApplyAnalysisOutcome(request, response, failure, watch, value, progress); }));
                return;
            }
            ApplyAnalysisOutcome(request, response, failure, watch, value, progress);
        }

        private void ApplyAnalysisOutcome(int request, SelectionAnalysisResponse response,
            Exception failure, Stopwatch watch, string value, RequestProgress progress)
        {
            try
            {
                if (IsDisposed || Disposing || request != generation) return;
                watch.Stop();
                if (failure != null) throw failure;
                passageExplanation = response == null ? String.Empty : response.explanation ?? String.Empty;
                passageText = response == null || String.IsNullOrWhiteSpace(response.display_text)
                    ? value : response.display_text.Trim();
                body.Text = String.IsNullOrWhiteSpace(passageExplanation)
                    ? "暂时没有可靠的整段解释，请重试。" : passageExplanation;
                ClearExplanationStale();
                sentence.Text = passageText;
                sentenceTitle.Text = response != null && response.ocr_corrected
                    ? "原句（已校正，可选取词语）" : "原句（可选取词语）";
                status.Text = response != null && response.analysis_mode == "model"
                    ? "整句解释 · 模型结果" : "整句解释 · 本地结果";
                analyze.Text = response != null && response.can_retry ? "重新解释" : "再次检查";
                RenderTermsSafely(response == null ? null : response.terms);
                RenderTasks(response == null ? null : response.actions,
                    response == null ? null : response.action_status);
                if (services != null) services.RecordSelectionMetric(
                    sourceApp, textSource,
                    response == null ? "empty" : response.analysis_mode,
                    (int)Math.Min(Int32.MaxValue, watch.ElapsedMilliseconds),
                    !String.Equals(initialText, value, StringComparison.Ordinal),
                    response != null && response.analysis_mode == "model" &&
                        !String.IsNullOrWhiteSpace(response.explanation),
                    response == null || response.terms == null ? 0 : response.terms.Count);
                if (services != null) services.Log("Selection analysis completed: " +
                    (response == null ? "empty" : response.analysis_mode));
            }
            catch (Exception error)
            {
                if (!IsDisposed && request == generation)
                {
                    body.Text = RequestFeedback.For(error);
                    status.Text = "解释失败";
                    analyze.Text = "重试";
                    if (services != null) services.Log("Selection analysis failed: " + error.GetType().Name);
                }
            }
            finally
            {
                progress.Dispose();
                if (!IsDisposed && request == generation)
                {
                    analyze.Enabled = true;
                }
            }
        }

        private void RenderTerms(List<SelectionTerm> items)
        {
            // 术语渲染是内容变化点：先按当前原句内容重排行高（正文随内容增长），
            // 保证着色过程的选择操作不会把可视区滚出首行。
            UpdateCardLayout();
            terms.Controls.Clear();
            terms.Controls.Add(explainSelected);
            linkedTerms.Clear();
            sentence.SelectAll();
            sentence.SelectionColor = Color.Black;
            sentence.SelectionFont = sentence.Font;
            sentence.DeselectAll();
            if (items == null || items.Count == 0)
            {
                terms.Controls.Add(new Label { Text = "没标出想查的词？在原句中拖选后点左侧按钮。",
                    AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 9, 3, 3) });
                termHeading.Text = "知识点 · 按需解释";
                termBody.Text = "选取原句中的任意词语，再点“解释选中词语”。";
                return;
            }
            var unique = new List<SelectionTerm>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SelectionTerm item in items)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.text)) continue;
                string termText = item.text.Trim();
                if (!seen.Add(termText)) continue;
                unique.Add(new SelectionTerm { text = termText, explanation = item.explanation });
                if (unique.Count >= 5) break;
            }
            if (unique.Count == 0)
            {
                RenderTerms(null);
                return;
            }
            var linkedRanges = new List<Tuple<int, int>>();
            string displayed = sentence.Text;
            foreach (SelectionTerm item in unique.OrderByDescending(value => value.text.Length))
            {
                int start = 0;
                while (start < displayed.Length)
                {
                    int found = displayed.IndexOf(item.text, start, StringComparison.Ordinal);
                    if (found < 0) break;
                    int end = found + item.text.Length;
                    bool overlaps = linkedRanges.Any(range =>
                        found < range.Item2 && end > range.Item1);
                    if (!overlaps)
                    {
                        sentence.Select(found, item.text.Length);
                        sentence.SelectionColor = Color.FromArgb(20, 92, 190);
                        sentence.SelectionFont = termLinkFont;
                        linkedTerms.Add(Tuple.Create(found, end, item));
                        linkedRanges.Add(Tuple.Create(found, end));
                    }
                    start = found + item.text.Length;
                }
            }
            sentence.DeselectAll();
            // 术语着色过程中的选择操作会把插入符带到文末，短文本框随之滚动到底部；
            // 回到顶部对齐，用户从原句开头读起，字符-坐标映射也保持可见区域内。
            sentence.Select(0, 0);
            sentence.ScrollToCaret();
            explainSelected.Enabled = false;
            termHeading.Text = "知识点 · 点击查看";
            termBody.Text = "点击蓝色词语，或拖选未标出的词语后点“解释选中词语”。";
        }

        private void RenderTermsSafely(List<SelectionTerm> items)
        {
            try { RenderTerms(items); }
            catch (Exception error)
            {
                linkedTerms.Clear();
                terms.Controls.Clear();
                terms.Controls.Add(explainSelected);
                terms.Controls.Add(new Label {
                    Text = "术语暂时无法标出，仍可拖选原句查词。",
                    AutoSize = true, ForeColor = Color.DimGray,
                    Margin = new Padding(3, 9, 3, 3)
                });
                termHeading.Text = "知识点 · 按需解释";
                termBody.Text = "选取原句中的词语，再点“解释选中词语”。";
                if (services != null) services.Log("Selection term rendering failed: " + error.GetType().Name);
            }
        }

        private void MarkExplanationStale()
        {
            if (body.Text.Length > 0)
                body.ForeColor = Color.FromArgb(150, 153, 160);
        }

        private void ClearExplanationStale()
        {
            body.ForeColor = Color.FromArgb(31, 35, 41);
        }

        private static int MeasureGrow(Control box, string text, Font font, int min, int max)
        {
            bool ignored;
            return MeasureGrow(box, text, font, min, max, out ignored);
        }

        private static int MeasureGrow(Control box, string text, Font font, int min, int max,
            out bool overflow)
        {
            overflow = false;
            if (String.IsNullOrEmpty(text)) return min;
            int width = box.ClientSize.Width > 0 ? box.ClientSize.Width : 300;
            Size measured = TextRenderer.MeasureText(text, font,
                new Size(Math.Max(60, width), Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int height = measured.Height + 8;
            overflow = height > max;
            return Math.Max(min, Math.Min(max, height));
        }

        // TextBox 的 ScrollBars.Vertical 会常驻显示；按需切换（RichTextBox 自带按需显示，不需要）。
        private static void SetScrollOnDemand(TextBox box, bool needed)
        {
            ScrollBars wanted = needed ? ScrollBars.Vertical : ScrollBars.None;
            if (box.ScrollBars != wanted) box.ScrollBars = wanted;
        }

        private SelectionTerm LinkedTermAt(int index)
        {
            foreach (Tuple<int, int, SelectionTerm> range in linkedTerms)
                if (index >= range.Item1 && index < range.Item2) return range.Item3;
            return null;
        }

        private string GetSelectedLookupTerm()
        {
            return sentence.SelectedText.Trim();
        }

        private void HideNearbySelectionAction()
        {
            nearbyExplain.Visible = false;
            nearbySelectionText = String.Empty;
        }

        private void ShowNearbySelectionAction()
        {
            string selected = GetSelectedLookupTerm();
            if (!sentence.Focused || String.IsNullOrWhiteSpace(selected) || selected.Length > 200)
            {
                HideNearbySelectionAction();
                return;
            }
            Point caret = sentence.GetPositionFromCharIndex(sentence.SelectionStart +
                sentence.SelectionLength - 1);
            if (!sentence.ClientRectangle.Contains(caret))
            {
                HideNearbySelectionAction();
                return;
            }
            Point anchor = PointToClient(sentence.PointToScreen(caret));
            int gap = Math.Max(5, sentence.Font.Height / 3);
            int left = Math.Max(0, Math.Min(ClientSize.Width - nearbyExplain.Width,
                anchor.X + gap));
            int top = anchor.Y + sentence.Font.Height + gap;
            if (top + nearbyExplain.Height > ClientSize.Height)
                top = Math.Max(0, anchor.Y - nearbyExplain.Height - gap);
            nearbySelectionText = selected;
            nearbyExplain.Location = new Point(left, top);
            nearbyExplain.Visible = true;
            nearbyExplain.BringToFront();
        }

        private async Task ExplainSelectedTerm()
        {
            string selected = GetSelectedLookupTerm();
            if (String.IsNullOrWhiteSpace(selected)) return;
            if (selected.Length > 200)
            {
                termBody.Text = "请只选取要解释的词语，最多 200 个字符。";
                return;
            }
            await ShowTerm(new SelectionTerm { text = selected });
        }

        private void RenderTasks(List<AnalysisEntity> items, string actionStatus)
        {
            // 日程候选属于整句视图；直接渲染时（诊断/图册）若停在词语视图，先切回，
            // 保证"添加日程"按钮可见可点（生产中 RunAnalysis 已先切回）。
            ShowSentenceView();
            tasks.Controls.Clear();
            SetTaskSectionVisible(false);
            if (items == null || items.Count == 0)
            {
                if (!String.Equals(actionStatus, "example_excluded", StringComparison.Ordinal)) return;
                string message = "这句是在举例，未加入日程。请点击包含真实时间安排的消息。";
                tasks.Controls.Add(new Label { Text = message, AutoSize = true,
                    MaximumSize = new Size(530, 52), ForeColor = Color.DimGray,
                    Margin = new Padding(3, 9, 3, 3) });
                SetTaskSectionVisible(true);
                return;
            }
            int resultGeneration = generation;
            foreach (AnalysisEntity item in items.Take(3))
            {
                if (item == null || String.IsNullOrWhiteSpace(item.text) ||
                    String.IsNullOrWhiteSpace(item.title) || item.start < 0 ||
                    item.end > passageText.Length || item.end <= item.start ||
                    !String.Equals(passageText.Substring(item.start, item.end - item.start),
                        item.text, StringComparison.Ordinal)) continue;
                AnalysisEntity candidate = item;
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false,
                    Margin = new Padding(1), BackColor = Color.FromArgb(239, 247, 255) };
                row.Controls.Add(new Label { Text = item.title + " · " + item.text,
                    AutoSize = true, MaximumSize = new Size(390, 40),
                    Margin = new Padding(5, 7, 4, 3) });
                Button add = new Button { Text = "添加日程", AutoSize = true,
                    Height = 29, Margin = new Padding(3) };
                add.Click += delegate
                {
                    if (resultGeneration != generation || openReminderEditor == null) return;
                    openReminderEditor(new HighlightItem {
                        term = candidate.text, context = passageText, kind = "task",
                        time_text = candidate.time_text, title = candidate.title,
                        start_iso = candidate.start_iso, end_iso = candidate.end_iso,
                        utc_offset = candidate.utc_offset, needs_confirmation = true
                    }, this);
                };
                row.Controls.Add(add);
                tasks.Controls.Add(row);
            }
            if (tasks.Controls.Count == 0)
                return;
            SetTaskSectionVisible(true);
        }

        private async Task ShowTerm(SelectionTerm item)
        {
            int request = ++termGeneration;
            ResetTermDetails();
            currentTermText = item.text;
            ShowWordView();   // v2：同一浮框内切换到词语视图，不另开窗口
            termHeading.Text = "知识点 · " + item.text;
            if (!String.IsNullOrWhiteSpace(item.explanation))
            {
                briefTermExplanation = item.explanation;
                termBody.Text = briefTermExplanation;
                expandTerm.Enabled = true;
                return;
            }
            termBody.Text = "正在解释这个术语…";
            string context = passageText;
            RequestProgress progress = new RequestProgress(this, delegate { return request == termGeneration; },
                delegate(string message) { termBody.Text = message; }, "正在解释这个术语");
            LookupResponse response = null;
            Exception failure = null;
            try
            {
                response = await Task.Factory.StartNew(delegate
                {
                    return lookupTerm(item.text, context, "brief");
                });
            }
            catch (Exception error)
            {
                failure = error;
            }
            // 与整句分析相同：续写可能在线程池，应用经 UI 编组并复查失效守卫。
            if (IsDisposed || Disposing || request != termGeneration)
            {
                progress.Dispose();
                return;
            }
            if (InvokeRequired)
            {
                BeginInvoke(new Action(delegate { ApplyTermBrief(request, response, failure, progress); }));
                return;
            }
            ApplyTermBrief(request, response, failure, progress);
        }

        private void ApplyTermBrief(int request, LookupResponse response, Exception failure,
            RequestProgress progress)
        {
            try
            {
                if (IsDisposed || Disposing || request != termGeneration) return;
                if (failure != null) throw failure;
                termBody.Text = response == null || String.IsNullOrWhiteSpace(response.explanation)
                    ? "暂时没有可靠解释。" : response.explanation;
                briefTermExplanation = termBody.Text;
                briefLookupFailed = response == null || String.IsNullOrWhiteSpace(response.explanation) ||
                    response.lookup_mode == "local_fallback";
                expandTerm.Text = briefLookupFailed ? "重试解释" : "展开解释";
                expandTerm.Enabled = briefLookupFailed || (response != null && response.lookup_mode == "model");
            }
            catch (Exception error)
            {
                if (!IsDisposed && request == termGeneration)
                {
                    termBody.Text = RequestFeedback.For(error);
                    briefLookupFailed = true;
                    expandTerm.Text = "重试解释";
                    expandTerm.Enabled = true;
                }
            }
            finally { progress.Dispose(); }
        }

        private void ResetTermDetails()
        {
            HideNearbySelectionAction();
            currentTermText = String.Empty;
            briefLookupFailed = false;
            briefTermExplanation = String.Empty;
            expandedTermExplanation = String.Empty;
            expandTerm.Text = "展开解释";
            expandTerm.Enabled = false;
            SetTermDetailsExpanded(false);
        }

        private void SetTermDetailsExpanded(bool expanded)
        {
            termDetailsExpanded = expanded;
            UpdateCardLayout();
        }

        private void SetTaskSectionVisible(bool visible)
        {
            layout.RowStyles[8].Height = visible ? 28 : 0;
            layout.RowStyles[9].Height = visible ? 68 : 0;
            taskHeading.Visible = tasks.Visible = visible;
            UpdateCardLayout();
        }

        private void UpdateCardLayout()
        {
            if (wordViewActive)
            {
                UpdateWordViewLayout();
                ClampIntoWorkingArea();
                return;
            }
            bool bodyOverflow, sentenceOverflow;
            layout.RowStyles[2].Height = MeasureGrow(body, body.Text, body.Font, ScaledCap(80),
                ScaledCap(176), out bodyOverflow);
            layout.RowStyles[4].Height = MeasureGrow(sentence, sentence.Text, sentence.Font, ScaledCap(48),
                ScaledCap(110), out sentenceOverflow);
            SetScrollOnDemand(body, bodyOverflow);
            int required = (int)layout.RowStyles.Cast<RowStyle>().Sum(row => row.Height) +
                layout.Padding.Vertical + floatGrip.Height + 4;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (!userSized)
                Height = Math.Min(area.Height - 8, Math.Max(ScaledCap(320), required));
            ClampIntoWorkingArea();
        }

        private async Task ToggleTermDetails()
        {
            if (String.IsNullOrWhiteSpace(currentTermText) || !expandTerm.Enabled) return;
            if (briefLookupFailed)
            {
                await ShowTerm(new SelectionTerm { text = currentTermText });
                return;
            }
            if (termDetailsExpanded)
            {
                SetTermDetailsExpanded(false);
                termBody.Text = briefTermExplanation;
                expandTerm.Text = "展开解释";
                return;
            }
            if (!String.IsNullOrWhiteSpace(expandedTermExplanation))
            {
                SetTermDetailsExpanded(true);
                termBody.Text = expandedTermExplanation;
                expandTerm.Text = "收起解释";
                return;
            }
            int request = ++termGeneration;
            string term = currentTermText;
            string context = passageText;
            expandTerm.Enabled = false;
            expandTerm.Text = "展开中…";
            RequestProgress progress = new RequestProgress(this, delegate { return request == termGeneration; },
                delegate(string message) { expandTerm.Text = "展开中 " + message; }, String.Empty, true);
            LookupResponse response = null;
            Exception failure = null;
            try
            {
                response = await Task.Factory.StartNew(delegate {
                    return lookupTerm(term, context, "expanded");
                });
                if (response == null || response.lookup_mode != "model" ||
                    String.IsNullOrWhiteSpace(response.explanation))
                    throw new InvalidOperationException("Expanded explanation unavailable.");
            }
            catch (Exception error)
            {
                failure = error;
                response = null;
            }
            // 与整句分析相同：续写可能在线程池，应用经 UI 编组并复查失效守卫。
            if (IsDisposed || Disposing || request != termGeneration)
            {
                progress.Dispose();
                return;
            }
            if (InvokeRequired)
            {
                BeginInvoke(new Action(delegate { ApplyTermExpansion(request, response, failure, progress); }));
                return;
            }
            ApplyTermExpansion(request, response, failure, progress);
        }

        private void ApplyTermExpansion(int request, LookupResponse response, Exception failure,
            RequestProgress progress)
        {
            try
            {
                if (IsDisposed || Disposing || request != termGeneration) return;
                if (failure != null) throw failure;
                expandedTermExplanation = response.explanation;
                SetTermDetailsExpanded(true);
                termBody.Text = expandedTermExplanation;
                expandTerm.Text = "收起解释";
            }
            catch
            {
                if (IsDisposed || request != termGeneration) return;
                termBody.Text = briefTermExplanation + "\r\n\r\n详细解释暂时不可用，可点击“重试展开”。";
                expandTerm.Text = "重试展开";
            }
            finally
            {
                progress.Dispose();
                if (!IsDisposed && request == termGeneration) expandTerm.Enabled = true;
            }
        }

        private void SetSourceEditorVisible(bool visible)
        {
            if (visible) ShowSentenceView();
            sourceEditorVisible = visible;
            source.Visible = visible;
            sourceNotice.Visible = visible;
            layout.RowStyles[11].Height = visible ? 100 : 0;
            layout.RowStyles[12].Height = visible ? 38 : 0;
            editSource.Text = visible
                ? "收起原文编辑" : "识别有误？修改原文";
            UpdateCardLayout();
        }
    }

    internal sealed class ManualLookupForm : Form
    {
        private readonly ServiceManager services;
        private readonly TextBox query = new TextBox { MaxLength = 200, Dock = DockStyle.Top };
        private readonly TextBox body = new TextBox { Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Button lookup = new Button { Text = "AI 解释", Width = 112, Height = 30 };
        private readonly Button retry = new Button { Text = "换个解释", Width = 112, Height = 30, Visible = false };
        private readonly Button paste = new Button { Text = "粘贴并解释", Width = 112, Height = 30 };
        private readonly LinkLabel related = new LinkLabel { Dock = DockStyle.Bottom, Height = 30,
            AutoEllipsis = true, LinkBehavior = LinkBehavior.HoverUnderline, Visible = false };
        private readonly LinkLabel feedback = new LinkLabel { Dock = DockStyle.Bottom, Height = 26,
            AutoEllipsis = true, LinkBehavior = LinkBehavior.HoverUnderline, Visible = false };
        private readonly Label notice = new Label { Text = "选中文字后按 Ctrl+Alt+D；也可以在这里输入或粘贴词语。",
            Dock = DockStyle.Top, Height = 48 };
        private int generation;
        private string currentExplanation;
        private bool settingQuery;
        private readonly Func<string, bool, LookupResponse> lookupRequest;
        private string inputSource = "typed";
        private string initialQuery = String.Empty;
        private string sourceApp = "other";

        // Show the waiting state immediately without stealing focus from the
        // source application. UI Automation still needs that application's
        // focused element in order to read the current selection.
        protected override bool ShowWithoutActivation { get { return true; } }

        internal ManualLookupForm(ServiceManager service, Func<string, bool, LookupResponse> requestLookup = null)
        {
            services = service;
            lookupRequest = requestLookup;
            Text = "AI 查词"; Size = new Size(460, 350); MinimumSize = Size;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true;
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40,
                FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 6, 0) };
            actions.Controls.Add(retry); actions.Controls.Add(lookup); actions.Controls.Add(paste);
            Controls.Add(body); Controls.Add(related); Controls.Add(feedback); Controls.Add(actions);
            Controls.Add(query); Controls.Add(notice);
            AcceptButton = lookup;
            query.TextChanged += delegate {
                if (settingQuery) return;
                generation++;
                currentExplanation = null;
                body.Clear();
                related.Visible = false; related.Links.Clear();
                feedback.Visible = false;
                retry.Visible = false;
                lookup.Enabled = query.Text.Trim().Length > 0 && query.Text.Trim().Length <= 200;
                retry.Enabled = lookup.Enabled;
                notice.Text = lookup.Enabled ? "文字已修改，点击解释后查询。" : "请输入 1–200 个字符。";
            };
            FormClosed += delegate { generation++; };
            lookup.Click += async delegate { await RunLookup(false); };
            retry.Click += async delegate { await RunLookup(true); };
            paste.Click += async delegate
            {
                string copied = ReadClipboardText();
                if (String.IsNullOrWhiteSpace(copied))
                {
                    notice.Text = "剪贴板中没有可查询的文字。";
                    return;
                }
                inputSource = "clipboard";
                initialQuery = copied;
                query.Text = copied;
                await RunLookup(false);
            };
            related.LinkClicked += async delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                string term = args.Link.LinkData as string;
                if (String.IsNullOrWhiteSpace(term)) return;
                inputSource = "typed";
                initialQuery = term;
                query.Text = term;
                await RunLookup(false);
            };
            feedback.Text = "反馈：有用 · 不需要标 · 解释不对";
            feedback.Links.Add(3, 2, "useful");
            feedback.Links.Add(8, 4, "unnecessary_highlight");
            feedback.Links.Add(15, 4, "wrong_explanation");
            feedback.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                string value = args.Link.LinkData as string;
                if (String.IsNullOrWhiteSpace(value)) return;
                services.RecordFeedbackMetric("active_lookup", sourceApp, value);
                if (value == "useful") services.NoteTermClicked(query.Text);
                else if (value == "unnecessary_highlight") services.IgnoreTerm(query.Text);
                feedback.Text = "已记录，谢谢";
                feedback.Links.Clear();
            };
        }

        private async Task RunLookup(bool refresh)
        {
            string term = query.Text.Trim();
            if (term.Length == 0 || term.Length > 200) { notice.Text = "请输入 1–200 个字符。"; return; }
            int request = ++generation;
            Stopwatch watch = Stopwatch.StartNew();
            body.Text = refresh ? "正在换一种解释…" : "正在先查本地术语索引…";
            related.Visible = false; related.Links.Clear();
            feedback.Visible = false;
            lookup.Enabled = false; retry.Enabled = false;
            RequestProgress progress = new RequestProgress(this, delegate { return request == generation; },
                delegate(string message) { notice.Text = message; }, "正在查询这个词语");
            if (services != null) services.Log("Manual lookup submitted (" + term.Length + " chars)");
            try
            {
                string previous = refresh ? currentExplanation : null;
                LookupResponse response;
                if (!refresh)
                {
                    LookupResponse instant = await Task.Factory.StartNew(delegate {
                        if (lookupRequest != null) return null;
                        LookupResponse local;
                        if (ShortcutLookup.TryExplain(term, String.Empty, out local)) return local;
                        services.EnsureRunning();
                        return services.LookupInstant(term, String.Empty);
                    });
                    if (IsDisposed || request != generation) return;
                    if (instant != null && !String.IsNullOrWhiteSpace(instant.explanation))
                    {
                        string instantCanonical = (instant.term ?? String.Empty).Trim();
                        if (!String.IsNullOrWhiteSpace(instantCanonical)) SetQueryText(instantCanonical);
                        body.Text = instant.explanation;
                        currentExplanation = instant.explanation;
                        ShowRelatedTerms(instant.entities,
                            String.IsNullOrWhiteSpace(instantCanonical) ? term : instantCanonical);
                        if (!instant.needs_model)
                        {
                            response = instant;
                            goto RenderCompletedLookup;
                        }
                        notice.Text = "本地即时结果 · AI 正在后台补充";
                    }
                }

                response = await Task.Factory.StartNew(delegate {
                    if (lookupRequest != null) return lookupRequest(term, refresh);
                    LookupResponse local;
                    if (ShortcutLookup.TryExplain(term, String.Empty, out local)) return local;
                    services.EnsureRunning();
                    return services.Lookup(term, String.Empty, refresh, previous);
                });
                if (IsDisposed || request != generation) return;
RenderCompletedLookup:
                watch.Stop();
                string canonical = response == null ? String.Empty : (response.term ?? String.Empty).Trim();
                if (!String.IsNullOrWhiteSpace(canonical)) SetQueryText(canonical);
                currentExplanation = response == null ? null : response.explanation;
                body.Text = String.IsNullOrWhiteSpace(currentExplanation)
                    ? "暂时没有可靠解释，请稍后重试。" : currentExplanation;
                string mode = response == null ? "无结果" :
                    String.Equals(response.lookup_mode, "model", StringComparison.OrdinalIgnoreCase)
                        ? "AI 模型解释"
                        : String.Equals(response.lookup_mode, "local_shortcut", StringComparison.OrdinalIgnoreCase)
                            ? "本地快捷键规则"
                            : String.Equals(response.lookup_mode, "local_glossary", StringComparison.OrdinalIgnoreCase)
                                ? "本地术语索引"
                            : response.can_refresh ? "本地兜底（AI 暂不可用）" : "本地/公共词典";
                notice.Text = mode + " · " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒";
                retry.Visible = response == null || response.can_refresh;
                retry.Text = response == null || response.lookup_mode == "local_fallback" ? "重试解释" : "换个解释";
                feedback.Text = "反馈：有用 · 不需要标 · 解释不对";
                feedback.Links.Clear();
                feedback.Links.Add(3, 2, "useful");
                feedback.Links.Add(8, 4, "unnecessary_highlight");
                feedback.Links.Add(15, 4, "wrong_explanation");
                feedback.Visible = response != null && !String.IsNullOrWhiteSpace(currentExplanation);
                string resolvedTerm = String.IsNullOrWhiteSpace(canonical) ? term : canonical;
                ShowRelatedTerms(response == null ? null : response.entities, resolvedTerm);
                if (services != null) services.NoteTermClicked(resolvedTerm);
                if (services != null) services.Log("Manual lookup completed: " + (response == null ? "empty" : response.lookup_mode));
                if (services != null) services.RecordLookupMetric("active_lookup", sourceApp, inputSource,
                    response == null ? "empty" : response.lookup_mode,
                    watch.ElapsedMilliseconds,
                    initialQuery.Length > 0 && !String.Equals(initialQuery, term, StringComparison.Ordinal),
                    response != null && response.cached,
                    response != null && !String.IsNullOrWhiteSpace(response.explanation));
            }
            catch (Exception error)
            {
                if (services != null) services.Log("Manual lookup failed: " + error.GetType().Name);
                if (!IsDisposed && request == generation)
                {
                    body.Text = RequestFeedback.For(error);
                    notice.Text = "查询失败";
                    retry.Visible = true;
                    retry.Text = "重试解释";
                    if (services != null) services.RecordLookupMetric("active_lookup", sourceApp, inputSource, "client_error",
                        watch.ElapsedMilliseconds,
                        initialQuery.Length > 0 && !String.Equals(initialQuery, term, StringComparison.Ordinal),
                        false, false);
                }
            }
            finally
            {
                progress.Dispose();
                if (!IsDisposed && request == generation)
                {
                    lookup.Enabled = true;
                    retry.Enabled = true;
                }
            }
        }

        private void SetQueryText(string text)
        {
            settingQuery = true;
            try { query.Text = text ?? String.Empty; }
            finally { settingQuery = false; }
        }

        private void ShowRelatedTerms(List<AnalysisEntity> entities, string originalTerm)
        {
            related.Links.Clear();
            if (entities == null || entities.Count == 0)
            {
                related.Visible = false;
                return;
            }
            StringBuilder text = new StringBuilder("继续查：");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<int, string>> links = new List<KeyValuePair<int, string>>();
            foreach (AnalysisEntity entity in entities)
            {
                string term = entity == null ? String.Empty : (entity.text ?? String.Empty).Trim();
                if (term.Length == 0 || String.Equals(term, originalTerm, StringComparison.OrdinalIgnoreCase) ||
                    !seen.Add(term)) continue;
                if (seen.Count > 1) text.Append(" · ");
                int start = text.Length;
                text.Append(term);
                links.Add(new KeyValuePair<int, string>(start, term));
                if (seen.Count >= 4) break;
            }
            related.Text = text.ToString();
            foreach (KeyValuePair<int, string> link in links)
                related.Links.Add(link.Key, link.Value.Length, link.Value);
            related.Visible = seen.Count > 0;
        }

        internal async void Open(bool readSelection, string originatingApp = "other")
        {
            sourceApp = originatingApp ?? "other";
            int request = ++generation;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            lookup.Enabled = true;
            SetQueryText(String.Empty);
            body.Text = String.Empty;
            currentExplanation = null;
            retry.Visible = false;
            related.Visible = false;
            related.Links.Clear();
            feedback.Visible = false;
            feedback.Text = "反馈：有用 · 不需要标 · 解释不对";
            feedback.Links.Clear();
            feedback.Links.Add(3, 2, "useful");
            feedback.Links.Add(8, 4, "unnecessary_highlight");
            feedback.Links.Add(15, 4, "wrong_explanation");
            inputSource = "typed";
            initialQuery = String.Empty;

            if (!readSelection)
            {
                notice.Text = "请输入或粘贴需要解释的词语。";
                if (!Visible) Show();
                ActivateForInput();
                if (services != null) services.Log("Manual lookup form opened for typed input");
                return;
            }

            // Give immediate feedback, but do not activate until accessibility has
            // read the source selection. Never touch the clipboard.
            IntPtr target = NativeMethods.GetForegroundWindow();
            notice.Text = "正在读取选中文字…";
            if (!Visible) Show();
            else Invalidate();
            if (services != null) services.Log("Manual lookup form shown; reading selection");

            Task<string> selection = Task.Factory.StartNew<string>(ReadSelection);
            Task completed = await Task.WhenAny(selection, Task.Delay(900));
            if (IsDisposed || request != generation) return;
            string text = completed == selection && !selection.IsFaulted ? selection.Result : String.Empty;
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != target && foreground != Handle) text = String.Empty;
            SetQueryText(text);
            if (String.IsNullOrWhiteSpace(text))
            {
                notice.Text = "当前软件未提供可读取的选中文字，请在下方输入或粘贴。";
                if (services != null) services.Log(completed == selection
                    ? "Manual lookup selection unavailable; editable fallback shown"
                    : "Manual lookup selection timed out; editable fallback shown");
            }
            else
            {
                inputSource = "accessibility";
                initialQuery = text;
                notice.Text = "已读取所选文字，正在为你解释。";
                if (services != null) services.Log("Manual lookup selection read (" + text.Length + " chars)");
            }
            ActivateForInput();
            if (!String.IsNullOrWhiteSpace(text)) lookup.PerformClick();
            else query.Focus();
        }

        private void ActivateForInput()
        {
            if (!Visible) Show();
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            BringToFront();
            query.Focus();
        }

        internal void OpenText(string text, bool exactSelection, string textSource,
            string originatingApp = "other")
        {
            sourceApp = originatingApp ?? "other";
            generation++;
            SetQueryText(text ?? String.Empty);
            if (query.Text.Length > 200)
            {
                lookup.Enabled = false;
                notice.Text = "所选文字超过 200 个字符，请缩小选区或修改后查询。";
                Show(); Activate(); return;
            }
            lookup.Enabled = !String.IsNullOrWhiteSpace(query.Text);
            inputSource = textSource ?? (exactSelection ? "accessibility" : "ocr");
            initialQuery = query.Text;
            body.Text = String.Empty;
            currentExplanation = null;
            retry.Visible = false;
            related.Visible = false;
            related.Links.Clear();
            feedback.Visible = false;
            notice.Text = exactSelection ? "已读取所选文字，正在为你解释。" :
                "这是所选区域的 OCR 结果，请核对或修改后点击 AI 解释。";
            Show(); Activate();
            if (exactSelection && !String.IsNullOrWhiteSpace(query.Text)) lookup.PerformClick();
            else query.Focus();
        }

        internal static string ReadSelection()
        {
            return ReadSelection(200, false);
        }

        internal static string ReadSelection(int maxLength, bool retainOversizeMarker)
        {
            try
            {
                var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(
                    RuntimeEnvironment.GetRuntimeDirectory(), "WPF", "UIAutomationClient.dll"));
                Type elementType = assembly.GetType("System.Windows.Automation.AutomationElement");
                Type patternType = assembly.GetType("System.Windows.Automation.TextPattern");
                object element = elementType.GetProperty("FocusedElement").GetValue(null, null);
                if (element == null) return String.Empty;
                object current = elementType.GetProperty("Current").GetValue(element, null);
                if ((bool)current.GetType().GetProperty("IsPassword").GetValue(current, null))
                    return String.Empty;
                object id = patternType.GetField("Pattern").GetValue(null);
                object pattern = elementType.GetMethod("GetCurrentPattern").Invoke(element, new[] { id });
                Array ranges = (Array)patternType.GetMethod("GetSelection").Invoke(pattern, null);
                if (ranges == null || ranges.Length != 1) return String.Empty;
                object range = ranges.GetValue(0);
                string text = (string)range.GetType().GetMethod("GetText").Invoke(
                    range, new object[] { Math.Max(2, maxLength + 1) });
                string value = (text ?? String.Empty).Trim();
                if (value.Length <= maxLength) return value;
                return retainOversizeMarker ? value : String.Empty;
            }
            catch { return String.Empty; }
        }

        internal static string ReadClipboardText()
        {
            try
            {
                if (!Clipboard.ContainsText()) return String.Empty;
                string value = Clipboard.GetText(TextDataFormat.UnicodeText);
                value = Regex.Replace(value ?? String.Empty, @"\s+", " ").Trim();
                return value.Length > 200 ? String.Empty : value;
            }
            catch { return String.Empty; }
        }

        internal static string PreferCopiedSelection(string ocr, string copied)
        {
            ocr = (ocr ?? String.Empty).Trim();
            copied = (copied ?? String.Empty).Trim();
            if (copied.Length == 0 || copied.Length > 200) return ocr;
            // The user already clicked the selection toolbar's explanation
            // action. If the bounded OCR crop produced nothing, the copied
            // selection is the only exact local text source and should fill the
            // query without requiring another paste action.
            if (ocr.Length == 0) return copied;
            string left = Regex.Replace(ocr.ToLowerInvariant(), @"[^0-9a-z\u4e00-\u9fff]+", "");
            string right = Regex.Replace(copied.ToLowerInvariant(), @"[^0-9a-z\u4e00-\u9fff]+", "");
            if (left.Length == 0 || right.Length == 0) return ocr;
            if (left == right) return copied;
            int longest = Math.Max(left.Length, right.Length);
            if (longest < 4) return ocr;
            int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
            for (int row = 1; row <= left.Length; row++)
            {
                int[] current = new int[right.Length + 1];
                current[0] = row;
                for (int column = 1; column <= right.Length; column++)
                    current[column] = Math.Min(Math.Min(
                        current[column - 1] + 1,
                        previous[column] + 1),
                        previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
                previous = current;
            }
            int distance = previous[right.Length];
            return distance <= 2 && distance / (double)longest <= 0.34 ? copied : ocr;
        }
    }

}
