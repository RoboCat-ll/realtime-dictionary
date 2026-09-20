using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SemanticOverlay.Diagnostics
{
    internal static class FollowTarget
    {
        private const uint KeyUp = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Form form = new Form();
            form.Text = "Semantic Overlay Native Follow Target";
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = new Rectangle(240, 180, 920, 380);

            Label label = new Label();
            label.AutoSize = true;
            label.Font = new Font("Segoe UI", 24, FontStyle.Regular);
            label.Location = new Point(55, 130);
            label.Text = "We need an agent with GitHub, OCR and RAG capabilities.";
            form.Controls.Add(label);

            System.Windows.Forms.Timer triggerTimer = new System.Windows.Forms.Timer();
            triggerTimer.Interval = 8000;
            triggerTimer.Tick += delegate
            {
                triggerTimer.Stop();
                // A synthetic diagnostic must explicitly earn foreground permission;
                // a real user click on WeChat naturally provides it.
                form.WindowState = FormWindowState.Normal;
                ShowWindow(form.Handle, 9); // SW_RESTORE
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                SetForegroundWindow(form.Handle);
                keybd_event(0x12, 0, KeyUp, UIntPtr.Zero);
                SendHotkey(0x4B);
            };

            System.Windows.Forms.Timer moveTimer = new System.Windows.Forms.Timer();
            moveTimer.Interval = 18000;
            moveTimer.Tick += delegate
            {
                moveTimer.Stop();
                form.WindowState = FormWindowState.Normal;
                ShowWindow(form.Handle, 9);
                form.Left += 180;
                form.Top += 90;
                Log("moved " + form.Left + "," + form.Top);
            };

            System.Windows.Forms.Timer resizeTimer = new System.Windows.Forms.Timer();
            resizeTimer.Interval = 28000;
            resizeTimer.Tick += delegate
            {
                resizeTimer.Stop();
                form.WindowState = FormWindowState.Normal;
                ShowWindow(form.Handle, 9);
                form.Width += 180;
                form.Height += 80;
                Log("resized " + form.Width + "x" + form.Height);
            };

            form.Shown += delegate
            {
                form.Activate();
                Log("shown " + form.Left + "," + form.Top + " " + form.Width + "x" + form.Height);
                triggerTimer.Start();
                moveTimer.Start();
                resizeTimer.Start();
            };

            Application.Run(form);
        }

        private static void SendHotkey(byte key)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(0x12, 0, KeyUp, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(0x11, 0, KeyUp, UIntPtr.Zero);
            Log("sent Ctrl+Alt+K");
        }

        private static void Log(string message)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_follow_target.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
        }
    }
}
