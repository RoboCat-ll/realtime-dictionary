using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    // 连续查词模式诊断：手势判定、门控、偏好持久化（隔离 APPDATA）。
    // 全部为本地合成检查，不发起网络或模型请求，不操作真实客户端。
    internal static class ContinuousLookupTest
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void CheckGesture()
        {
            IntPtr windowA = new IntPtr(1001);
            IntPtr windowB = new IntPtr(1002);
            DateTime lastUtc = DateTime.MinValue;
            Point lastPoint = Point.Empty;
            IntPtr lastWindow = IntPtr.Zero;
            const int ms = 500, w = 8, h = 8;
            DateTime t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

            // 单击：不触发
            Require(!OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(300, 300), windowA, t0, ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Single click must not trigger");
            // 阈值内第二次同窗点击：触发一次
            Require(OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(304, 302), windowA, t0.AddMilliseconds(220), ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Qualified double click must trigger");
            // 第三击不连锁
            Require(!OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(300, 300), windowA, t0.AddMilliseconds(400), ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Third click must not chain");

            // 超时：不触发
            lastUtc = DateTime.MinValue; lastWindow = IntPtr.Zero;
            OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(300, 300), windowA, t0, ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow);
            Require(!OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(300, 300), windowA, t0.AddMilliseconds(900), ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Slow second click must not trigger");

            // 不同窗口：不触发
            lastUtc = DateTime.MinValue; lastWindow = IntPtr.Zero;
            OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(300, 300), windowA, t0, ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow);
            Require(!OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(300, 300), windowB, t0.AddMilliseconds(150), ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Cross-window must not trigger");

            // 距离超阈值：不触发
            lastUtc = DateTime.MinValue; lastWindow = IntPtr.Zero;
            OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(300, 300), windowA, t0, ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow);
            Require(!OverlayContext.EvaluateContinuousGesture("double_click", false,
                new Point(330, 300), windowA, t0.AddMilliseconds(150), ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Distant second click must not trigger");

            // Alt＋单击：按住 Alt 才触发，每次按下都是明确意图
            Require(OverlayContext.EvaluateContinuousGesture("alt_click", true,
                new Point(300, 300), windowA, t0, ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Alt+click must trigger");
            Require(!OverlayContext.EvaluateContinuousGesture("alt_click", false,
                new Point(300, 300), windowA, t0, ms, w, h,
                ref lastUtc, ref lastPoint, ref lastWindow), "Plain click in alt mode must not trigger");
            Console.WriteLine("continuous-gesture-ok");
        }

        private static void CheckGates()
        {
            // 关闭时：任何手势不处理
            Require(!OverlayContext.ShouldHandleContinuousPress(false, true, false, false, "wechat"),
                "Disabled mode must ignore gestures");
            // 总开关关闭：不处理
            Require(!OverlayContext.ShouldHandleContinuousPress(true, false, false, false, "wechat"),
                "Master toolbar off must ignore gestures");
            // armed 单次流程优先：不重复处理
            Require(!OverlayContext.ShouldHandleContinuousPress(true, true, true, false, "wechat"),
                "Armed one-shot must take precedence");
            // 自有窗口（浮框/菜单）：不处理
            Require(!OverlayContext.ShouldHandleContinuousPress(true, true, false, true, "wechat"),
                "Own windows must be ignored");
            // 非 QQ/微信：不处理
            Require(!OverlayContext.ShouldHandleContinuousPress(true, true, false, false, "other"),
                "Non-chat windows must be ignored");
            // 全部满足：处理
            Require(OverlayContext.ShouldHandleContinuousPress(true, true, false, false, "wechat") &&
                OverlayContext.ShouldHandleContinuousPress(true, true, false, false, "qq"),
                "Qualified press must be handled on chat clients");
            Console.WriteLine("continuous-gates-ok");
        }

        private static void CheckPreferences()
        {
            // .NET GetFolderPath 不读 APPDATA 环境变量；用显式路径覆盖隔离偏好文件。
            string isolated = Path.Combine(Path.GetTempPath(),
                "rtd-cont-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ServiceManager.PreferencePathOverrideForDiagnostics = isolated;
                using (var service = new ServiceManager())
                {
                    Require(!service.ContinuousLookupEnabled,
                        "Continuous lookup must default to off");
                    Require(service.ContinuousLookupTrigger == "double_click",
                        "Trigger must default to double_click");
                    Size unset;
                    Require(!service.TryGetFloatSize(out unset), "Float size must default to unset");
                    service.SetContinuousLookupEnabled(true);
                    service.SetContinuousLookupTrigger("alt_click");
                    service.SetFloatSize(new Size(420, 320));
                }
                using (var service = new ServiceManager())
                {
                    Require(service.ContinuousLookupEnabled, "Continuous lookup must persist");
                    Require(service.ContinuousLookupTrigger == "alt_click",
                        "Trigger choice must persist");
                    Size restored;
                    Require(service.TryGetFloatSize(out restored) &&
                        restored.Width == 420 && restored.Height == 320,
                        "Float size must persist");
                    Size invalid;
                    service.SetContinuousLookupEnabled(false);
                    Require(!service.ContinuousLookupEnabled, "Toggle off must persist");
                    service.SetFloatSize(new Size(1280, 720));
                    using (var restarted = new ServiceManager())
                    {
                        Require(restarted.TryGetFloatSize(out restored) && restored == new Size(1280, 720),
                            "A valid wide float must survive restart rather than be discarded above 1000 pixels");
                    }
                    // A directory as destination deterministically rejects the save without touching real preferences.
                    string blocked = Path.Combine(Path.GetTempPath(), "rtd-size-blocked-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(blocked);
                    typeof(ServiceManager).GetField("preferences", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(service, new PreferenceStore(blocked, null));
                    Require(!service.TrySetFloatSize(new Size(540, 360)),
                        "Denied preference save must return failure instead of throwing");
                    Require(service.TryGetFloatSize(out restored) && restored == new Size(540, 360),
                        "Denied save must retain the current-session size");
                    using (var form = new SelectionAnalysisForm(service, null))
                    {
                        form.Size = new Size(600, 400);
                        typeof(SelectionAnalysisForm).GetMethod("PersistSize", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(form, null);
                        Label state = (Label)typeof(SelectionAnalysisForm).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(form);
                        Require(form.Size == new Size(600, 400) && state.Text.Contains("无法保存"),
                            "Resize-save failure must retain the size and visibly explain persistence failure");
                    }
                    Console.WriteLine("resize-save-failure-session-retention-and-ui-warning-ok");
                }
                Console.WriteLine("continuous-preferences-ok");
            }
            finally
            {
                ServiceManager.PreferencePathOverrideForDiagnostics = null;
                try { File.Delete(isolated); } catch { }
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            try
            {
                CheckGesture();
                CheckGates();
                CheckPreferences();
                Console.WriteLine("continuous-lookup-gesture-gates-preferences-ok");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine(error.ToString());
                return 1;
            }
        }
    }
}
