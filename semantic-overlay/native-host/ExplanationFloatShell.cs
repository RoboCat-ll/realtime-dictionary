using System;
using System.Drawing;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    // v2 轻量解释浮框：窗口外壳与行为层。
    // 职责：无边框外观、grip 拖动、关闭与 Esc、基于气泡矩形的定位、目标窗口跟随。
    // 业务逻辑（分析、术语、日程入口）留在 MessageExplanationForms.cs。
    // 约定见 design/03-设计规范.md §10：弹出不抢焦点（ShowWithoutActivation + 置顶走
    // SetWindowPos），但故意不设置 WS_EX_NOACTIVATE——用户点击、拖选、编辑时必须能
    // 正常获得焦点。Esc 仅在浮框持有焦点时经 KeyPreview 生效，不注册全局钩子。
    internal sealed partial class SelectionAnalysisForm
    {
        private Panel floatGrip;
        private Label floatClose;
        private Panel resizeGrip;
        private readonly Panel[] resizeEdges = new Panel[4];
        private Point resizeStartPoint;
        private Rectangle resizeStartBounds;
        private int resizeDirection;
        private Panel wordView;
        private TableLayoutPanel wordLayout;
        private LinkLabel wordBack;
        private bool wordViewActive;
        private bool userPositioned;
        private bool userSized;
        private bool gripDragging;
        private bool gripResizing;
        private int gripDX;
        private int gripDY;
        private Rectangle anchorRect = Rectangle.Empty;
        private IntPtr anchorTarget = IntPtr.Zero;
        private Rectangle lastTargetRect = Rectangle.Empty;
        private bool autoHidden;
        private bool bubbleAnchorDetached;
        private System.Windows.Forms.Timer followTimer;
        private const int InputZoneReserve = 120; // 96DPI 逻辑像素：聊天输入区预留

        private void InitializeFloatShell()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            BackColor = Color.White;
            Size = new Size(480, 320);
            MinimumSize = new Size(320, 170);
            RestorePersistedSize();

            floatGrip = new Panel {
                Height = 26, Dock = DockStyle.Top, BackColor = Color.FromArgb(247, 248, 250),
                Cursor = Cursors.SizeAll };
            floatClose = new Label {
                Text = "✕", AutoSize = false, Width = 28, Dock = DockStyle.Right,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(138, 143, 153),
                Cursor = Cursors.Hand };
            floatClose.MouseEnter += delegate { floatClose.ForeColor = Color.FromArgb(31, 35, 41); };
            floatClose.MouseLeave += delegate { floatClose.ForeColor = Color.FromArgb(138, 143, 153); };
            floatClose.MouseClick += delegate(object sender, MouseEventArgs args) {
                if (args.Button == MouseButtons.Left) Close(); };
            Label dots = new Label {
                Text = "⠿", AutoSize = false, Width = 20, Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(179, 184, 192) };
            // 后添加的先停靠：heading 先加（填充中部），dots/close 后加（两侧）
            floatGrip.Controls.Add(dots);
            floatGrip.Controls.Add(floatClose);
            floatGrip.MouseDown += GripMouseDown;
            floatGrip.MouseMove += GripMouseMove;
            floatGrip.MouseUp += GripMouseUp;
            dots.MouseDown += GripMouseDown;
            dots.MouseMove += GripMouseMove;
            dots.MouseUp += GripMouseUp;
            FormClosed += delegate {
                if (followTimer != null) { followTimer.Stop(); followTimer.Dispose(); followTimer = null; }
            };

            // 右下角调整手柄：浮层控件，拖到即改尺寸；用户尺寸优先且持久化。
            resizeGrip = new Panel {
                Size = new Size(16, 16), BackColor = Color.White,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Cursor = Cursors.SizeNWSE };
            resizeGrip.MouseDown += delegate(object sender, MouseEventArgs args) {
                if (args.Button != MouseButtons.Left) return;
                BeginFloatResize(resizeGrip, 10);
            };
            resizeGrip.MouseMove += delegate(object sender, MouseEventArgs args) {
                if (!gripResizing) return;
                ContinueFloatResize();
            };
            resizeGrip.MouseUp += delegate(object sender, MouseEventArgs args) {
                if (!gripResizing) return;
                EndFloatResize(resizeGrip);
            };
            resizeGrip.MouseCaptureChanged += delegate {
                if (gripResizing && !resizeGrip.Capture) EndFloatResize(resizeGrip);
            };
            resizeGrip.Paint += delegate(object sender, PaintEventArgs args) {
                using (Pen pen = new Pen(Color.FromArgb(179, 184, 192)))
                    for (int i = 0; i < 3; i++)
                        args.Graphics.DrawLine(pen, 15, 3 + i * 4, 3 + i * 4, 15);
            };
            for (int index = 0; index < resizeEdges.Length; index++)
            {
                int direction = 1 << index; // left, right, top, bottom
                var edge = new Panel { BackColor = Color.White,
                    Cursor = index < 2 ? Cursors.SizeWE : Cursors.SizeNS };
                edge.MouseDown += delegate(object sender, MouseEventArgs args) {
                    if (args.Button == MouseButtons.Left) BeginFloatResize((Control)sender, direction);
                };
                edge.MouseMove += delegate { if (gripResizing) ContinueFloatResize(); };
                edge.MouseUp += delegate(object sender, MouseEventArgs args) {
                    if (gripResizing && args.Button == MouseButtons.Left) EndFloatResize((Control)sender);
                };
                edge.MouseCaptureChanged += delegate(object sender, EventArgs args) {
                    Control handle = (Control)sender;
                    if (gripResizing && !handle.Capture) EndFloatResize(handle);
                };
                resizeEdges[index] = edge;
            }
        }

        private void BeginFloatResize(Control handle, int direction)
        {
            userSized = true;
            userPositioned = true;
            gripResizing = true;
            resizeDirection = direction;
            resizeStartPoint = Cursor.Position;
            resizeStartBounds = Bounds;
            handle.Capture = true;
        }

        internal static Rectangle ResizeFloatBounds(Rectangle original, Point delta, int direction,
            Size minimum, Rectangle area)
        {
            int left = original.Left, right = original.Right;
            int top = original.Top, bottom = original.Bottom;
            if ((direction & 1) != 0) left = Math.Max(area.Left, Math.Min(right - minimum.Width, left + delta.X));
            if ((direction & 2) != 0) right = Math.Min(area.Right, Math.Max(left + minimum.Width, right + delta.X));
            if ((direction & 4) != 0) top = Math.Max(area.Top, Math.Min(bottom - minimum.Height, top + delta.Y));
            if ((direction & 8) != 0) bottom = Math.Min(area.Bottom, Math.Max(top + minimum.Height, bottom + delta.Y));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private void ContinueFloatResize()
        {
            Point mouse = Cursor.Position;
            Bounds = ResizeFloatBounds(resizeStartBounds,
                new Point(mouse.X - resizeStartPoint.X, mouse.Y - resizeStartPoint.Y),
                resizeDirection, MinimumSize, Screen.FromRectangle(resizeStartBounds).WorkingArea);
            UpdateCardLayout();
        }

        private void EndFloatResize(Control handle)
        {
            gripResizing = false;
            handle.Capture = false;
            UpdateCardLayout();
            ClampIntoWorkingArea();
            PersistSize();
        }

        // 恢复用户上次调整的尺寸（96DPI 逻辑值经 AutoScaleMode.Dpi 自动缩放）。
        // 恢复的尺寸同样视为用户尺寸：布局更新不再改动窗体大小。
        private void RestorePersistedSize()
        {
            if (services == null || ServiceManager.DisableFloatSizeRestoreForDiagnostics) return;
            Size logical;
            if (!services.TryGetFloatSize(out logical)) return;
            Size = logical;
            userSized = true;
        }

        private void PersistSize()
        {
            if (services == null) return;
            int dpi = Math.Max(96, DeviceDpi);
            if (!services.TrySetFloatSize(new Size(Width * 96 / dpi, Height * 96 / dpi)))
                status.Text = "本次尺寸已保留，无法保存到下次启动";
        }

        // 标题标签并入 grip 拖动条（保持控件身份与文案，诊断反射依赖不变）。
        // 调用时 dots/close 已在 grip 中；SetChildIndex(title, 0) 使标题最后停靠、填充中部。
        private void MountHeading(Label title)
        {
            title.Font = new Font("Microsoft YaHei UI", 9f);
            title.ForeColor = Color.FromArgb(138, 143, 153);
            title.Margin = new Padding(0);
            title.Cursor = Cursors.SizeAll;
            floatGrip.Controls.Add(title);
            floatGrip.Controls.SetChildIndex(title, 0);
            title.MouseDown += GripMouseDown;
            title.MouseMove += GripMouseMove;
            title.MouseUp += GripMouseUp;
        }

        // 在 layout 与 wordView 之后调用：最后加入 → 最先停靠到顶部
        private void AttachGrip()
        {
            Controls.Add(floatGrip);
            foreach (Panel edge in resizeEdges) Controls.Add(edge);
            Controls.Add(resizeGrip);
            LayoutResizeGrip();
            Resize += delegate { LayoutResizeGrip(); };
            Layout += delegate { LayoutResizeGrip(); };
        }

        private void LayoutResizeGrip()
        {
            if (resizeGrip == null || resizeGrip.IsDisposed) return;
            int edgeWidth = ScaledCap(5);
            if (resizeEdges[0] != null)
            {
                resizeEdges[0].Bounds = new Rectangle(0, edgeWidth, edgeWidth, Math.Max(0, ClientSize.Height - edgeWidth * 2));
                resizeEdges[1].Bounds = new Rectangle(ClientSize.Width - edgeWidth, edgeWidth, edgeWidth, Math.Max(0, ClientSize.Height - edgeWidth * 2));
                resizeEdges[2].Bounds = new Rectangle(0, 0, ClientSize.Width, edgeWidth);
                resizeEdges[3].Bounds = new Rectangle(0, ClientSize.Height - edgeWidth, ClientSize.Width, edgeWidth);
                foreach (Panel edge in resizeEdges) edge.BringToFront();
            }
            resizeGrip.Location = new Point(ClientSize.Width - resizeGrip.Width - 2,
                ClientSize.Height - resizeGrip.Height - 2);
            resizeGrip.BringToFront();
        }

        // 弹出时永不抢焦点；用户点击浮框后按 Windows 正常规则激活（未设 WS_EX_NOACTIVATE）
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= NativeMethods.WsExToolWindow;
                value.ClassStyle |= NativeMethods.CsDropShadow;
                return value;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Form.TopMost 在 .NET Framework 句柄创建时会激活窗口；改经 SetWindowPos 置顶
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost, Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(218, 222, 230)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private void GripMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            gripDragging = true;
            gripDX = e.X;
            gripDY = e.Y;
        }

        private void GripMouseMove(object sender, MouseEventArgs e)
        {
            if (!gripDragging) return;
            Left += e.X - gripDX;
            Top += e.Y - gripDY;
            userPositioned = true;
        }

        private void GripMouseUp(object sender, MouseEventArgs e)
        {
            if (!gripDragging) return;
            gripDragging = false;
            ClampIntoWorkingArea();
        }

        /* ---------------- 定位 ---------------- */

        private int ScaledInputReserve()
        {
            return InputZoneReserve * Math.Max(96, DeviceDpi) / 96;
        }

        // 96DPI 逻辑像素上限按当前 DPI 缩放（正文行高随字体缩放，上限也要同步）。
        private int ScaledCap(int logicalPixels)
        {
            return logicalPixels * Math.Max(96, DeviceDpi) / 96;
        }

        // 纯函数：由气泡矩形 R、目标窗口矩形 W 与工作区 S 计算浮框左上角。
        // 右侧优先 → 翻左侧 → 压入屏幕；垂直对齐气泡顶，避让输入区与工作区底边；
        // 保底不覆盖目标消息（移到下方或上方）。约定见设计 §10.3。
        internal static Point PlaceFloat(Rectangle anchor, Size size, Rectangle window,
            Rectangle area, int inputReserve)
        {
            int gap = 12, margin = 14;
            int x;
            if (anchor.Right + gap + size.Width <= area.Right - margin)
                x = anchor.Right + gap;
            else if (anchor.Left - gap - size.Width >= area.Left + margin)
                x = anchor.Left - gap - size.Width;
            else
                x = Math.Max(area.Left + margin,
                    Math.Min(anchor.Left, area.Right - margin - size.Width));
            int y = anchor.Top - 4;
            int bottomLimit = area.Bottom - 8;
            bool overlapsWindow = window.Height > 0 &&
                x + size.Width > window.Left && x < window.Right;
            if (overlapsWindow)
                bottomLimit = Math.Min(bottomLimit, window.Bottom - inputReserve);
            if (y + size.Height > bottomLimit) y = bottomLimit - size.Height;
            if (y < area.Top + margin) y = area.Top + margin;
            Rectangle placed = new Rectangle(x, y, size.Width,
                Math.Min(size.Height, Math.Max(0, bottomLimit - y)));
            if (placed.IntersectsWith(anchor))
            {
                if (anchor.Bottom + gap + Math.Min(size.Height, 200) <= bottomLimit)
                    y = anchor.Bottom + gap;
                else
                    y = Math.Max(area.Top + margin,
                        anchor.Top - gap - Math.Min(size.Height, 240));
            }
            if (y + size.Height > area.Bottom - 8)
                y = Math.Max(area.Top + margin, area.Bottom - 8 - size.Height);
            if (x < area.Left + 8) x = area.Left + 8;
            if (x + size.Width > area.Right - 8)
                x = Math.Max(area.Left + 8, area.Right - 8 - size.Width);
            return new Point(x, y);
        }

        internal void SetAnchor(Rectangle screenRect, IntPtr target)
        {
            anchorRect = screenRect;
            bubbleAnchorDetached = false;
            UpdateReadingHeading();
            anchorTarget = target;
            userPositioned = false;
            autoHidden = false;
            if (target != IntPtr.Zero)
            {
                NativeRect native;
                lastTargetRect = NativeMethods.GetWindowRect(target, out native)
                    ? new Rectangle(native.Left, native.Top, native.Width, native.Height)
                    : Rectangle.Empty;
                if (followTimer == null)
                {
                    followTimer = new System.Windows.Forms.Timer { Interval = 400 };
                    followTimer.Tick += FollowTick;
                }
                followTimer.Start();
            }
        }

        private void PlaceInitialIfAnchored()
        {
            if (anchorRect.Width <= 0) return;
            Rectangle area = Screen.FromRectangle(anchorRect).WorkingArea;
            Location = PlaceFloat(anchorRect, Size, lastTargetRect, area, ScaledInputReserve());
        }

        private void UpdateReadingHeading()
        {
            if (heading != null)
                heading.Text = (wordViewActive ? "词语解释" : "这句话的意思") +
                    (bubbleAnchorDetached ? " · 已保留原消息" : "");
        }

        private void DetachBubbleAnchor()
        {
            anchorRect = Rectangle.Empty;
            bubbleAnchorDetached = true;
            UpdateReadingHeading();
        }

        internal void ObserveChatScroll(Point point)
        {
            if (IsDisposed || anchorTarget == IntPtr.Zero ||
                NativeMethods.GetForegroundWindow() != anchorTarget ||
                (Visible && Bounds.Contains(point))) return;
            NativeRect target;
            if (NativeMethods.GetWindowRect(anchorTarget, out target) &&
                new Rectangle(target.Left, target.Top, target.Width, target.Height).Contains(point))
                DetachBubbleAnchor();
        }

        internal void RestoreReadingCard()
        {
            if (IsDisposed) return;
            autoHidden = false;
            Show();
            Activate(); // Explicit desktop-shortcut action, unlike automatic following.
            ClampIntoWorkingArea();
        }

        private bool CanRepairVisibility()
        {
            if (autoHidden) return false;
            if (anchorTarget == IntPtr.Zero) return true;
            if (!NativeMethods.IsWindow(anchorTarget) || NativeMethods.IsIconic(anchorTarget)) return false;
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            return foreground == anchorTarget || IsOwnedForeground(foreground);
        }

        private void ClampIntoWorkingArea()
        {
            Rectangle area = anchorRect.Width > 0
                ? Screen.FromRectangle(anchorRect).WorkingArea
                : Screen.FromControl(this).WorkingArea;
            if (Width > area.Width - 16)
                Width = Math.Max(MinimumSize.Width, area.Width - 16);
            int bottomLimit = area.Bottom - 8;
            if (lastTargetRect.Height > 0 &&
                Left + Width > lastTargetRect.Left && Left < lastTargetRect.Right)
                bottomLimit = Math.Min(bottomLimit,
                    lastTargetRect.Bottom - ScaledInputReserve());
            if (bottomLimit < area.Top + 40) bottomLimit = area.Top + 40;
            if (Height > bottomLimit - area.Top - 8)
                Height = Math.Max(MinimumSize.Height, bottomLimit - area.Top - 8);
            if (Bottom > bottomLimit) Top = Math.Max(area.Top + 8, bottomLimit - Height);
            if (Top < area.Top + 8) Top = area.Top + 8;
            if (Left + Width > area.Right - 8)
                Left = Math.Max(area.Left + 8, area.Right - 8 - Width);
            if (Left < area.Left + 8) Left = area.Left + 8;
        }

        /* ---------------- 目标窗口跟随（设计 §10.4） ---------------- */

        private void FollowTick(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (anchorTarget == IntPtr.Zero) { followTimer.Stop(); return; }
            if (!NativeMethods.IsWindow(anchorTarget)) { Close(); return; }
            if (NativeMethods.IsIconic(anchorTarget))
            {
                if (Visible) { autoHidden = true; Hide(); }
                return;
            }
            NativeRect native;
            if (!NativeMethods.GetWindowRect(anchorTarget, out native)) return;
            Rectangle rect = new Rectangle(native.Left, native.Top, native.Width, native.Height);
            if (lastTargetRect.Width > 0 && rect.Size != lastTargetRect.Size)
            {
                // Layout changed, not the user's reading intent. Retain the answer
                // without pretending the old bubble coordinates remain valid.
                DetachBubbleAnchor();
            }
            if (lastTargetRect.Width > 0 && rect.Location != lastTargetRect.Location)
            {
                if (!userPositioned)
                {
                    Left += rect.Left - lastTargetRect.Left;
                    Top += rect.Top - lastTargetRect.Top;
                    ClampIntoWorkingArea();
                }
            }
            lastTargetRect = rect;
            if (bubbleAnchorDetached) ClampIntoWorkingArea();
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            bool ownForeground = foreground == anchorTarget || IsOwnedForeground(foreground);
            if (!ownForeground)
            {
                if (Visible) { autoHidden = true; Hide(); }
                return;
            }
            if (autoHidden && !Visible)
            {
                autoHidden = false;
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
                NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost,
                    Left, Top, Width, Height,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
                ClampIntoWorkingArea();
            }
        }

        private bool IsOwnedForeground(IntPtr foreground)
        {
            if (foreground == Handle) return true;
            foreach (Form owned in OwnedForms)
                if (owned != null && !owned.IsDisposed && owned.Handle == foreground)
                    return true;
            return false;
        }

        /* ---------------- 视图切换：整句 ⇄ 词语（同一浮框实例） ---------------- */

        private void BuildWordView()
        {
            wordView = new Panel {
                Dock = DockStyle.Fill, Visible = false, BackColor = Color.White,
                Padding = new Padding(12, 2, 12, 12) };
            wordLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            wordLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            wordLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            wordLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            wordLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            wordLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Spare height must not stretch a short definition.
            wordBack = new LinkLabel {
                Text = "‹ 返回整句解释", AutoSize = true, Dock = DockStyle.Left,
                LinkColor = Color.FromArgb(36, 88, 184),
                LinkBehavior = LinkBehavior.HoverUnderline,
                TextAlign = ContentAlignment.MiddleLeft };
            wordBack.LinkClicked += delegate { ShowSentenceView(); };
            Panel backRow = new Panel { Dock = DockStyle.Fill };
            backRow.Controls.Add(wordBack);
            wordLayout.Controls.Add(backRow, 0, 0);
            wordView.Controls.Add(wordLayout);
            Controls.Add(wordView);
        }

        private void ShowWordView()
        {
            if (wordView == null) BuildWordView();
            if (!wordViewActive)
            {
                wordViewActive = true;
                wordLayout.Controls.Add(termHeader, 0, 1);
                wordLayout.Controls.Add(termBody, 0, 2);
                termHeader.Visible = true;
                termBody.Visible = true;
                layout.Visible = false;
                wordView.Visible = true;
                UpdateReadingHeading();
            }
            UpdateCardLayout();
        }

        private void ShowSentenceView()
        {
            if (!wordViewActive) return;
            wordViewActive = false;
            layout.Controls.Add(termHeader, 0, 6);
            layout.Controls.Add(termBody, 0, 7);
            termHeader.Visible = false;
            termBody.Visible = false;
            wordView.Visible = false;
            layout.Visible = true;
            UpdateReadingHeading();
            UpdateCardLayout();
        }

        private void UpdateWordViewLayout()
        {
            if (wordLayout == null) return;
            int cap = ScaledCap(termDetailsExpanded ? 384 : 176);
            if (userSized)
                cap = Math.Max(ScaledCap(100), ClientSize.Height - floatGrip.Height -
                    wordView.Padding.Vertical - wordLayout.Padding.Vertical - 30 - 30 - 4);
            bool overflow;
            wordLayout.RowStyles[2].Height = MeasureGrow(
                termBody, termBody.Text, termBody.Font, ScaledCap(100), cap, out overflow);
            SetScrollOnDemand(termBody, overflow);
            int required = floatGrip.Height + wordView.Padding.Vertical + 30 + 30 +
                (int)wordLayout.RowStyles[2].Height + wordLayout.Padding.Vertical + 4;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (!userSized)
                Height = Math.Min(area.Height - 8, Math.Max(ScaledCap(220), required));
        }
    }
}
