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
        private void StartScan()
        {
            if (!active || scanRunning || choosingScanRegion || targetWindow == IntPtr.Zero)
                return;
            if (services.WorkMode == "caption")
                return;

            ScanTiming scanTiming = new ScanTiming();
            scanTiming.TotalWatch.Start();

            NativeRect captureRect;
            if (!TryGetUsableRect(targetWindow, out captureRect))
            {
                statusWindow.Hide();
                return;
            }
            NativeRect scanRect = GetPreferredScanRect(
                targetWindow, captureRect, services.ScanScope, services.WorkMode);
            if (services.WorkMode != "caption" && customRegionWindow == targetWindow)
                scanRect = ScanRegionForm.MapRegion(customRegion, captureRect);
            int scanOffsetX = scanRect.Left - captureRect.Left;
            int scanOffsetY = scanRect.Top - captureRect.Top;

            // Compare the currently displayed composition before removing our windows.
            // Generic accessibility events and clicks need not disturb a stable view.
            if (services.WorkMode != "caption" && highlightsCurrent && displayedFingerprint != null)
            {
                try
                {
                    if (services.AreVisualFingerprintsEquivalent(displayedFingerprint,
                        services.ComputeScreenFingerprint(captureRect)))
                    {
                        refreshDueUtc = DateTime.MaxValue;
                        return;
                    }
                }
                catch { /* Normal capture below reports failures to the user. */ }
            }
            displayedFingerprint = null;

            scanRunning = true;
            highlightsCurrent = false;
            refreshDueUtc = DateTime.MaxValue;
            IntPtr capturedWindow = targetWindow;
            if (services.WorkMode == "caption")
                nextCaptionProbeUtc = DateTime.UtcNow.AddMilliseconds(CaptionProbeInterval);
            if (!preserveTrackedHighlights) HideHighlights();
            HideDefinition();
            statusWindow.Hide();
            string capturePath = null;
            byte[] captureFingerprint;
            bool captionMode = services.WorkMode == "caption";
            if (captionMode)
                captionLyricWindow.Hide();
            try
            {
                // Wait for the desktop compositor to remove our windows before
                // screen capture; otherwise OCR can read the previous lyric itself.
                Stopwatch captureWatch = Stopwatch.StartNew();
                NativeMethods.DwmFlush();
                capturePath = services.Capture(scanRect);
                scanTiming.CaptureMs = captureWatch.ElapsedMilliseconds;
                Stopwatch fingerprintWatch = Stopwatch.StartNew();
                captureFingerprint = services.ComputeVisualFingerprint(capturePath);
                scanTiming.FingerprintMs = fingerprintWatch.ElapsedMilliseconds;
                if (captionMode && NativeMethods.GetForegroundWindow() == targetWindow &&
                    !String.IsNullOrWhiteSpace(pendingCaptionText))
                    captionLyricWindow.ShowLines(
                        PreviousCaptionText(), pendingCaptionText, targetRect);
            }
            catch (Exception error)
            {
                if (!String.IsNullOrEmpty(capturePath))
                {
                    try { File.Delete(capturePath); }
                    catch { }
                }
                scanRunning = false;
                if (captionMode && NativeMethods.GetForegroundWindow() == targetWindow &&
                    !String.IsNullOrWhiteSpace(pendingCaptionText))
                    captionLyricWindow.ShowLines(
                        PreviousCaptionText(), pendingCaptionText, targetRect);
                services.Log("Screen capture failed: " + error);
                trayStatusItem.Text = "状态：截屏失败";
                ShowNotice("读取屏幕失败，详情见 _native_host.log。", ToolTipIcon.Error);
                return;
            }
            if (services.AreVisualFingerprintsEquivalent(lastCaptureFingerprint, captureFingerprint))
            {
                try { File.Delete(capturePath); }
                catch (Exception error) { services.Log("Temporary scan cleanup failed: " + error.GetType().Name); }
                scanRunning = false;
                targetRect = captureRect;
                if (!captionMode && ((refinementRunning && runningRefinementGeneration == scanGeneration) ||
                    (queuedRefinementWords != null && queuedRefinementGeneration == scanGeneration)))
                {
                    highlightsCurrent = relativeHighlights.Count > 0;
                    statusWindow.ShowScanning(captureRect);
                    trayStatusItem.Text = relativeHighlights.Count > 0
                        ? "状态：已显示稳定词，正在补充…"
                        : "状态：正在智能筛选…";
                    services.Log("Skipped unchanged capture; retained progressive highlights while refinement is pending");
                    return;
                }
                highlightsCurrent = true;
                statusWindow.Hide();
                RenderHighlights();
                trayStatusItem.Text = "状态：内容未变化，已复用高亮";
                services.Log("Skipped unchanged capture");
                return;
            }
            int generation = ++scanGeneration;
            scanTiming.Generation = generation;
            RememberScanTiming(scanTiming);
            if (services.WorkMode != "caption")
                statusWindow.ShowScanning(captureRect);
            services.Log(
                "Scan started generation " + generation + " for " +
                scanRect.Width + "x" + scanRect.Height +
                (scanOffsetX != 0 || scanOffsetY != 0 ? " (content region)" : " (full window)"));

            Task.Factory.StartNew(delegate
            {
                try
                {
                    services.EnsureRunning();
                    return services.Scan(scanRect, capturePath);
                }
                catch (Exception error)
                {
                    return new ScanResponse { ok = false, error = error.Message };
                }
                finally
                {
                    try { File.Delete(capturePath); }
                    catch (Exception error) { services.Log("Temporary scan cleanup failed: " + error.GetType().Name); }
                }
            }).ContinueWith(delegate(Task<ScanResponse> task)
            {
                ScanResponse completedResponse;
                try
                {
                    completedResponse = task.Result;
                    services.Log(
                        "Scan response generation " + generation + ": " +
                        (completedResponse != null && completedResponse.ok ? "ok" : "failed"));
                }
                catch (Exception error)
                {
                    completedResponse = new ScanResponse { ok = false, error = error.Message };
                    services.Log("Scan task exception: " + error);
                }

                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        scanRunning = false;
                        if (!active)
                        {
                            statusWindow.Hide();
                            services.Log("Discarded scan generation " + generation + ": session inactive");
                            return;
                        }
                        if (generation != scanGeneration)
                        {
                            services.Log(
                                "Discarded scan generation " + generation +
                                ": current generation is " + scanGeneration);
                            return;
                        }
                        if (capturedWindow != targetWindow)
                        {
                            services.Log("Discarded scan generation " + generation + ": target changed");
                            return;
                        }

                        ScanResponse response = completedResponse;
                        if (response == null || !response.ok)
                        {
                            ForgetScanTiming(generation);
                            statusWindow.Hide();
                            string error = response == null ? "No OCR response" : response.error;
                            services.Log("Scan failed: " + error);
                            trayStatusItem.Text = "状态：识别失败";
                            ShowNotice("文字识别失败，详情见 _native_host.log。", ToolTipIcon.Error);
                            return;
                        }

                        NativeRect currentRect;
                        if (!TryGetUsableRect(targetWindow, out currentRect))
                        {
                            statusWindow.Hide();
                            services.Log("Scan result deferred: target minimized or unavailable");
                            return;
                        }
                        if (Math.Abs(currentRect.Width - captureRect.Width) > 2 ||
                            Math.Abs(currentRect.Height - captureRect.Height) > 2)
                        {
                            targetRect = currentRect;
                            services.Log(
                                "Scan result requires resize refresh: captured " +
                                captureRect.Width + "x" + captureRect.Height + ", current " +
                                currentRect.Width + "x" + currentRect.Height);
                            InvalidateContentAndSchedule(ContentRefreshDelay);
                            return;
                        }

                        targetRect = currentRect;
                        lastCaptureFingerprint = captureFingerprint;
                        scanTiming.OcrMs = response.ocr_ms;
                        scanTiming.LocalAnalysisMs = response.local_analysis_ms;
                        if (services.WorkMode == "caption")
                        {
                            OffsetHighlights(response, scanOffsetX, scanOffsetY);
                            relativeHighlights.Clear();
                            if (response.highlights != null)
                                relativeHighlights.AddRange(response.highlights);
                            highlightsCurrent = true;
                            statusWindow.Hide();
                            scanTiming.RenderMs = RenderHighlights();
                            UpdateCaptionFromScan(
                                generation,
                                capturedWindow,
                                captureRect,
                                response.words,
                                scanOffsetX,
                                scanOffsetY);
                            trayStatusItem.Text = "状态：字幕已标注 " + relativeHighlights.Count + " 个关键词";
                            services.Log(
                                "Rendered " + relativeHighlights.Count + " caption term windows in " +
                                response.duration_ms + "ms");
                            LogCompletedScanTiming(generation, response.analysis_mode,
                                response.analysis_duration_ms, relativeHighlights.Count);
                        }
                        else if (response.words == null || response.words.Count == 0)
                        {
                            queuedRefinementWords = null;
                            queuedRefinementFallback = null;
                            progressiveHighlightGeneration = generation;
                            relativeHighlights.Clear();
                            highlightsCurrent = true;
                            statusWindow.Hide();
                            HideHighlights();
                            trayStatusItem.Text = "状态：未识别到可标注文字";
                            LogCompletedScanTiming(generation, response.analysis_mode,
                                response.analysis_duration_ms, 0);
                        }
                        else
                        {
                            ScanResponse instant = CloneScanResponseHighlights(response);
                            OffsetHighlights(instant, scanOffsetX, scanOffsetY);
                            relativeHighlights.Clear();
                            if (instant.highlights != null)
                                relativeHighlights.AddRange(instant.highlights);
                            progressiveHighlightGeneration = generation;
                            highlightsCurrent = true;
                            statusWindow.Hide();
                            scanTiming.RenderMs += RenderHighlights();
                            scanTiming.FirstVisibleMs = relativeHighlights.Count > 0
                                ? scanTiming.TotalWatch.ElapsedMilliseconds : 0;
                            statusWindow.ShowScanning(captureRect);
                            trayStatusItem.Text = relativeHighlights.Count > 0
                                ? "状态：已显示稳定词，正在补充…"
                                : "状态：正在智能筛选…";
                            services.Log(relativeHighlights.Count > 0
                                ? "Progressive local render generation " + generation + ": " +
                                    relativeHighlights.Count + " stable highlights at " +
                                    scanTiming.FirstVisibleMs + "ms"
                                : "Progressive local render generation " + generation +
                                    ": no deterministic stable highlights");
                            QueueConversationRefinement(
                                generation,
                                capturedWindow,
                                captureRect,
                                response.words,
                                scanOffsetX,
                                scanOffsetY, response);
                        }
                    }));
                }
                catch (Exception error)
                {
                    scanRunning = false;
                    services.Log("UI dispatch failed: " + error);
                }
            });
        }

        private void QueueConversationRefinement(
            int generation,
            IntPtr capturedWindow,
            NativeRect captureRect,
            List<OcrWord> words,
            int scanOffsetX,
            int scanOffsetY, ScanResponse fallback)
        {
            queuedRefinementGeneration = generation;
            queuedRefinementWindow = capturedWindow;
            queuedRefinementRect = captureRect;
            queuedRefinementWords = new List<OcrWord>(words);
            queuedRefinementFallback = fallback;
            queuedRefinementOffsetX = scanOffsetX;
            queuedRefinementOffsetY = scanOffsetY;
            if (refinementRunning)
            {
                services.Log("Queued latest conversation refinement generation " + generation);
                return;
            }
            StartQueuedConversationRefinement();
        }

        private void ChooseScanRegion()
        {
            NativeRect rect;
            if (!active || services.WorkMode == "caption" || !TryGetUsableRect(targetWindow, out rect))
            {
                ShowNotice("先在聊天窗口按 Ctrl+Alt+K，再从此处框选识别区域。", ToolTipIcon.Info);
                return;
            }
            choosingScanRegion = true;
            InvalidateContentAndSchedule(0);
            statusWindow.Hide();
            IntPtr selectedWindow = targetWindow;
            try
            {
                NativeMethods.DwmFlush();
                using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
                    using (ScanRegionForm form = new ScanRegionForm(bitmap))
                    {
                        if (form.ShowDialog() == DialogResult.OK && targetWindow == selectedWindow)
                        {
                            customRegionWindow = selectedWindow;
                            customRegion = form.SelectedRegion;

                            NativeRect chosen = CurrentTrackingRect();
                            services.Log("Manual scope applied: " + chosen.Width + "x" + chosen.Height);
                            ShowNotice("手动框选已生效，仅识别所选区域。重新选择自动或整个窗口会清除框选。", ToolTipIcon.Info);
                        }
                    }
                }
            }
            catch (Exception) { ShowNotice("区域预览未能打开，请保持目标窗口可见后重试。", ToolTipIcon.Error); }
            finally { choosingScanRegion = false; InvalidateContentAndSchedule(0); }
        }

        private void StartQueuedConversationRefinement()
        {
            if (!active || services.WorkMode == "caption" || queuedRefinementWords == null)
                return;
            int generation = queuedRefinementGeneration;
            IntPtr capturedWindow = queuedRefinementWindow;
            NativeRect captureRect = queuedRefinementRect;
            List<OcrWord> words = queuedRefinementWords;
            ScanResponse fallback = queuedRefinementFallback;
            int scanOffsetX = queuedRefinementOffsetX;
            int scanOffsetY = queuedRefinementOffsetY;
            queuedRefinementWords = null;
            queuedRefinementFallback = null;
            if (generation != scanGeneration || capturedWindow != targetWindow)
                return;
            refinementRunning = true;
            services.Log("Started conversation refinement generation " + generation);
            StartRefinement(generation, capturedWindow, captureRect, words, scanOffsetX, scanOffsetY, fallback);
        }

        private void StartRefinement(
            int generation,
            IntPtr capturedWindow,
            NativeRect captureRect,
            List<OcrWord> words,
            int scanOffsetX,
            int scanOffsetY, ScanResponse fallback = null)
        {
            runningRefinementGeneration = generation;
            byte[] expectedFrame = lastCaptureFingerprint;
            Stopwatch refinementWatch = Stopwatch.StartNew();
            Task.Factory.StartNew(delegate
            {
                try
                {
                    return ResolveRefinement(refineWords(words), fallback);
                }
                catch (Exception error)
                {
                    services.Log("Background refinement failed: " + error.GetType().Name);
                    return ResolveRefinement(null, fallback);
                }
            }).ContinueWith(delegate(Task<ScanResponse> task)
            {
                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            ScanResponse response = task.Result;
                            if (!active || generation != scanGeneration || capturedWindow != targetWindow)
                            {
                                ForgetScanTiming(generation);
                                return;
                            }
                            bool captionMode = services.WorkMode == "caption";
                            if (response == null || !response.ok ||
                                (captionMode && response.analysis_mode != "llm"))
                            {
                                if (!captionMode)
                                {
                                    statusWindow.Hide();
                                    lastCaptureFingerprint = null;
                                    relativeHighlights.Clear();
                                    HideHighlights();
                                    trayStatusItem.Text = "状态：筛选失败，请按 Ctrl+Alt+K 重试";
                                }
                                return;
                            }

                            NativeRect currentRect;
                            if (!TryGetUsableRect(targetWindow, out currentRect) ||
                                Math.Abs(currentRect.Width - captureRect.Width) > 2 ||
                                Math.Abs(currentRect.Height - captureRect.Height) > 2)
                            {
                                statusWindow.Hide();
                                lastCaptureFingerprint = null;
                                ScheduleRefresh(ContentRefreshDelay);
                                return;
                            }

                            if (!captionMode && expectedFrame != null)
                            {
                                NativeRect verificationRect = GetPreferredScanRect(
                                    targetWindow, currentRect, services.ScanScope, services.WorkMode);
                                if (customRegionWindow == targetWindow)
                                    verificationRect = ScanRegionForm.MapRegion(customRegion, currentRect);
                                bool frameMatches;
                                try
                                {
                                    statusWindow.Hide();
                                    NativeMethods.DwmFlush();
                                    frameMatches = services.AreVisualFingerprintsEquivalent(expectedFrame,
                                        services.ComputeScreenFingerprint(verificationRect));
                                }
                                catch { frameMatches = false; }
                                if (!frameMatches)
                                {
                                    services.Log("Discarded refinement: source pixels changed before render");
                                    InvalidateContentAndSchedule(ContentRefreshDelay);
                                    return;
                                }
                            }
                            OffsetHighlights(response, scanOffsetX, scanOffsetY);
                            int addedHighlights;
                            if (!captionMode && progressiveHighlightGeneration == generation)
                            {
                                addedHighlights = MergeProgressiveHighlights(
                                    relativeHighlights, response.highlights);
                            }
                            else
                            {
                                relativeHighlights.Clear();
                                if (response.highlights != null)
                                    relativeHighlights.AddRange(response.highlights);
                                addedHighlights = relativeHighlights.Count;
                                if (!captionMode)
                                    progressiveHighlightGeneration = generation;
                            }
                            targetRect = currentRect;
                            highlightsCurrent = true;
                            statusWindow.Hide();
                            long renderMs = addedHighlights > 0 ? RenderHighlights() : 0;
                            ScanTiming completedTiming = GetScanTiming(generation);
                            if (completedTiming != null)
                            {
                                completedTiming.RefinementMs = refinementWatch.ElapsedMilliseconds;
                                completedTiming.RenderMs += renderMs;
                            }
                            bool intelligent = IsModelAnalysisMode(response.analysis_mode);
                            trayStatusItem.Text = intelligent
                                ? "状态：已智能筛选 " + relativeHighlights.Count + " 个关键词"
                                : "状态：模型不可用，已显示本地结果 " + relativeHighlights.Count + " 个";
                            if (!captionMode)
                                statusWindow.ShowMessage(
                                    intelligent ? "智能识别已完成" : "已显示本地结果",
                                    targetRect, 1000);
                            services.Log(
                                (intelligent ? "Applied additive model refinement: " : "Retained local fallback: ") +
                                addedHighlights + " added, " + relativeHighlights.Count + " total highlights");
                            if (!captionMode)
                                LogCompletedScanTiming(generation, response.analysis_mode,
                                    response.analysis_duration_ms, relativeHighlights.Count);
                        }
                        finally
                        {
                            refinementRunning = false;
                            if (services.WorkMode == "caption")
                                TryStartCaptionRefinement();
                            else
                                StartQueuedConversationRefinement();
                        }
                    }));
                }
                catch (Exception error)
                {
                    refinementRunning = false;
                    services.Log("Refinement UI dispatch failed: " + error.GetType().Name);
                }
            });
        }

        internal static ScanResponse ResolveRefinement(ScanResponse response, ScanResponse fallback)
        {
            if (response != null && response.ok && response.highlights != null)
                return response;
            if (fallback != null && fallback.ok && fallback.highlights != null)
                return new ScanResponse { ok = true, highlights = fallback.highlights,
                    words = fallback.words, analysis_mode = "local_error" };
            return new ScanResponse { ok = false, error = "No usable analysis result" };
        }

        internal static int MergeProgressiveHighlights(
            List<HighlightItem> stable, List<HighlightItem> refined)
        {
            if (stable == null || refined == null)
                return 0;
            int added = 0;
            foreach (HighlightItem candidate in refined)
            {
                if (candidate == null)
                    continue;
                HighlightItem match = stable.Find(delegate(HighlightItem existing)
                {
                    return existing != null &&
                        String.Equals(existing.term, candidate.term, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(existing.kind, candidate.kind, StringComparison.OrdinalIgnoreCase) &&
                        existing.x == candidate.x && existing.y == candidate.y &&
                        existing.w == candidate.w && existing.h == candidate.h;
                });
                if (match != null)
                {
                    match.context = candidate.context;
                    match.title = candidate.title;
                    match.time_text = candidate.time_text;
                    match.start_iso = candidate.start_iso;
                    match.end_iso = candidate.end_iso;
                    match.utc_offset = candidate.utc_offset;
                    match.needs_confirmation = candidate.needs_confirmation;
                    continue;
                }
                stable.Add(candidate);
                added++;
            }
            return added;
        }

        internal static bool IsModelAnalysisMode(string mode)
        {
            return String.Equals(mode, "llm", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(mode, "local_strong", StringComparison.OrdinalIgnoreCase);
        }

        private void RememberScanTiming(ScanTiming timing)
        {
            if (scanTimings == null || timing == null)
                return;
            scanTimings[timing.Generation] = timing;
            List<int> expired = new List<int>();
            foreach (int generation in scanTimings.Keys)
                if (generation < timing.Generation - 8)
                    expired.Add(generation);
            foreach (int generation in expired)
                scanTimings.Remove(generation);
        }

        private ScanTiming GetScanTiming(int generation)
        {
            if (scanTimings == null)
                return null;
            ScanTiming timing;
            return scanTimings.TryGetValue(generation, out timing) ? timing : null;
        }

        private void ForgetScanTiming(int generation)
        {
            if (scanTimings != null)
                scanTimings.Remove(generation);
        }

        private void LogCompletedScanTiming(
            int generation, string mode, int analysisDurationMs, int highlightCount)
        {
            ScanTiming timing = GetScanTiming(generation);
            if (timing == null)
                return;
            timing.TotalWatch.Stop();
            services.Log(String.Format(
                "E2E generation {0}: capture {1}ms, fingerprint {2}ms, OCR {3}ms, " +
                "local {4}ms, model_server {5}ms, refinement {6}ms, render {7}ms, " +
                "first_visible {8}ms, total {9}ms, mode {10}, highlights {11}",
                generation, timing.CaptureMs, timing.FingerprintMs, timing.OcrMs,
                timing.LocalAnalysisMs, analysisDurationMs, timing.RefinementMs,
                timing.RenderMs, timing.FirstVisibleMs, timing.TotalWatch.ElapsedMilliseconds,
                String.IsNullOrWhiteSpace(mode) ? "unknown" : mode, highlightCount));
            ForgetScanTiming(generation);
        }

    }
}
