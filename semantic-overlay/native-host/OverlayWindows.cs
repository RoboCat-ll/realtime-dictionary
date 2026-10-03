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
    internal sealed class MessageForm : Form
    {
        public event Action<int> HotkeyPressed;

        public MessageForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            Opacity = 0;
            IntPtr ignored = Handle;
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmHotkey && HotkeyPressed != null)
                HotkeyPressed(message.WParam.ToInt32());
            base.WndProc(ref message);
        }
    }

    internal static class TermColor
    {
        // FNV-1a over normalized UTF-16 for consistent term colors.
        public static Color ForTerm(string term)
        {
            string key = (term ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            uint hash = 2166136261;
            foreach (char c in key)
                if (!Char.IsWhiteSpace(c)) hash = unchecked((hash ^ c) * 16777619);
            // Warm + green + purple colors; reserve the blue hue range for schedules.
            double hue = hash % 240;
            if (hue >= 150) hue += 90;
            double x = 150 * (1 - Math.Abs((hue / 60) % 2 - 1));
            int hi = 235, lo = 85, mid = 85 + (int)Math.Floor(x + 0.5);
            if (hue < 60) return Color.FromArgb(hi, mid, lo);
            if (hue < 120) return Color.FromArgb(mid, hi, lo);
            if (hue < 180) return Color.FromArgb(lo, hi, mid);
            if (hue < 240) return Color.FromArgb(lo, mid, hi);
            if (hue < 300) return Color.FromArgb(mid, lo, hi);
            return Color.FromArgb(hi, lo, mid);
        }
    }

    internal sealed class AssistantPanelForm : Form
    {
        private const int BubbleSize = 58;
        private static readonly Size ExpandedSize = new Size(380, 360);
        private readonly Label bubble;
        private readonly Label title;
        private readonly Label hint;
        private readonly Button collapseButton;
        private readonly FlowLayoutPanel termsPanel;
        private bool expanded;
        private bool userPositioned;
        private bool dragging;
        private Point dragCursor;
        private Point dragWindow;

        public event Action<HighlightItem, Rectangle> ItemClicked;

        public AssistantPanelForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.White;
            Opacity = 0.97;
            DoubleBuffered = true;

            bubble = new Label();
            bubble.Name = "assistantBubble";
            bubble.Text = "词";
            bubble.TextAlign = ContentAlignment.MiddleCenter;
            bubble.ForeColor = Color.White;
            bubble.BackColor = Color.FromArgb(58, 122, 254);
            bubble.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 13.0f, FontStyle.Bold);
            bubble.Cursor = Cursors.Hand;
            bubble.MouseDown += BubbleMouseDown;
            bubble.MouseMove += BubbleMouseMove;
            bubble.MouseUp += BubbleMouseUp;
            Controls.Add(bubble);

            title = new Label();
            title.Name = "assistantTitle";
            title.Text = "实时字典";
            title.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12.0f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(25, 31, 42);
            title.AutoSize = true;
            Controls.Add(title);

            hint = new Label();
            hint.Text = "当前画面中值得解释的词";
            hint.ForeColor = Color.FromArgb(108, 116, 130);
            hint.AutoSize = true;
            Controls.Add(hint);

            collapseButton = new Button();
            collapseButton.Name = "assistantCollapse";
            collapseButton.Text = "×";
            collapseButton.AccessibleName = "收起悬浮助手";
            collapseButton.FlatStyle = FlatStyle.Flat;
            collapseButton.FlatAppearance.BorderSize = 0;
            collapseButton.BackColor = Color.White;
            collapseButton.ForeColor = Color.FromArgb(105, 112, 124);
            collapseButton.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 13.0f);
            collapseButton.Click += delegate { SetExpanded(false); };
            Controls.Add(collapseButton);

            termsPanel = new FlowLayoutPanel();
            termsPanel.Name = "assistantTerms";
            termsPanel.FlowDirection = FlowDirection.TopDown;
            termsPanel.WrapContents = false;
            termsPanel.AutoScroll = true;
            termsPanel.BackColor = Color.White;
            termsPanel.Padding = new Padding(0, 3, 0, 3);
            Controls.Add(termsPanel);

            SetItems(new HighlightItem[0]);
            ApplyLayout();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
                return parameters;
            }
        }

        public void StartForTarget(NativeRect target)
        {
            userPositioned = false;
            SetExpanded(false);
            PositionFor(target);
        }

        public void PositionFor(NativeRect target)
        {
            Rectangle targetBounds = new Rectangle(target.Left, target.Top,
                Math.Max(1, target.Width), Math.Max(1, target.Height));
            Rectangle work = Screen.FromRectangle(targetBounds).WorkingArea;
            if (!userPositioned)
            {
                int bubbleX = Math.Min(work.Right - BubbleSize - 10,
                    Math.Max(work.Left + 10, target.Right - BubbleSize - 18));
                int bubbleY = Math.Min(work.Bottom - BubbleSize - 10,
                    Math.Max(work.Top + 10, target.Top + Math.Min(150, Math.Max(24, target.Height / 5))));
                Location = expanded
                    ? new Point(Math.Max(work.Left + 8, bubbleX - 14),
                        Math.Max(work.Top + 8, bubbleY - 14))
                    : new Point(bubbleX, bubbleY);
            }
            ClampTo(work);
        }

        public void SetItems(IEnumerable<HighlightItem> items)
        {
            termsPanel.SuspendLayout();
            termsPanel.Controls.Clear();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            if (items != null)
            {
                foreach (HighlightItem item in items)
                {
                    if (item == null || String.IsNullOrWhiteSpace(item.term) || !seen.Add(item.term.Trim()))
                        continue;
                    Button termButton = new Button();
                    termButton.Name = "assistantTerm" + index;
                    termButton.Text = String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase)
                        ? "日程  " + item.term.Trim()
                        : item.term.Trim();
                    termButton.TextAlign = ContentAlignment.MiddleLeft;
                    termButton.AutoEllipsis = true;
                    termButton.Size = new Size(326, 42);
                    termButton.Margin = new Padding(0, 0, 0, 8);
                    termButton.Padding = new Padding(12, 0, 8, 0);
                    termButton.FlatStyle = FlatStyle.Flat;
                    termButton.FlatAppearance.BorderColor = Color.FromArgb(224, 229, 238);
                    termButton.BackColor = index == 0
                        ? Color.FromArgb(235, 243, 255)
                        : Color.FromArgb(247, 248, 251);
                    termButton.ForeColor = Color.FromArgb(34, 42, 57);
                    termButton.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10.0f, FontStyle.Regular);
                    HighlightItem captured = item;
                    termButton.Click += delegate(object sender, EventArgs args)
                    {
                        Control source = sender as Control;
                        if (source == null || ItemClicked == null) return;
                        ItemClicked(captured, source.RectangleToScreen(source.ClientRectangle));
                    };
                    termsPanel.Controls.Add(termButton);
                    index++;
                }
            }
            if (index == 0)
            {
                Label empty = new Label();
                empty.Name = "assistantEmpty";
                empty.Text = "当前画面没有发现需要解释的词。\r\n滚动或按 Ctrl+Alt+K 可重新识别。";
                empty.ForeColor = Color.FromArgb(108, 116, 130);
                empty.Size = new Size(326, 70);
                empty.Padding = new Padding(10, 12, 8, 0);
                termsPanel.Controls.Add(empty);
            }
            bubble.Text = index > 0 ? Math.Min(index, 9).ToString() : "词";
            termsPanel.ResumeLayout();
        }

        public void ShowInactive()
        {
            IntPtr previousForeground = NativeMethods.GetForegroundWindow();
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost,
                Left, Top, Width, Height, NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            if (NativeMethods.GetForegroundWindow() == Handle && previousForeground != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(previousForeground);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmMouseActivate)
            {
                message.Result = new IntPtr(NativeMethods.MaNoActivate);
                return;
            }
            base.WndProc(ref message);
        }

        private void SetExpanded(bool value)
        {
            if (expanded == value) return;
            Point bubbleScreen = Visible ? bubble.PointToScreen(Point.Empty) : Location;
            expanded = value;
            ApplyLayout();
            Location = expanded
                ? new Point(bubbleScreen.X - bubble.Left, bubbleScreen.Y - bubble.Top)
                : bubbleScreen;
            Rectangle work = Screen.FromPoint(bubbleScreen).WorkingArea;
            ClampTo(work);
            if (Visible) ShowInactive();
        }

        private void ApplyLayout()
        {
            Size = expanded ? ExpandedSize : new Size(BubbleSize, BubbleSize);
            bubble.Bounds = expanded
                ? new Rectangle(14, 14, BubbleSize, BubbleSize)
                : new Rectangle(0, 0, BubbleSize, BubbleSize);
            title.Visible = expanded;
            hint.Visible = expanded;
            collapseButton.Visible = expanded;
            termsPanel.Visible = expanded;
            if (expanded)
            {
                title.Location = new Point(88, 17);
                hint.Location = new Point(88, 46);
                collapseButton.Bounds = new Rectangle(334, 12, 36, 34);
                termsPanel.Bounds = new Rectangle(20, 88, 340, 252);
            }
            Region old = Region;
            using (GraphicsPath path = new GraphicsPath())
            {
                if (!expanded)
                    path.AddEllipse(ClientRectangle);
                else
                {
                    int radius = 20;
                    Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                    path.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
                    path.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90);
                    path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
                    path.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90);
                    path.CloseFigure();
                }
                Region = new Region(path);
            }
            if (old != null) old.Dispose();
            Invalidate();
        }

        private void BubbleMouseDown(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            dragging = false;
            dragCursor = Cursor.Position;
            dragWindow = Location;
        }

        private void BubbleMouseMove(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            Point cursor = Cursor.Position;
            int dx = cursor.X - dragCursor.X;
            int dy = cursor.Y - dragCursor.Y;
            if (!dragging && Math.Abs(dx) + Math.Abs(dy) < 6) return;
            dragging = true;
            userPositioned = true;
            Location = new Point(dragWindow.X + dx, dragWindow.Y + dy);
        }

        private void BubbleMouseUp(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            if (!dragging) SetExpanded(!expanded);
            dragging = false;
            ClampTo(Screen.FromPoint(Cursor.Position).WorkingArea);
        }

        private void ClampTo(Rectangle work)
        {
            int x = Math.Max(work.Left + 4, Math.Min(Left, work.Right - Width - 4));
            int y = Math.Max(work.Top + 4, Math.Min(Top, work.Bottom - Height - 4));
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (!expanded) return;
            using (Pen border = new Pen(Color.FromArgb(214, 220, 230), 1))
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class HighlightForm : Form
    {
        public bool CaptureExcluded { get; private set; }
        public bool IsHighlightVisible { get { return Visible && clipVisible; } }
        private bool clipVisible = true;
        private Rectangle? appliedClip;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int build;
            string value = Convert.ToString(Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "0"));
            CaptureExcluded = Int32.TryParse(value, out build) && build >= 19041 &&
                NativeMethods.SetWindowDisplayAffinity(Handle, 0x11);
        }
        public void ClipTo(Rectangle viewport)
        {
            Rectangle clip = Rectangle.Intersect(Bounds, viewport);
            clipVisible = clip.Width > 0 && clip.Height > 0;
            if (!clipVisible) { appliedClip = null; Hide(); return; }
            if (clip == Bounds)
            {
                ClearClip();
                return;
            }
            clip.Offset(-Left, -Top);
            if (appliedClip.HasValue && appliedClip.Value == clip)
                return;
            Region old = Region;
            Region = new Region(clip);
            appliedClip = clip;
            if (old != null) old.Dispose();
        }
        public void ClearClip()
        {
            clipVisible = true;
            if (Region == null) { appliedClip = null; return; }
            Region old = Region; Region = null;
            appliedClip = null;
            if (old != null) old.Dispose();
        }
        private HighlightItem item = new HighlightItem();
        private Color borderColor = Color.FromArgb(240, 128, 24);
        private readonly ContextMenuStrip termMenu;
        private readonly ToolStripMenuItem ignoreItem;
        public event Action<HighlightForm, HighlightItem> ItemClicked;
        public event Action<HighlightForm, string> TermIgnored;

        public HighlightForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 184, 48);
            Opacity = 0.34;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            termMenu = new ContextMenuStrip();
            ignoreItem = new ToolStripMenuItem();
            ignoreItem.Click += delegate
            {
                if (TermIgnored != null)
                    TermIgnored(this, item.term);
            };
            termMenu.Items.Add(ignoreItem);
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
                parameters.ExStyle |= NativeMethods.WsExToolWindow |
                                      NativeMethods.WsExNoActivate;
                return parameters;
            }
        }

        public void SetHighlightBounds(Rectangle bounds, HighlightItem value)
        {
            string previousTerm = item == null ? String.Empty : item.term;
            string previousKind = item == null ? String.Empty : item.kind;
            Size previousSize = Size;
            item = value ?? new HighlightItem();
            bool isTask = String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase);
            BackColor = isTask ? Color.FromArgb(82, 148, 255) : TermColor.ForTerm(item.term);
            borderColor = isTask ? Color.FromArgb(38, 94, 210) : ControlPaint.Dark(BackColor);
            if (!Visible)
                Bounds = bounds;
            if (!String.Equals(previousTerm, item.term, StringComparison.Ordinal) ||
                !String.Equals(previousKind, item.kind, StringComparison.Ordinal) ||
                previousSize != bounds.Size)
                Invalidate();
        }

        public void ShowInactive()
        {
            if (!clipVisible) return;
            if (!Visible)
                NativeMethods.SetWindowPos(
                    Handle,
                    NativeMethods.HwndTopMost,
                    Left,
                    Top,
                    Width,
                    Height,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(borderColor, 2))
                args.Graphics.DrawRectangle(border, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button == MouseButtons.Left && ItemClicked != null)
                ItemClicked(this, item);
            else if (args.Button == MouseButtons.Right &&
                     !String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase))
            {
                ignoreItem.Text = "不再标注“" + item.term + "”";
                termMenu.Show(Cursor.Position);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                termMenu.Dispose();
            base.Dispose(disposing);
        }
    }

}
