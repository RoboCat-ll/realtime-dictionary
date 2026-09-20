using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SemanticOverlay.Diagnostics
{
    internal static class JevE2ETarget
    {
        private const uint KeyUp = 0x0002;
        private const int RunCount = 10;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Form form = new Form();
            form.Text = "Jev Desktop E2E Target";
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = new Rectangle(130, 120, 1420, 760);
            form.BackColor = Color.White;

            Label content = new Label();
            content.AutoSize = true;
            content.Font = new Font("Microsoft YaHei UI", 24, FontStyle.Regular);
            content.Location = new Point(55, 170);
            content.Text =
                "RAG 从向量数据库检索文档，数据库事务满足 ACID。\r\n" +
                "与 oneAPI 同事开 bootcamp，FAISS 和 JWT 属于不同领域。\r\n" +
                "OpenAI 和 DeepSeek 提供模型服务，其中模型只是普通泛称。";
            form.Controls.Add(content);

            Label changing = new Label();
            changing.AutoSize = true;
            changing.Font = new Font("Segoe UI", 12, FontStyle.Regular);
            changing.ForeColor = Color.DimGray;
            changing.Location = new Point(58, 440);
            form.Controls.Add(changing);

            Panel visualPulse = new Panel();
            visualPulse.Location = new Point(55, 510);
            visualPulse.Size = new Size(1240, 105);
            visualPulse.BackColor = Color.WhiteSmoke;
            form.Controls.Add(visualPulse);

            int run = 0;
            System.Windows.Forms.Timer trigger = new System.Windows.Forms.Timer();
            trigger.Interval = 4500;
            trigger.Tick += delegate
            {
                if (run >= RunCount)
                {
                    trigger.Stop();
                    Log("completed");
                    CloseAfter(form, 5000);
                    return;
                }
                int nextRun = run + 1;
                // Force a material fingerprint change without changing the OCR text
                // or emitting an accessibility name/value change that would queue
                // a duplicate automatic refresh alongside the explicit hotkey.
                visualPulse.BackColor = nextRun % 2 == 0 ? Color.Gainsboro : Color.WhiteSmoke;
                form.WindowState = FormWindowState.Normal;
                ShowWindow(form.Handle, 9);
                for (int attempt = 0; attempt < 12 && GetForegroundWindow() != form.Handle; attempt++)
                {
                    form.Activate();
                    SetForegroundWindow(form.Handle);
                    form.Refresh();
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
                if (GetForegroundWindow() != form.Handle)
                {
                    Log("focus-retry " + nextRun);
                    return;
                }
                run = nextRun;
                form.Refresh();
                Application.DoEvents();
                Thread.Sleep(180);
                SendHotkey(0x4B);
                Log("triggered " + run);
            };

            form.Shown += delegate
            {
                changing.Text = "preflighting TypeSafe/Jev; bounded run 0 / " + RunCount;
                form.Activate();
                string preflight;
                if (!TryConfirmJevProvider(out preflight))
                {
                    content.Text = "Jev desktop validation aborted.";
                    content.ForeColor = Color.Firebrick;
                    changing.Text = preflight;
                    Log("aborted: " + preflight);
                    CloseAfter(form, 2500);
                    return;
                }
                changing.Text = "TypeSafe/Jev confirmed; diagnostic frame 0 / " + RunCount;
                Log("preflight-ok: " + preflight);
                trigger.Start();
            };
            Application.Run(form);
        }

        private static bool TryConfirmJevProvider(out string detail)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:8877/health");
                request.Method = "GET";
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    string provider = ReadJsonString(body, "analysis_provider");
                    string mode = ReadJsonString(body, "analysis_mode");
                    detail = "provider=" + (provider ?? "missing") +
                        ", mode=" + (mode ?? "missing");
                    return String.Equals(provider, "typesafe", StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(mode, "jev", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception error)
            {
                detail = "health unavailable (" + error.GetType().Name + ")";
                return false;
            }
        }

        private static string ReadJsonString(string body, string property)
        {
            if (String.IsNullOrWhiteSpace(body) || String.IsNullOrWhiteSpace(property))
                return null;
            Match match = Regex.Match(
                body,
                "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static void CloseAfter(Form form, int milliseconds)
        {
            System.Windows.Forms.Timer close = new System.Windows.Forms.Timer();
            close.Interval = milliseconds;
            close.Tick += delegate
            {
                close.Stop();
                close.Dispose();
                form.Close();
            };
            close.Start();
        }

        private static void SendHotkey(byte key)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(70);
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
            keybd_event(0x12, 0, KeyUp, UIntPtr.Zero);
            keybd_event(0x11, 0, KeyUp, UIntPtr.Zero);
        }

        private static void Log(string message)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_jev_e2e_target.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
    }
}
