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
    internal sealed partial class OverlayContext : ApplicationContext
    {
        private void OnWinEvent(
            IntPtr hook,
            uint eventType,
            IntPtr hwnd,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (!active || hwnd == IntPtr.Zero || targetWindow == IntPtr.Zero)
                return;
            if (services.WorkMode == "caption" && definitionWindow.Visible)
                return;
            if (eventType == NativeMethods.EventObjectLocationChange)
            {
                if (hwnd == targetWindow && objectId == NativeMethods.ObjIdWindow)
                    QueueGeometryUpdate();
                else if (hwnd == targetWindow || NativeMethods.IsChild(targetWindow, hwnd))
                    QueueContentRefresh(ContentRefreshDelay);
                return;
            }
            if (eventType != NativeMethods.EventObjectNameChange &&
                eventType != NativeMethods.EventObjectValueChange)
                return;
            if (hwnd == targetWindow)
            {
                QueueContentRefresh(ContentRefreshDelay);
            }
            else if (NativeMethods.IsChild(targetWindow, hwnd))
            {
                QueueContentRefresh(ContentRefreshDelay);
            }
        }

        private IntPtr OnMouseHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && MessageClickArmed &&
                (message.ToInt64() == 0x0201 || message.ToInt64() == NativeMethods.WmLButtonUp ||
                 message.ToInt64() == NativeMethods.WmMouseWheel || message.ToInt64() == 0x0204))
            {
                var selectionData = (NativeMethods.MouseHookData)Marshal.PtrToStructure(
                    data, typeof(NativeMethods.MouseHookData));
                Point point = new Point(selectionData.point.X, selectionData.point.Y);
                int selectionMessage = (int)message.ToInt64();
                try { dispatcher.BeginInvoke(new Action(delegate { ObserveSelectionGesture(selectionMessage, point); })); }
                catch { }
            }
            // 连续查词模式：只观察左键按下，不拦截；armed 单次流程优先，不参与跟踪。
            if (code >= 0 && message.ToInt64() == 0x0201 &&
                services.ContinuousLookupEnabled && services.SelectionToolbarEnabled &&
                !MessageClickArmed)
            {
                var hookData = (NativeMethods.MouseHookData)Marshal.PtrToStructure(
                    data, typeof(NativeMethods.MouseHookData));
                Point point = new Point(hookData.point.X, hookData.point.Y);
                try { dispatcher.BeginInvoke(new Action(delegate { ObserveContinuousTrigger(point); })); }
                catch { }
            }
            if (code >= 0 && active && targetWindow != IntPtr.Zero)
            {
                long msg = message.ToInt64();
                if (msg == NativeMethods.WmMouseWheel || msg == NativeMethods.WmLButtonUp)
                {
                    NativeMethods.MouseHookData hookData =
                        (NativeMethods.MouseHookData)Marshal.PtrToStructure(
                            data, typeof(NativeMethods.MouseHookData));
                    bool overHighlight = false;
                    foreach (HighlightForm window in windows)
                    {
                        if (window.Visible && window.Bounds.Contains(hookData.point.X, hookData.point.Y))
                        {
                            overHighlight = true;
                            break;
                        }
                    }
                    bool overDefinition = definitionWindow.Visible &&
                        definitionWindow.Bounds.Contains(hookData.point.X, hookData.point.Y);
                    if (msg == NativeMethods.WmLButtonUp &&
                        !overHighlight && !overDefinition && definitionWindow.Visible)
                    {
                        try { dispatcher.BeginInvoke(new Action(HideDefinition)); }
                        catch { }
                    }
                    if ((!overHighlight || msg == NativeMethods.WmMouseWheel) && !overDefinition &&
                        targetRect.Contains(hookData.point.X, hookData.point.Y))
                    {
                        if (msg == NativeMethods.WmMouseWheel)
                            QueueContentRefresh(ContentRefreshDelay, true);
                        else
                            ScheduleRefresh(250);
                    }
                }
            }
            return NativeMethods.CallNextHookEx(mouseHook, code, message, data);
        }

        private void DismissSelectionAction()
        {
            selectionGeneration++;
            selectionDragStart = null;
            selectionAction.Hide();
        }

        /* ---------------- 连续查词模式（只观察的触发监听，非自动扫描） ---------------- */

        private DateTime contLastDownUtc = DateTime.MinValue;
        private Point contLastDownPoint;
        private IntPtr contLastDownWindow = IntPtr.Zero;

        // 纯函数：判定一次左键按下是否构成有效触发。
        // double_click：同一窗口、系统双击时间与距离阈值内命中则触发并重置（第三次按下不连锁）；
        // alt_click：按住 Alt 的每次按下都是明确触发。
        internal static bool EvaluateContinuousGesture(string trigger, bool altHeld,
            Point point, IntPtr hwnd, DateTime nowUtc, int doubleClickMs, int doubleClickWidth,
            int doubleClickHeight, ref DateTime lastDownUtc, ref Point lastDownPoint,
            ref IntPtr lastDownWindow)
        {
            if (trigger == "alt_click")
                return altHeld;
            bool hit = lastDownWindow == hwnd && lastDownUtc != DateTime.MinValue &&
                (nowUtc - lastDownUtc).TotalMilliseconds <= doubleClickMs &&
                Math.Abs(point.X - lastDownPoint.X) <= doubleClickWidth &&
                Math.Abs(point.Y - lastDownPoint.Y) <= doubleClickHeight;
            lastDownUtc = hit ? DateTime.MinValue : nowUtc;
            lastDownPoint = point;
            lastDownWindow = hwnd;
            return hit;
        }

        // 门控（纯函数）：连续模式只作用于 QQ／微信前台窗口的明确手势；
        // armed 单次流程优先，桌面/列表/其他应用一律不处理。
        internal static bool ShouldHandleContinuousPress(bool continuousEnabled,
            bool toolbarEnabled, bool armed, bool ownWindow, string appClass)
        {
            return continuousEnabled && toolbarEnabled && !armed && !ownWindow &&
                appClass != "other";
        }

        private void ObserveContinuousTrigger(Point point)
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            bool ownWindow = foreground != IntPtr.Zero && IsOwnWindow(foreground);
            if (!ShouldHandleContinuousPress(services.ContinuousLookupEnabled,
                services.SelectionToolbarEnabled, MessageClickArmed, ownWindow,
                foreground == IntPtr.Zero ? "other" : ClassifyChatApp(foreground))) return;
            bool altHeld = (NativeMethods.GetKeyState(NativeMethods.VkMenu) & 0x8000) != 0;
            bool hit = EvaluateContinuousGesture(services.ContinuousLookupTrigger, altHeld,
                point, foreground, DateTime.UtcNow, SystemInformation.DoubleClickTime,
                SystemInformation.DoubleClickSize.Width, SystemInformation.DoubleClickSize.Height,
                ref contLastDownUtc, ref contLastDownPoint, ref contLastDownWindow);
            if (!hit) return;
            DismissSelectionAction();   // 递增 generation：旧消息的迟到结果不得覆盖新消息
            trayStatusItem.Text = "状态：正在读取消息…";
            services.Log("Continuous lookup trigger (" + services.ContinuousLookupTrigger +
                ", " + ClassifyChatApp(foreground) + ")");
            ProbeMessageClick(point, foreground, selectionGeneration, true);
        }

        private void ObserveSelectionGesture(int message, Point point)
        {
            if (!MessageClickArmed) return;
            if (selectionAction.Visible && selectionAction.Bounds.Contains(point)) return;
            if (message == 0x0201)
            {
                DismissSelectionAction();
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero || IsOwnWindow(foreground)) return;
                if (ClassifyChatApp(foreground) == "other") return;
                selectionTarget = foreground;
                selectionDragStart = point;
            }
            else if (message == NativeMethods.WmMouseWheel || message == 0x0204)
            {
                DismissSelectionAction();
                DisarmMessageClick("cancelled by another gesture");
            }
            else if (message == NativeMethods.WmLButtonUp && selectionDragStart.HasValue)
            {
                Point start = selectionDragStart.Value;
                selectionDragStart = null;
                DisarmMessageClick("target gesture consumed");
                trayStatusItem.Text = "状态：正在读取消息…";
                services.Log("One-click gesture: " +
                    (Math.Abs(start.X - point.X) < 8 && Math.Abs(start.Y - point.Y) < 8
                        ? "click" : "drag") + ", " + ClassifyChatApp(selectionTarget));
                if (Math.Abs(start.X - point.X) < 8 && Math.Abs(start.Y - point.Y) < 8)
                {
                    ProbeMessageClick(point, selectionTarget, selectionGeneration);
                    return;
                }
                ProbeSelection(start, point, selectionTarget, selectionGeneration);
            }
        }

        private async void ProbeMessageClick(Point point, IntPtr target, int generation,
            bool quietFailure = false)
        {
            services.Log("One-click probe entered: " + ClassifyChatApp(target));
            if (ClassifyChatApp(target) == "other") return;
            await Task.Delay(120);
            services.Log("One-click probe resumed");
            if (generation != selectionGeneration || NativeMethods.GetForegroundWindow() != target)
            {
                services.Log("One-click message cancelled before capture: " +
                    (generation != selectionGeneration ? "new gesture" : "foreground changed"));
                trayStatusItem.Text = "状态：读取已取消，请重新选择消息";
                return;
            }
            if (Interlocked.CompareExchange(ref selectionProbeBusy, 1, 0) != 0)
            {
                services.Log("One-click message capture skipped: previous probe busy");
                trayStatusItem.Text = "状态：上一条消息仍在读取，请稍后重试";
                return;
            }
            Stopwatch probeWatch = Stopwatch.StartNew();
            services.Log("One-click capture started");
            MessageProbeResult result = null;
            try
            {
                result = await Task.Factory.StartNew(delegate
                {
                    NativeRect nativeWindow;
                    if (!NativeMethods.GetWindowRect(target, out nativeWindow)) return null;
                    Rectangle window = new Rectangle(nativeWindow.Left, nativeWindow.Top,
                        nativeWindow.Width, nativeWindow.Height);
                    MessageProbeResult accessible = MessageTextReader.ReadAtPointBounded(point, window, 1000);
                    if (accessible != null) return accessible;
                    Rectangle bubble;
                    if (!MessageBubbleDetector.TryFind(window, point, out bubble))
                    {
                        services.Log("One-click message capture failed: bubble not located");
                        return null;
                    }
                    string text = services.ReadSelectionRegion(bubble);
                    if (String.IsNullOrWhiteSpace(text)) return null;
                    return new MessageProbeResult {
                        Text = text.Trim(), Bounds = bubble, Source = "bubble_ocr", Exact = false };
                });
            }
            catch (Exception error)
            {
                services.Log("One-click message probe failed: " + error.GetType().Name);
            }
            finally { Interlocked.Exchange(ref selectionProbeBusy, 0); }
            if (generation != selectionGeneration || NativeMethods.GetForegroundWindow() != target)
            {
                services.Log("One-click message cancelled after capture: " +
                    (generation != selectionGeneration ? "new gesture" : "foreground changed"));
                return;
            }
            if (result == null || String.IsNullOrWhiteSpace(result.Text))
            {
                if (quietFailure)
                {
                    // 连续查词：无法可靠确认消息时只给轻量提示——不发附近文字、
                    // 不读剪贴板、不猜测，也不用气球打扰正常聊天。
                    trayStatusItem.Text = "状态：未读到消息（连续查词）";
                    NativeRect failRect;
                    if (NativeMethods.GetWindowRect(target, out failRect))
                        statusWindow.ShowMessage("没有读到完整消息", failRect, 1600);
                    services.Log("Continuous lookup: message not confirmed at gesture point");
                }
                else
                {
                    trayStatusItem.Text = "状态：未读到消息，请重新按 Ctrl+Alt+K";
                    ShowNotice("没有读到完整消息。请重新按 Ctrl+Alt+K 后再点击一次。",
                        ToolTipIcon.Warning);
                }
                return;
            }
            services.Log("One-click message captured (" + result.Text.Length + " chars, " +
                result.Source + ", " + result.Bounds.Width + "x" + result.Bounds.Height +
                ", " + probeWatch.ElapsedMilliseconds + "ms)");
            OpenSelectionResult(result.Text, result.Exact, result.Source, true,
                result.Bounds, target);
            trayStatusItem.Text = "状态：消息解释已打开";
        }

        private async void ProbeSelection(Point start, Point end, IntPtr target, int generation)
        {
            await Task.Delay(130);
            if (generation != selectionGeneration || NativeMethods.GetForegroundWindow() != target ||
                Interlocked.CompareExchange(ref selectionProbeBusy, 1, 0) != 0) return;
            Task<string> probe = Task.Factory.StartNew(delegate {
                try { return ManualLookupForm.ReadSelection(1000, true); }
                finally { Interlocked.Exchange(ref selectionProbeBusy, 0); }
            });
            Task completed = await Task.WhenAny(probe, Task.Delay(800));
            if (generation != selectionGeneration || NativeMethods.GetForegroundWindow() != target ||
                !services.SelectionToolbarEnabled || selectionAction.IsDisposed) return;
            string text = completed == probe && !probe.IsFaulted ? probe.Result : String.Empty;
            NativeRect window;
            if (!NativeMethods.GetWindowRect(target, out window)) return;
            Rectangle region = SelectionActionForm.DragRegion(start, end,
                new Rectangle(window.Left, window.Top, window.Width, window.Height));
            if (String.IsNullOrWhiteSpace(text) && region.IsEmpty) return;
            selectionAction.Present(text, end, region);
            trayStatusItem.Text = "状态：已定位选区，请点击“解释这段”";
        }

        private async void ExplainSelection()
        {
            string text = selectionAction.SelectedText;
            Rectangle region = selectionAction.SelectedRegion;
            IntPtr target = selectionTarget;
            DismissSelectionAction();
            int generation = selectionGeneration;
            if (NativeMethods.GetForegroundWindow() != target) return;
            if (String.IsNullOrWhiteSpace(text))
            {
                if (region.IsEmpty) return;
                ShowNotice("正在本地识别所选区域…", ToolTipIcon.Info);
                try
                {
                    text = await Task.Factory.StartNew(delegate { return services.ReadSelectionRegion(region); });
                }
                catch { text = String.Empty; }
                if (generation != selectionGeneration || NativeMethods.GetForegroundWindow() != target) return;
                OpenSelectionResult(text, false, "ocr", false,
                    region.IsEmpty ? new Rectangle(selectionAction.Location, new Size(1, 1)) : region,
                    target);
            }
            else OpenSelectionResult(text, true, "accessibility", true,
                region.IsEmpty ? new Rectangle(selectionAction.Location, new Size(1, 1)) : region,
                target);
        }

        private void OpenSelectionResult(string text, bool exactSelection, string textSource,
            bool autoAnalyze, Rectangle anchor, IntPtr target)
        {
            if (selectionAnalysis != null && !selectionAnalysis.IsDisposed)
                selectionAnalysis.Close();
            selectionAnalysis = new SelectionAnalysisForm(services, OpenReminderEditor);
            selectionAnalysis.SetAnchor(anchor, target);
            selectionAnalysis.OpenText(text, exactSelection, textSource,
                ClassifyChatApp(selectionTarget), autoAnalyze);
        }

    }
}
