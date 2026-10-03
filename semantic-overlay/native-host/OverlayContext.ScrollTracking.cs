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
        private void ScheduleRefresh(int delayMilliseconds)
        {
            if (!active)
                return;
            lock (refreshLock)
                refreshDueUtc = DateTime.UtcNow.AddMilliseconds(delayMilliseconds);
        }

        private void InvalidateContentAndSchedule(int delayMilliseconds)
        {
            if (!active)
                return;
            scrollTrackingGeneration++;
            scrollTrackingFailures = 0;
            scrollFrame = null;
            scrollUntilUtc = DateTime.MinValue;
            preserveTrackedHighlights = false;
            trackingViewport = null;
            HideHighlights();
            HideDefinition();
            highlightsCurrent = false;
            scanGeneration++;
            lastCaptureFingerprint = null;
            displayedFingerprint = null;
            queuedRefinementWords = null;
            queuedRefinementFallback = null;
            ScheduleRefresh(delayMilliseconds);
        }

        private void QueueContentRefresh(int delayMilliseconds, bool definiteMovement = false)
        {
            if (definiteMovement) Interlocked.Exchange(ref definiteContentMovementQueued, 1);
            if (Interlocked.Exchange(ref contentRefreshQueued, 1) != 0)
                return;
            try
            {
                dispatcher.BeginInvoke(new Action(delegate
                {
                    Interlocked.Exchange(ref contentRefreshQueued, 0);
                    if (Interlocked.Exchange(ref definiteContentMovementQueued, 0) != 0)
                        BeginScrollTracking(delayMilliseconds);
                    else
                        ScheduleRefresh(delayMilliseconds);
                }));
            }
            catch
            {
                Interlocked.Exchange(ref contentRefreshQueued, 0);
            }
        }

        private NativeRect CurrentTrackingRect()
        {
            return customRegionWindow == targetWindow
                ? ScanRegionForm.MapRegion(customRegion, targetRect)
                : GetPreferredScanRect(targetWindow, targetRect, services.ScanScope, services.WorkMode);
        }

        private bool CanTrackScroll()
        {
            if (services.WorkMode == "caption" || windows.Count == 0) return false;
            foreach (HighlightForm window in windows)
                if (!window.CaptureExcluded) return false;
            return true;
        }

        private void BeginScrollTracking(int delay)
        {
            if (!CanTrackScroll() || scrollFrame == null || !highlightsCurrent)
            {
                InvalidateContentAndSchedule(delay);
                return;
            }
            HideDefinition(); statusWindow.Hide();
            scanGeneration++; // Ignore results captured before this scroll.
            queuedRefinementWords = null; queuedRefinementFallback = null;
            lastCaptureFingerprint = null; displayedFingerprint = null;
            preserveTrackedHighlights = true;
            scrollUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
            scrollProbeUtc = DateTime.MinValue;
            ScheduleRefresh(550);
        }

        private void TrackScrollFrame()
        {
            try
            {
                NativeRect viewport = CurrentTrackingRect();
                ScrollFrame next = ScrollFrame.Capture(viewport);
                int delta;
                if (scrollFrame == null || !scrollFrame.TryDisplacement(next, out delta))
                {
                    HandleUncertainScrollFrame();
                    return;
                }
                ApplyScrollDisplacement(next, viewport, delta);
            }
            catch { HandleUncertainScrollFrame(); }
        }

        private void QueueScrollProbe()
        {
            if (Interlocked.CompareExchange(ref scrollProbeRunning, 1, 0) != 0)
                return;
            ScrollFrame previous = scrollFrame;
            NativeRect viewport = CurrentTrackingRect();
            int trackingGeneration = scrollTrackingGeneration;
            IntPtr trackedWindow = targetWindow;
            bool expectMotion = scrollUntilUtc > DateTime.UtcNow;
            Task.Factory.StartNew(delegate
            {
                ScrollProbeResult result = new ScrollProbeResult();
                result.Next = ScrollFrame.Capture(viewport);
                result.Confident = previous != null &&
                    previous.TryDisplacement(result.Next, out result.Delta);
                // DWM can return the pre-scroll surface for the first capture even
                // after the wheel event has arrived.  One bounded re-capture keeps
                // the overlay on the same visual frame as the text without turning
                // tracking into an unbounded polling loop.
                if (expectMotion && result.Confident && result.Delta == 0)
                {
                    Thread.Sleep(8);
                    ScrollFrame retry = ScrollFrame.Capture(viewport);
                    int retryDelta;
                    if (previous != null && previous.TryDisplacement(retry, out retryDelta))
                    {
                        result.Next = retry;
                        result.Delta = retryDelta;
                    }
                }
                return result;
            }).ContinueWith(delegate(Task<ScrollProbeResult> task)
            {
                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            if (!active || trackedWindow != targetWindow ||
                                trackingGeneration != scrollTrackingGeneration ||
                                previous != scrollFrame)
                                return;
                            if (task.IsFaulted || task.IsCanceled || !task.Result.Confident)
                            {
                                HandleUncertainScrollFrame();
                                return;
                            }
                            ApplyScrollDisplacement(task.Result.Next, viewport, task.Result.Delta);
                        }
                        finally { Interlocked.Exchange(ref scrollProbeRunning, 0); }
                    }));
                }
                catch { Interlocked.Exchange(ref scrollProbeRunning, 0); }
            });
        }

        private void HandleUncertainScrollFrame()
        {
            scrollTrackingFailures++;
            if (scrollTrackingFailures < 3)
            {
                // A single animation or caret frame must not erase every term.
                scrollProbeUtc = DateTime.UtcNow.AddMilliseconds(16);
                ScheduleRefresh(180);
                return;
            }
            InvalidateContentAndSchedule(ContentRefreshDelay);
        }

        private void ApplyScrollDisplacement(ScrollFrame next, NativeRect viewport, int delta)
        {
                scrollTrackingFailures = 0;
                if (delta != 0)
                {
                    scanGeneration++;
                    queuedRefinementWords = null; queuedRefinementFallback = null;
                    lastCaptureFingerprint = null; displayedFingerprint = null;
                    preserveTrackedHighlights = true;
                    foreach (HighlightItem item in relativeHighlights) item.y += delta;
                    trackingViewport = new Rectangle(viewport.Left, viewport.Top, viewport.Width, viewport.Height);
                    PositionHighlightWindows();
                    ShowHighlightWindows();
                    ScheduleRefresh(550);
                    scrollUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
                    scrollProbeUtc = DateTime.UtcNow.AddMilliseconds(16);
                }
                scrollFrame = next;
        }

    }
}
