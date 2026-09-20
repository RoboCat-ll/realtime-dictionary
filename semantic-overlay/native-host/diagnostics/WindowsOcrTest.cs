using System;
using System.Diagnostics;
using System.IO;

namespace SemanticOverlay.Diagnostics
{
    internal static class WindowsOcrTest
    {
        private static void Main(string[] args)
        {
            if (args.Length != 1 || !File.Exists(args[0]))
            {
                Console.Error.WriteLine("Usage: WindowsOcrTest <image-path>");
                Environment.Exit(2);
            }

            Stopwatch watch = Stopwatch.StartNew();
            string hostDirectory = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar)).FullName;
            string script = Path.Combine(hostDirectory, "windows_ocr.ps1");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "powershell.exe";
            info.Arguments =
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script +
                "\" -ImagePath \"" + Path.GetFullPath(args[0]) + "\"";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = System.Text.Encoding.UTF8;
            info.StandardErrorEncoding = System.Text.Encoding.UTF8;
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(error);
                Console.WriteLine("duration_ms=" + watch.ElapsedMilliseconds);
                Console.WriteLine(output);
            }
        }
    }
}
