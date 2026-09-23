using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RealtimeDictionary.Setup
{
    internal static class Program
    {
        internal const string ProductName = "实时字典";
        internal const string Version = "0.19.8";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && String.Equals(args[0], "/verify", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = VerifyPayload() ? 0 : 2;
                return;
            }
            if (args.Length > 0 && String.Equals(args[0], "/cleanup", StringComparison.OrdinalIgnoreCase))
            {
                Cleanup(args);
                return;
            }
            if (args.Length > 0 && String.Equals(args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstall();
                return;
            }
            Application.Run(new SetupForm());
        }

        internal static string InstallDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RealtimeDictionary",
                    "App");
            }
        }

        internal static void Install()
        {
            string target = InstallDirectory;
            StopInstalledProcesses(target);
            EnsureNoOtherPortableHost(target);
            Directory.CreateDirectory(target);
            ExtractPayload(target);

            string uninstall = Path.Combine(target, "卸载实时字典.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, uninstall, true);
            CreateShortcuts(target, uninstall);
        }

        internal static bool VerifyPayload()
        {
            string[] required = {
                "start.cmd",
                "server.py",
                "calendar_export.py",
                "runtime/python.exe",
                "native-host/bin/SemanticOverlay.exe",
                "native-host/bin/NAudio.dll",
                "THIRD_PARTY_LICENSES/NAudio.txt",
                "browser-extension/manifest.json"
            };
            bool[] found = new bool[required.Length];
            Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                "RealtimeDictionary.Payload.zip");
            if (payload == null)
                return false;
            using (payload)
            using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string normalized = entry.FullName.Replace('\\', '/');
                    if (normalized.IndexOf("../", StringComparison.Ordinal) >= 0 ||
                        normalized.EndsWith("config.json", StringComparison.OrdinalIgnoreCase) ||
                        normalized.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                        return false;
                    for (int index = 0; index < required.Length; index++)
                    {
                        if (String.Equals(normalized, required[index], StringComparison.OrdinalIgnoreCase))
                            found[index] = true;
                    }
                }
            }
            foreach (bool value in found)
            {
                if (!value)
                    return false;
            }
            return true;
        }

        internal static void LaunchInstalledApp()
        {
            string target = InstallDirectory;
            string host = Path.Combine(target, "native-host", "bin", "SemanticOverlay.exe");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = host;
            info.WorkingDirectory = target;
            info.UseShellExecute = true;
            Process.Start(info);
        }

        private static void ExtractPayload(string target)
        {
            Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                "RealtimeDictionary.Payload.zip");
            if (payload == null)
                throw new InvalidOperationException("安装包缺少程序数据。请重新下载安装包。");
            string root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) +
                          Path.DirectorySeparatorChar;
            using (payload)
            using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string destination = Path.GetFullPath(Path.Combine(target, relative));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装包包含不安全路径。");
                    if (String.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (Stream source = entry.Open())
                    using (FileStream output = new FileStream(
                        destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        source.CopyTo(output);
                }
            }
        }

        private static void EnsureNoOtherPortableHost(string installDirectory)
        {
            string expected = Path.Combine(
                installDirectory, "native-host", "bin", "SemanticOverlay.exe");
            foreach (Process process in Process.GetProcessesByName("SemanticOverlay"))
            {
                try
                {
                    string path = process.MainModule.FileName;
                    if (!PathsEqual(path, expected))
                        throw new InvalidOperationException(
                            "检测到另一个便携版实时字典正在运行。请先从它的托盘菜单选择“退出”，再安装。\r\n\r\n" + path);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static void StopInstalledProcesses(string target)
        {
            string host = Path.Combine(target, "native-host", "bin", "SemanticOverlay.exe");
            string python = Path.Combine(target, "runtime", "python.exe");
            string pythonw = Path.Combine(target, "runtime", "pythonw.exe");
            StopExactProcesses("SemanticOverlay", host);
            StopExactProcesses("python", python);
            StopExactProcesses("pythonw", pythonw);
        }

        private static void StopExactProcesses(string name, string expectedPath)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!PathsEqual(process.MainModule.FileName, expectedPath))
                        continue;
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static bool PathsEqual(string first, string second)
        {
            if (String.IsNullOrEmpty(first) || String.IsNullOrEmpty(second))
                return false;
            try
            {
                return String.Equals(
                    Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CreateShortcuts(string target, string uninstall)
        {
            string host = Path.Combine(target, "native-host", "bin", "SemanticOverlay.exe");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string programGroup = Path.Combine(programs, ProductName);
            Directory.CreateDirectory(programGroup);
            CreateShortcut(Path.Combine(desktop, ProductName + ".lnk"), host, "", target, host);
            CreateShortcut(Path.Combine(programGroup, ProductName + ".lnk"), host, "", target, host);
            CreateShortcut(
                Path.Combine(programGroup, "卸载实时字典.lnk"),
                uninstall,
                "/uninstall",
                target,
                uninstall);
        }

        private static void CreateShortcut(
            string shortcutPath,
            string targetPath,
            string arguments,
            string workingDirectory,
            string iconPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconPath + ",0" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "启动实时字典" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }

        private static void Uninstall()
        {
            if (MessageBox.Show(
                    "确定卸载实时字典吗？\r\n\r\n个人 API Key 和偏好设置将保留，重新安装后仍可使用。",
                    ProductName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string target = InstallDirectory;
            StopInstalledProcesses(target);
            DeleteShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ProductName + ".lnk"));
            string group = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);
            if (Directory.Exists(group))
                Directory.Delete(group, true);

            string helper = Path.Combine(
                Path.GetTempPath(),
                "RealtimeDictionary-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, helper, true);
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = helper;
            info.Arguments = "/cleanup \"" + target + "\" " + Process.GetCurrentProcess().Id;
            info.UseShellExecute = true;
            Process.Start(info);
        }

        private static void Cleanup(string[] args)
        {
            if (args.Length < 3 || !PathsEqual(args[1], InstallDirectory))
                return;
            int parentId;
            if (Int32.TryParse(args[2], out parentId))
            {
                try
                {
                    using (Process parent = Process.GetProcessById(parentId))
                        parent.WaitForExit(10000);
                }
                catch
                {
                }
            }
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(args[1]))
                        Directory.Delete(args[1], true);
                    MessageBox.Show(
                        "实时字典已卸载。个人 API Key 和偏好设置仍保存在当前用户目录。",
                        ProductName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
            MessageBox.Show(
                "部分文件仍被占用，卸载未完成。请重启电脑后重试。",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private static void DeleteShortcut(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Button installButton;
        private readonly Button cancelButton;
        private readonly Label statusLabel;

        public SetupForm()
        {
            Text = Program.ProductName + " 安装程序";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(540, 330);
            Font = SystemFonts.MessageBoxFont;

            Label title = new Label();
            title.Text = "安装实时字典";
            title.Font = new Font(Font.FontFamily, 18, FontStyle.Bold);
            title.Location = new Point(28, 24);
            title.Size = new Size(470, 38);
            Controls.Add(title);

            Label description = new Label();
            description.Text =
                "面向微信、QQ 等聊天窗口和无原生字幕会议的实时词典。\r\n" +
                "按 Ctrl+Alt+K，再在 10 秒内单击一条微信或 QQ 消息；整窗高亮与会议字幕位于实验功能。";
            description.Location = new Point(31, 72);
            description.Size = new Size(475, 52);
            Controls.Add(description);

            Label pathTitle = new Label();
            pathTitle.Text = "安装位置";
            pathTitle.Font = new Font(Font, FontStyle.Bold);
            pathTitle.Location = new Point(31, 137);
            pathTitle.AutoSize = true;
            Controls.Add(pathTitle);

            TextBox path = new TextBox();
            path.Text = Program.InstallDirectory;
            path.ReadOnly = true;
            path.Location = new Point(31, 160);
            path.Size = new Size(475, 26);
            Controls.Add(path);

            Label privacy = new Label();
            privacy.Text = "安装包不含 API Key。安装完成后可在托盘菜单中配置自己的密钥。";
            privacy.ForeColor = Color.FromArgb(85, 91, 102);
            privacy.Location = new Point(31, 199);
            privacy.Size = new Size(475, 28);
            Controls.Add(privacy);

            statusLabel = new Label();
            statusLabel.Text = "版本 " + Program.Version;
            statusLabel.ForeColor = Color.FromArgb(85, 91, 102);
            statusLabel.Location = new Point(31, 270);
            statusLabel.Size = new Size(250, 26);
            Controls.Add(statusLabel);

            installButton = new Button();
            installButton.Text = "安装";
            installButton.Location = new Point(338, 260);
            installButton.Size = new Size(80, 36);
            installButton.Click += InstallClicked;
            Controls.Add(installButton);

            cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Location = new Point(426, 260);
            cancelButton.Size = new Size(80, 36);
            Controls.Add(cancelButton);

            AcceptButton = installButton;
            CancelButton = cancelButton;
        }

        private void InstallClicked(object sender, EventArgs args)
        {
            installButton.Enabled = false;
            cancelButton.Enabled = false;
            statusLabel.Text = "正在安装…";
            Refresh();
            try
            {
                Program.Install();
                statusLabel.Text = "安装完成";
                MessageBox.Show(
                    this,
                    "安装完成。实时字典将启动并驻留在右下角托盘。",
                    Program.ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Program.LaunchInstalledApp();
                Close();
            }
            catch (Exception error)
            {
                statusLabel.Text = "安装失败";
                MessageBox.Show(this, error.Message, Program.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                installButton.Enabled = true;
                cancelButton.Enabled = true;
            }
        }
    }
}
