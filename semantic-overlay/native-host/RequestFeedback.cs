using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    // Never surface provider bodies, URLs or exception messages to the user.
    internal static class RequestFeedback
    {
        internal static string For(Exception error)
        {
            for (int depth = 0; error != null && depth < 8; depth++, error = error.InnerException)
            {
                if (error is TimeoutException)
                    return "请求超时，请重试。连续失败时，可在“模型设置”检查服务。";
                WebException network = error as WebException;
                if (network != null)
                {
                    HttpWebResponse response = network.Response as HttpWebResponse;
                    if (response != null && (response.StatusCode == HttpStatusCode.Unauthorized ||
                        response.StatusCode == HttpStatusCode.Forbidden))
                        return "服务认证失败，请在“模型设置”检查配置后重试。";
                    return network.Status == WebExceptionStatus.Timeout
                        ? "请求超时，请重试。连续失败时，可在“模型设置”检查服务。"
                        : "服务连接失败，请检查网络后重试。";
                }
                if (error is FileNotFoundException || error is DirectoryNotFoundException)
                    return "运行文件不完整，请重新解压完整安装包后启动。";
                if (error is InvalidOperationException &&
                    error.Message == "尚未同意发送文字，本次请求未发送。")
                    return "本次未获发送许可。点击重试后，可重新选择是否允许发送。";
            }
            return "这次解释未完成，请重试。连续失败时，请检查“模型设置”与本地服务状态。";
        }
    }

    internal sealed class RequestProgress : IDisposable
    {
        private readonly Timer timer = new Timer { Interval = 500 };
        private readonly Stopwatch watch = Stopwatch.StartNew();

        internal RequestProgress(Control owner, Func<bool> current, Action<string> update,
            string phase, bool compact = false)
        {
            timer.Tick += delegate {
                if (owner.IsDisposed || !current()) { timer.Stop(); return; }
                string elapsed = (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒";
                update(compact ? elapsed : phase + " · 已等待 " + elapsed);
            };
            timer.Start();
        }

        public void Dispose() { timer.Stop(); timer.Dispose(); }
    }
}
