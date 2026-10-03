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
    internal sealed partial class ServiceManager : IDisposable
    {
        private void StartPython(string scriptName)
        {
            string script = Path.Combine(projectRoot, scriptName);
            string python = FindPythonExecutable();
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = python;
            info.Arguments = "\"" + script + "\"";
            info.WorkingDirectory = projectRoot;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            Process process = Process.Start(info);
            if (process != null)
            {
                ownedProcesses.Add(process);
                Log("Started " + scriptName + " as PID " + process.Id);
            }
        }

        private string FindPythonExecutable()
        {
            string bundled = Path.Combine(projectRoot, "runtime", "python.exe");
            if (File.Exists(bundled))
                return bundled;

            string configured = Environment.GetEnvironmentVariable("REALTIME_DICTIONARY_PYTHON");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            string onPath = "python.exe";
            try
            {
                using (Process probe = Process.Start(new ProcessStartInfo
                {
                    FileName = onPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (probe != null)
                    {
                        if (!probe.WaitForExit(1500))
                        {
                            try { probe.Kill(); probe.WaitForExit(500); } catch { }
                        }
                        else if (probe.ExitCode == 0) return onPath;
                    }
                }
            }
            catch { }

            throw new FileNotFoundException(
                "Python runtime not found. Install Python or include runtime\\python.exe in the package.");
        }

        private static bool IsHealthy(string url, int timeout)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = timeout;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        private ServiceHealth TryGetAnalysisHealth(int timeout)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:8877/health");
                request.Method = "GET";
                request.Timeout = timeout;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return serializer.Deserialize<ServiceHealth>(reader.ReadToEnd());
            }
            catch
            {
                return null;
            }
        }

        private void WaitForCompatibleAnalysisService(int maxMilliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < maxMilliseconds)
            {
                ServiceHealth health = TryGetAnalysisHealth(500);
                if (IsCompatibleHealth(health))
                    return;
                if (health != null)
                    throw new InvalidOperationException(
                        "启动后的后端协议不兼容，请确认前端与 server.py 来自同一版本。");
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("实时字典后端未能在限定时间内启动。");
        }

        private static void WaitForHealth(string url, int maxMilliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < maxMilliseconds)
            {
                if (IsHealthy(url, 500))
                    return;
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("Service did not start: " + url);
        }

        private static string FindProjectRoot()
        {
            DirectoryInfo current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int depth = 0; current != null && depth < 8; depth++, current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "server.py")))
                    return current.FullName;
            }
            throw new DirectoryNotFoundException("Cannot locate semantic-overlay project root.");
        }

        public void Dispose()
        {
            familiarity.EndSession(AutoLearnFamiliarTerms);
            lock (windowsOcrLock)
                StopWindowsOcrWorker();
            foreach (Process process in ownedProcesses)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }
                process.Dispose();
            }
        }
    }
}
