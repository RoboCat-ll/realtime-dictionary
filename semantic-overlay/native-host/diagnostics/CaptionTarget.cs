using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SemanticOverlay.Diagnostics
{
    internal sealed class CaptionTargetForm : Form
    {
        private const uint KeyUpFlag = 0x0002;
        private readonly Label caption;
        private int stressStep;
        private readonly string[] lines =
        {
            "We will review the OneAPI deployment in tomorrow's bootcamp.",
            "The latency budget is critical for real-time transcription.",
            "Please confirm the Kubernetes rollback strategy before launch.",
            "The semantic overlay should explain domain-specific terminology."
        };
        private int index;

        public CaptionTargetForm()
        {
            Text = "实时字幕跟随验收窗口";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 700);
            BackColor = Color.FromArgb(28, 31, 38);

            Label title = new Label();
            title.Text = "Live Meeting · English captions";
            title.ForeColor = Color.White;
            title.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(34, 30);
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = "字幕会自动变化，用于验证高亮是否持续跟随；无需操作此窗口。";
            hint.ForeColor = Color.FromArgb(170, 177, 190);
            hint.Font = new Font("Microsoft YaHei UI", 11);
            hint.AutoSize = true;
            hint.Location = new Point(38, 78);
            Controls.Add(hint);

            Panel captionBar = new Panel();
            captionBar.BackColor = Color.FromArgb(210, 0, 0, 0);
            captionBar.Location = new Point(90, 480);
            captionBar.Size = new Size(920, 112);
            captionBar.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(captionBar);

            caption = new Label();
            caption.Text = lines[0];
            caption.ForeColor = Color.White;
            caption.BackColor = Color.Black;
            caption.TextAlign = ContentAlignment.MiddleCenter;
            caption.Dock = DockStyle.Fill;
            caption.Font = new Font("Segoe UI", 19, FontStyle.Regular);
            captionBar.Controls.Add(caption);

            Timer timer = new Timer();
            timer.Interval = ReadInterval("CAPTION_TEST_LINE_INTERVAL_MS", 1500);
            timer.Tick += delegate
            {
                index = (index + 1) % lines.Length;
                caption.Text = lines[index];
            };
            timer.Start();

            if (String.Equals(
                    Environment.GetEnvironmentVariable("CAPTION_TEST_STRESS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Timer stress = new Timer();
                stress.Interval = 2300;
                stress.Tick += delegate
                {
                    Rectangle area = Screen.FromControl(this).WorkingArea;
                    bool compact = stressStep % 2 == 0;
                    Size = compact ? new Size(920, 620) : new Size(1120, 740);
                    int right = Math.Max(area.Left, area.Right - Width - 30);
                    int bottom = Math.Max(area.Top, area.Bottom - Height - 30);
                    Location = stressStep % 4 < 2
                        ? new Point(area.Left + 30, area.Top + 30)
                        : new Point(right, bottom);
                    stressStep++;
                };
                stress.Start();
            }

            Timer trigger = new Timer();
            trigger.Interval = 500;
            trigger.Tick += delegate
            {
                NativeMethods.SwitchToThisWindow(Handle, true);
                NativeMethods.SetForegroundWindow(Handle);
                if (NativeMethods.GetForegroundWindow() != Handle)
                    return;
                trigger.Stop();
                NativeMethods.SendHotkey(0x4B);
                TopMost = false;
            };
            Shown += delegate
            {
                TopMost = true;
                Activate();
                BringToFront();
                trigger.Start();
            };

            Timer capture = new Timer();
            capture.Interval = 7500;
            capture.Tick += delegate
            {
                capture.Stop();
                string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
                string output = System.IO.Path.Combine(
                    projectRoot, "tests", "caption-follow-preview.png");
                using (Bitmap image = new Bitmap(Width, Height))
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.CopyFromScreen(Left, Top, 0, 0, image.Size);
                    image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                }
            };
            Shown += delegate { capture.Start(); };

            Timer stop = new Timer();
            stop.Interval = ReadInterval("CAPTION_TEST_DURATION_MS", 11000);
            stop.Tick += delegate
            {
                stop.Stop();
                NativeMethods.SwitchToThisWindow(Handle, true);
                NativeMethods.SetForegroundWindow(Handle);
                if (NativeMethods.GetForegroundWindow() == Handle)
                {
                    NativeMethods.KeybdEvent(0x11, 0, 0, UIntPtr.Zero);
                    NativeMethods.KeybdEvent(0x12, 0, 0, UIntPtr.Zero);
                    NativeMethods.KeybdEvent(0x47, 0, 0, UIntPtr.Zero);
                    NativeMethods.KeybdEvent(0x47, 0, KeyUpFlag, UIntPtr.Zero);
                    NativeMethods.KeybdEvent(0x12, 0, KeyUpFlag, UIntPtr.Zero);
                    NativeMethods.KeybdEvent(0x11, 0, KeyUpFlag, UIntPtr.Zero);
                }
                Close();
            };
            Shown += delegate { stop.Start(); };
        }

        private static int ReadInterval(string name, int fallback)
        {
            int value;
            return Int32.TryParse(Environment.GetEnvironmentVariable(name), out value) &&
                   value >= 500 && value <= Int32.MaxValue
                ? value
                : fallback;
        }
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { public uint type; public InputData data; }
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputData {
            [FieldOffset(0)] public KeyboardInput keyboard;
            [FieldOffset(0)] public MouseInput mouse;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput { public int x,y; public uint data,flags,time; public UIntPtr extra; }
        [DllImport("user32.dll", SetLastError=true)]
        internal static extern uint SendInput(uint count, Input[] inputs, int size);
        internal static void SendHotkey(ushort key) {
            ushort[] keys = {0x11,0x12,key,key,0x12,0x11};
            Input[] inputs = new Input[keys.Length];
            for (int i=0;i<inputs.Length;i++) {
                inputs[i].type=1; inputs[i].data.keyboard.key=keys[i];
                inputs[i].data.keyboard.flags=i>=3 ? 2u : 0u;
            }
            uint sent = SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(Input)));
            if (sent != inputs.Length) throw new InvalidOperationException("SendInput failed: " + Marshal.GetLastWin32Error());
        }

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern void SwitchToThisWindow(IntPtr window, bool altTab);

        [DllImport("user32.dll", EntryPoint = "keybd_event")]
        internal static extern void KeybdEvent(
            byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    }

    internal static class CaptionTarget
    {
        [STAThread]
        private static void Main()
        {
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CaptionTargetForm());
        }
    }
}
