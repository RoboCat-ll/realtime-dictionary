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
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int WindowStyle(IntPtr hwnd, int index);
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
                    service.SetSelectionToolbarEnabled(false);
                    service.SetContinuousLookupEnabled(true);
                    Require(service.SelectionToolbarEnabled && service.ContinuousLookupEnabled,
                        "One action must enable continuous lookup and its prerequisite");
                    service.SetSelectionToolbarEnabled(false);
                    Require(!service.ContinuousLookupEnabled, "Master off must also turn off continuous lookup");
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
                    string corrupt = Path.Combine(Path.GetTempPath(), "rtd-corrupt-prefs-" + Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(corrupt, "{broken");
                    typeof(ServiceManager).GetField("preferences", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(service, new PreferenceStore(corrupt, delegate { }));
                    int warnings = 0;
                    service.PreferenceSaveFailed = delegate { warnings++; };
                    service.SetContinuousLookupEnabled(true);
                    service.SetCaptionArchiveEnabled(false);
                    Require(service.ContinuousLookupEnabled && !service.CaptionArchiveEnabled && warnings == 2,
                        "Failed UI preference saves must retain disclosed session values");
                    Require(!service.TrySetFloatSize(new Size(550, 370)) && warnings == 3 &&
                        File.ReadAllText(corrupt) == "{broken", "Corrupt preferences caused a resize exception or overwrite");
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
                    File.Delete(corrupt);
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
                CheckMessageHints();
                Console.WriteLine("continuous-lookup-gesture-gates-preferences-ok");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine(error.ToString());
                return 1;
            }
        }

        private static void CheckMessageHints()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var context = (OverlayContext)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(OverlayContext));
            using (var source = new Form { Location = new Point(100, 100), Size = new Size(500, 400) })
            using (var hint = new StatusForm(true))
            {
                source.Show();
                Application.DoEvents();
                // Preserve whichever desktop window is actually foreground;
                // Windows may legitimately refuse activation from this test process.
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                Require(foreground != IntPtr.Zero, "A foreground window is needed for the preservation check");
                typeof(OverlayContext).GetField("messageHintWindow", flags).SetValue(context, hint);
                var show = typeof(OverlayContext).GetMethod("ShowMessageHint", flags);
                var hide = typeof(OverlayContext).GetMethod("HideMessageHint", flags);
                show.Invoke(context, new object[] { "点击消息 · 10 秒", source.Handle, 300, 1 });
                Application.DoEvents();
                Require(hint.Visible, "Armed hint must be visible");
                var hintLabel = (Label)hint.Controls[0];
                Require(TextRenderer.MeasureText(hintLabel.Text, hintLabel.Font).Width <= hintLabel.ClientSize.Width,
                    "Armed hint wording must fit the compact reading area");
                show.Invoke(context, new object[] { "未读到 · 选词后按 Ctrl+Alt+D", source.Handle, 300, 1 });
                Require(TextRenderer.MeasureText(hintLabel.Text, hintLabel.Font).Width <= hintLabel.ClientSize.Width,
                    "Capture recovery shortcut must fit the hint");
                using (var quietHint = new StatusForm()) {
                    quietHint.ShowMessage("未读到 · 选词后按 Ctrl+Alt+D",
                        new NativeRect { Left=source.Left, Top=source.Top, Right=source.Right, Bottom=source.Bottom }, 300);
                    var quietLabel = (Label)quietHint.Controls[0];
                    Require(TextRenderer.MeasureText(quietLabel.Text, quietLabel.Font).Width <= quietLabel.ClientSize.Width,
                        "Continuous capture recovery shortcut must fit the hint");
                }
                Require(NativeMethods.GetForegroundWindow() == foreground, "Hint must not steal focus: before=" +
                    foreground + " after=" + NativeMethods.GetForegroundWindow() + " hint=" + hint.Handle +
                    " ex=" + WindowStyle(hint.Handle, -20).ToString("X") + " style=" + WindowStyle(hint.Handle, -16).ToString("X"));
                var parameters = (CreateParams)typeof(StatusForm).GetProperty("CreateParams", flags).GetValue(hint, null);
                Require((parameters.ExStyle & 0x20) != 0 && (parameters.ExStyle & 0x80000) != 0,
                    "Hint must be a transparent layered window so message clicks pass through");
                show.Invoke(context, new object[] { "正在读取", source.Handle, 300, 2 });
                hide.Invoke(context, new object[] { 1 });
                Require(hint.Visible, "A stale request must not hide the current hint");
                hide.Invoke(context, new object[] { 2 });
                Require(!hint.Visible, "Current request must clear its hint");
                show.Invoke(context, new object[] { "请点击消息", source.Handle, 250, 3 });
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (hint.Visible && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(15); }
                Require(!hint.Visible, "Hint must expire without user interaction");
                show.Invoke(context, new object[] { "请点击消息", source.Handle, 2000, 4 });
                source.Hide();
                Application.DoEvents();
                Require(NativeMethods.GetForegroundWindow() != source.Handle,
                    "A hidden source must no longer own foreground");
                typeof(OverlayContext).GetMethod("TrackTarget", flags).Invoke(context, new object[] { null, EventArgs.Empty });
                Require(!hint.Visible, "Hint must hide when its source loses foreground");
                Console.WriteLine("message-hint-focus-transparency-generation-expiry-ok");
            }
        }
    }
}
