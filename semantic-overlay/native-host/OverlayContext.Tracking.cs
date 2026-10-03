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
        private void TrackTarget(object sender, EventArgs args)
        {
            if (messageClickArmedUntilUtc != DateTime.MinValue && !MessageClickArmed)
            {
                DisarmMessageClick("expired");
                DismissSelectionAction();
                if (!active)
                    trayStatusItem.Text = "状态：待命，按 Ctrl+Alt+K 后点击消息";
            }
            if (selectionAction != null && selectionAction.Visible &&
                (DateTime.UtcNow > selectionAction.ExpiresUtc ||
                 NativeMethods.GetForegroundWindow() != selectionTarget))
                DismissSelectionAction();
            if (!active || targetWindow == IntPtr.Zero)
                return;
            if (!NativeMethods.IsWindow(targetWindow))
            {
                DisableSession();
                return;
            }
            if (NativeMethods.IsIconic(targetWindow))
            {
                HideHighlights();
                captionLyricWindow.Hide();
                captionStatusWindow.Hide();
                return;
            }

            UpdateTargetGeometry();

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != targetWindow)
            {
                HideHighlights();
                captionLyricWindow.Hide();
                statusWindow.Hide();
                captionStatusWindow.Hide();
                HideDefinition();
            }
            else
            {
                if (services.WorkMode == "caption" && audioCapture != null)
                    ShowAudioDisplay();
                else if (services.WorkMode == "caption" &&
                         !String.IsNullOrWhiteSpace(pendingCaptionText))
                    captionLyricWindow.ShowLines(
                        PreviousCaptionText(), pendingCaptionText, targetRect);
                if (services.WorkMode == "caption" && active &&
                    !String.IsNullOrEmpty(captionStatusText) && !captionStatusWindow.Visible)
                    captionStatusWindow.ShowState(captionStatusText, targetRect);

                if (highlightsCurrent && relativeHighlights.Count > 0 && !highlightsVisible)
                    ShowHighlightWindows();
                else if (scanRunning && services.WorkMode != "caption")
                    statusWindow.ShowScanning(targetRect);
            }

            DateTime now = DateTime.UtcNow;
            if (foreground == targetWindow && !choosingScanRegion && !definitionWindow.Visible &&
                !scanRunning && highlightsCurrent && scrollFrame != null && CanTrackScroll() && now >= scrollProbeUtc)
            {
                scrollProbeUtc = now.AddMilliseconds(scrollUntilUtc > now ? 16 : 40);
                QueueScrollProbe();
            }
            if (foreground == targetWindow && scrollUntilUtc > now)
            {
                if (now >= scrollProbeUtc)
                {
                    scrollProbeUtc = now.AddMilliseconds(16);
                    QueueScrollProbe();
                }
                return;
            }
            if (foreground == targetWindow && services.WorkMode == "caption" &&
                !definitionWindow.Visible && !pendingCaptionCommitted &&
                !String.IsNullOrWhiteSpace(pendingCaptionText) &&
                now - pendingCaptionChangedUtc >=
                    TimeSpan.FromMilliseconds(CaptionStabilityDelay))
                CommitPendingCaption(true);
            if (foreground == targetWindow && services.WorkMode == "caption" &&
                !definitionWindow.Visible)
                TryStartCaptionRefinement();

            DateTime due;
            lock (refreshLock)
                due = refreshDueUtc;
            bool popupBlocksScheduledScan = PopupBlocksScheduledScan(
                services.WorkMode, definitionWindow.Visible);
            if (!scanRunning && !popupBlocksScheduledScan && due != DateTime.MaxValue &&
                DateTime.UtcNow >= due && NativeMethods.GetForegroundWindow() == targetWindow)
                StartScan();
        }

        internal static bool PopupBlocksScheduledScan(string workMode, bool definitionVisible)
        {
            // A definition is the user's active task in every work mode.  A
            // periodic OCR refresh must not make the card flash and disappear.
            return definitionVisible;
        }

        private void QueueGeometryUpdate()
        {
            if (Interlocked.Exchange(ref geometryUpdateQueued, 1) != 0)
                return;
            try
            {
                dispatcher.BeginInvoke(new Action(delegate
                {
                    Interlocked.Exchange(ref geometryUpdateQueued, 0);
                    UpdateTargetGeometry();
                }));
            }
            catch
            {
                Interlocked.Exchange(ref geometryUpdateQueued, 0);
            }
        }

        private void UpdateTargetGeometry()
        {
            if (!active || targetWindow == IntPtr.Zero)
                return;
            NativeRect current;
            if (!TryGetUsableRect(targetWindow, out current))
                return;

            bool sizeChanged = Math.Abs(current.Width - targetRect.Width) > 2 ||
                               Math.Abs(current.Height - targetRect.Height) > 2;
            bool moved = current.Left != targetRect.Left || current.Top != targetRect.Top;
            if (sizeChanged)
            {
                targetRect = current;
                InvalidateContentAndSchedule(ContentRefreshDelay);
                services.Log("Target resized; invalidated stale highlights");
            }
            else if (moved)
            {
                targetRect = current;
                PositionHighlightWindows();
                if (services.WorkMode == "caption" && captionLyricWindow.Visible)
                    captionLyricWindow.PositionFor(targetRect);
                statusWindow.PositionFor(targetRect);
                captionStatusWindow.PositionFor(targetRect);
                if (UseAssistantPresentation())
                    assistantPanel.PositionFor(targetRect);
                if (selectedHighlight != null && definitionWindow.Visible)
                    definitionWindow.PositionFor(selectedHighlight.Bounds);
            }
        }

        private long RenderHighlights()
        {
            Stopwatch renderWatch = Stopwatch.StartNew();
            preserveTrackedHighlights = false;
            trackingViewport = null;
            relativeHighlights.RemoveAll(delegate(HighlightItem item)
            {
                if (item == null) return true;
                bool task = String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase);
                if (task) return !services.ExperimentalFeaturesEnabled;
                return services.ShouldSuppressTerm(item.term);
            });
            if (relativeHighlights.Count > 5)
                relativeHighlights.RemoveRange(5, relativeHighlights.Count - 5);
            assistantPanel.SetItems(relativeHighlights);
            while (windows.Count < relativeHighlights.Count)
            {
                HighlightForm window = new HighlightForm();
                window.ItemClicked += BeginItemAction;
                window.TermIgnored += IgnoreHighlight;
                windows.Add(window);
            }
            for (int index = relativeHighlights.Count; index < windows.Count; index++)
                windows[index].Hide();
            PositionHighlightWindows();
            if (NativeMethods.GetForegroundWindow() == targetWindow)
                ShowHighlightWindows();
            if (services.WorkMode != "caption" && NativeMethods.GetForegroundWindow() == targetWindow)
            {
                try
                {
                    foreach (HighlightForm window in windows)
                        if (window.Visible) window.Update();
                    NativeMethods.DwmFlush();
                    displayedFingerprint = services.ComputeScreenFingerprint(targetRect);
                    if (CanTrackScroll()) scrollFrame = ScrollFrame.Capture(CurrentTrackingRect());
                }
                catch { displayedFingerprint = null; }
            }
            services.Log("Highlight render: " + relativeHighlights.Count +
                " windows, " + renderWatch.ElapsedMilliseconds + "ms");
            services.RecordHighlightMetric(ClassifyChatApp(targetWindow),
                relativeHighlights.Count, "rendered");
            return renderWatch.ElapsedMilliseconds;
        }

        private void PositionHighlightWindows()
        {
            if (UseAssistantPresentation())
            {
                assistantPanel.PositionFor(targetRect);
                return;
            }
            if (relativeHighlights.Count == 0)
                return;
            Rectangle[] boundsByIndex = new Rectangle[relativeHighlights.Count];
            IntPtr deferred = NativeMethods.BeginDeferWindowPos(relativeHighlights.Count);
            bool batchFailed = deferred == IntPtr.Zero;
            for (int index = 0; index < relativeHighlights.Count; index++)
            {
                HighlightItem item = relativeHighlights[index];
                Rectangle bounds = new Rectangle(
                    targetRect.Left + item.x,
                    targetRect.Top + item.y,
                    Math.Max(3, item.w),
                    Math.Max(3, item.h));
                boundsByIndex[index] = bounds;
                windows[index].SetHighlightBounds(bounds, item);
                if (windows[index].Visible && !batchFailed)
                {
                    IntPtr next = NativeMethods.DeferWindowPos(
                        deferred,
                        windows[index].Handle,
                        NativeMethods.HwndTopMost,
                        bounds.Left,
                        bounds.Top,
                        bounds.Width,
                        bounds.Height,
                        NativeMethods.SwpNoActivate);
                    if (next == IntPtr.Zero)
                        batchFailed = true;
                    else
                        deferred = next;
                }
            }
            bool batchApplied = !batchFailed && NativeMethods.EndDeferWindowPos(deferred);
            if (!batchApplied)
            {
                for (int index = 0; index < relativeHighlights.Count; index++)
                {
                    if (windows[index].Visible)
                    {
                        Rectangle bounds = boundsByIndex[index];
                        NativeMethods.SetWindowPos(
                            windows[index].Handle,
                            NativeMethods.HwndTopMost,
                            bounds.Left,
                            bounds.Top,
                            bounds.Width,
                            bounds.Height,
                            NativeMethods.SwpNoActivate);
                    }
                }
            }
        }

        private void ShowHighlightWindows()
        {
            HashSet<string> visibleTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (UseAssistantPresentation())
            {
                foreach (HighlightForm window in windows)
                    window.Hide();
                foreach (HighlightItem item in relativeHighlights)
                    if (item != null && !String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase))
                        visibleTerms.Add(item.term);
                assistantPanel.ShowInactive();
                services.UpdateVisibleTerms(visibleTerms);
                highlightsVisible = true;
                return;
            }
            assistantPanel.Hide();
            for (int index = 0; index < relativeHighlights.Count; index++)
            {
                if (trackingViewport.HasValue)
                    windows[index].ClipTo(trackingViewport.Value);
                else windows[index].ClearClip();
                windows[index].ShowInactive();
                HighlightItem item = relativeHighlights[index];
                if (windows[index].IsHighlightVisible && item != null &&
                    !String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase))
                    visibleTerms.Add(item.term);
            }
            services.UpdateVisibleTerms(visibleTerms);
            highlightsVisible = relativeHighlights.Count > 0;
        }

        private void HideHighlights()
        {
            services.UpdateVisibleTerms(new string[0]);
            foreach (HighlightForm window in windows)
                window.Hide();
            assistantPanel.Hide();
            highlightsVisible = false;
        }

        private bool UseAssistantPresentation()
        {
            return services.WorkMode != "caption" && services.PresentationMode == "assistant";
        }

    }
}
