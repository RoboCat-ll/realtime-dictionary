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
    internal sealed class ScanRegionForm : Form
    {
        public RectangleF SelectedRegion { get; private set; }
        internal static NativeRect MapRegion(RectangleF region, NativeRect window)
        {
            return new NativeRect {
                Left = window.Left + (int)Math.Round(region.Left * window.Width),
                Top = window.Top + (int)Math.Round(region.Top * window.Height),
                Right = window.Left + (int)Math.Round(region.Right * window.Width),
                Bottom = window.Top + (int)Math.Round(region.Bottom * window.Height)
            };
        }
        public ScanRegionForm(Bitmap screenshot)
        {
            Text = "框选聊天内容 · 鼠标拖动选区";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            double scale = Math.Min(1.0, Math.Min(Math.Min(900, area.Width - 100) / (double)screenshot.Width,
                Math.Min(560, area.Height - 180) / (double)screenshot.Height));
            int width = Math.Max(200, (int)(screenshot.Width * scale));
            int height = Math.Max(120, (int)(screenshot.Height * scale));
            ClientSize = new Size(width, height + 85);
            var picture = new PictureBox { Image = screenshot, SizeMode = PictureBoxSizeMode.StretchImage,
                Location = Point.Empty, Size = new Size(width, height), Cursor = Cursors.Cross };
            var notice = new Label { Text = "拖出只包含消息正文的区域。选区仅本次运行有效；排版变化后可重新框选。",
                Location = new Point(8, height + 4), Size = new Size(width - 16, 32) };
            var confirm = new Button { Text = "使用选区", Enabled = false,
                Location = new Point(width - 190, height + 44), Size = new Size(90, 30) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel,
                Location = new Point(width - 94, height + 44), Size = new Size(86, 30) };
            Controls.Add(picture); Controls.Add(notice); Controls.Add(confirm); Controls.Add(cancel);
            CancelButton = cancel;
            bool dragging = false;
            Point origin = Point.Empty;
            Rectangle selection = Rectangle.Empty;
            picture.MouseDown += delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                origin = e.Location; dragging = true; picture.Capture = true;
                confirm.Enabled = false; selection = Rectangle.Empty;
            };
            picture.MouseMove += delegate(object sender, MouseEventArgs e) {
                if (!dragging) return;
                int x = Math.Max(0, Math.Min(width, e.X)), y = Math.Max(0, Math.Min(height, e.Y));
                selection = Rectangle.FromLTRB(Math.Min(origin.X, x), Math.Min(origin.Y, y),
                    Math.Max(origin.X, x), Math.Max(origin.Y, y));
                picture.Invalidate();
            };
            picture.MouseUp += delegate {
                if (!dragging) return;
                dragging = false; picture.Capture = false;
                confirm.Enabled = selection.Width / scale >= 100 && selection.Height / scale >= 50;
            };
            picture.Paint += delegate(object sender, PaintEventArgs e) {
                if (selection.Width < 1 || selection.Height < 1) return;
                using (var brush = new SolidBrush(Color.FromArgb(40, 50, 110, 240))) e.Graphics.FillRectangle(brush, selection);
                using (var pen = new Pen(Color.RoyalBlue, 2)) e.Graphics.DrawRectangle(pen, selection);
            };
            confirm.Click += delegate {
                SelectedRegion = new RectangleF(selection.X / (float)width, selection.Y / (float)height,
                    selection.Width / (float)width, selection.Height / (float)height);
                DialogResult = DialogResult.OK; Close();
            };
        }
    }

    internal sealed class CaptionConsentForm : Form
    {
        private readonly CheckBox remember = new CheckBox {
            Text = "以后启动会议字幕不再询问", AutoSize = true,
            Location = new Point(18, 174) };

        public bool RememberApproval { get { return remember.Checked; } }

        public CaptionConsentForm()
        {
            Text = "开始会议语音字幕";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            ClientSize = new Size(500, 250);
            Font = new Font("Microsoft YaHei UI", 9);
            Label notice = new Label { Location = new Point(18, 18), Size = new Size(465, 145),
                Text = "程序将采集所选音源正在播放的语音片段，并发送到硅基流动生成原文字幕。\r\n\r\n" +
                    "音源可能回退到全系统声音；不采集麦克风、不保存录音。\r\n" +
                    "字幕文字默认按日期保存本机；托盘可关闭保存，记录窗口可删除当天历史。" };
            Button start = new Button { Text = "开始字幕", DialogResult = DialogResult.OK,
                Location = new Point(304, 212), Size = new Size(90, 28) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel,
                Location = new Point(404, 212), Size = new Size(78, 28) };
            Controls.Add(notice);
            Controls.Add(remember);
            Controls.Add(start);
            Controls.Add(cancel);
            AcceptButton = start;
            CancelButton = cancel;
        }
    }

    internal sealed class CaptionStatusForm : Form
    {
        private readonly Label label = new Label { Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter };
        private readonly System.Windows.Forms.Timer dismissTimer = new System.Windows.Forms.Timer();

        public CaptionStatusForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(232, 246, 255);
            ClientSize = new Size(190, 32);
            label.ForeColor = Color.FromArgb(20, 74, 125);
            label.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.0f);
            Controls.Add(label);
            dismissTimer.Tick += delegate { dismissTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= NativeMethods.WsExTransparent |
                                 NativeMethods.WsExToolWindow |
                                 NativeMethods.WsExNoActivate;
                return value;
            }
        }

        public void PositionFor(NativeRect target)
        {
            Rectangle bounds = new Rectangle(target.Left, target.Top, target.Width, target.Height);
            Rectangle working = Screen.FromRectangle(bounds).WorkingArea;
            Location = new Point(
                Math.Max(working.Left + 8, Math.Min(target.Left + 16, working.Right - Width - 8)),
                Math.Max(working.Top + 8, Math.Min(target.Top + 16, working.Bottom - Height - 8)));
        }

        public void ShowState(string message, NativeRect target)
        {
            dismissTimer.Stop();
            label.Text = message ?? String.Empty;
            PositionFor(target);
            if (!Visible) NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost,
                Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        public void ShowTemporary(string message, NativeRect target, int milliseconds)
        {
            ShowState(message, target);
            dismissTimer.Interval = Math.Max(250, milliseconds);
            dismissTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen pen = new Pen(Color.FromArgb(71, 151, 204)))
                args.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) dismissTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class StatusForm : Form
    {
        private readonly Label label;
        private readonly System.Windows.Forms.Timer dismissTimer;

        public StatusForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 248, 218);
            ClientSize = new Size(104, 32);
            DoubleBuffered = true;

            label = new Label();
            label.Text = "正在读取文字…";
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Dock = DockStyle.Fill;
            label.ForeColor = Color.FromArgb(92, 67, 10);
            label.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.0f, FontStyle.Regular);
            Controls.Add(label);

            dismissTimer = new System.Windows.Forms.Timer();
            dismissTimer.Tick += delegate
            {
                dismissTimer.Stop();
                Hide();
            };
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
                parameters.ExStyle |= NativeMethods.WsExTransparent |
                                      NativeMethods.WsExToolWindow |
                                      NativeMethods.WsExNoActivate;
                return parameters;
            }
        }

        public void PositionFor(NativeRect target)
        {
            Rectangle targetBounds = new Rectangle(
                target.Left, target.Top, target.Width, target.Height);
            Rectangle working = Screen.FromRectangle(targetBounds).WorkingArea;
            int desiredX = target.Right - Width - 16;
            int desiredY = target.Top + 16;
            Location = new Point(
                Math.Max(working.Left + 8, Math.Min(desiredX, working.Right - Width - 8)),
                Math.Max(working.Top + 8, Math.Min(desiredY, working.Bottom - Height - 8)));
        }

        public void ShowScanning(NativeRect target)
        {
            dismissTimer.Stop();
            label.Text = "正在读取文字…";
            PositionFor(target);
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
        }

        public void ShowMessage(string message, NativeRect target, int milliseconds)
        {
            label.Text = message ?? string.Empty;
            PositionFor(target);
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
            dismissTimer.Interval = Math.Max(250, milliseconds);
            dismissTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(Color.FromArgb(210, 170, 58), 1))
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                dismissTimer.Dispose();
            base.Dispose(disposing);
        }
    }

}
