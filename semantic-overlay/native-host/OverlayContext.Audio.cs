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
        private int audioChunkSequence;
        private int lastAcceptedAudioSequence = -1;
        private string lastAudioContextText;

        private void StartAudioCaptionSession()
        {
            StopAudioCaptionSession();
            int generation = ++audioSessionGeneration;
            audioFailureShown = false;
            audioTransientFailures = 0;
            audioDroppedChunks = 0;
            audioRetryNotBeforeUtc = DateTime.MinValue;
            lastAudioTranscript = null;
            lastAudioContextText = null;
            audioChunkSequence = 0;
            lastAcceptedAudioSequence = -1;
            lastAudioTranscriptIdentity = null;
            lastAudioTranscriptCapturedAt = DateTime.MinValue;
            queuedAudio.Clear();
            audioCaptureStartedUtc = DateTime.MinValue;
            audioLastTranscriptUtc = DateTime.MinValue;
            audioTranscriptionRunning = false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(targetWindow, out processId);
            bool preferIsolated = processId != 0 &&
                services.CaptionAudioScope == "process" &&
                ProcessLoopbackAudioClient.IsSupported &&
                !IsKnownProcessLoopbackUnsupported(targetWindow);
            string targetLabel = GetTargetProcessLabel(targetWindow);
            audioTargetLabel = targetLabel;
            captionArchive.BeginSession(DateTime.Now, targetLabel);
            trayStatusItem.Text = "状态：正在初始化 " + targetLabel + " 的会议音源…";
            Task.Factory.StartNew(delegate
            {
                services.EnsureRunning();
                return CreateStartedAudioCapture(processId, preferIsolated, generation);
            }).ContinueWith(delegate(Task<Tuple<SystemAudioCaptionCapture, bool>> task)
            {
                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (task.IsFaulted || task.Result == null)
                        {
                            string message = task.IsFaulted
                                ? task.Exception.GetBaseException().Message : "音频设备没有返回结果";
                            ReportAudioFailure(generation, "无法开始会议声音采集：" + message);
                            active = false;
                            return;
                        }
                        SystemAudioCaptionCapture started = task.Result.Item1;
                        if (!active || generation != audioSessionGeneration)
                        {
                            started.Dispose();
                            return;
                        }
                        audioCapture = started;
                        audioCaptureStartedUtc = DateTime.UtcNow;
                        audioHealthTimer.Start();
                        if (task.Result.Item2)
                            ShowNotice("当前会议进程无法单独捕获，已回退为全系统声音。请关闭其他会发声的软件。",
                                ToolTipIcon.Warning);
                        string sourceName = audioCapture.IsProcessIsolated ? "仅会议进程" : "全系统声音";
                        trayStatusItem.Text = "状态：字幕目标 " + targetLabel + " · 音源：" + sourceName;
                        SetCaptionStatus("字幕：正在收音 · 等待发言");
                        services.Log("Audio caption capture started for target process " + targetLabel +
                            "; source=" + sourceName + "; microphone disabled");
                    }));
                }
                catch
                {
                    if (!task.IsFaulted && task.Result != null) task.Result.Item1.Dispose();
                }
            });
        }

        private Tuple<SystemAudioCaptionCapture, bool> CreateStartedAudioCapture(
            uint processId, bool preferIsolated, int generation)
        {
            SystemAudioCaptionCapture capture = new SystemAudioCaptionCapture(
                preferIsolated ? (uint?)processId : null);
            capture.AudioChunkReadyWithContext += delegate(byte[] wav, bool overlap) { QueueAudioTranscription(wav, generation, overlap); };
            capture.Failed += delegate(string error)
            {
                try { dispatcher.BeginInvoke(new Action(delegate { ReportAudioFailure(generation, error); })); }
                catch { }
            };
            try
            {
                capture.Start();
                return Tuple.Create(capture, false);
            }
            catch (Exception isolatedError)
            {
                capture.Dispose();
                if (!preferIsolated) throw;
                services.Log("Process audio isolation unavailable; falling back to system output: " +
                    isolatedError.GetType().Name);
                capture = new SystemAudioCaptionCapture();
                capture.AudioChunkReadyWithContext += delegate(byte[] wav, bool overlap) { QueueAudioTranscription(wav, generation, overlap); };
                capture.Failed += delegate(string error)
                {
                    try { dispatcher.BeginInvoke(new Action(delegate { ReportAudioFailure(generation, error); })); }
                    catch { }
                };
                try
                {
                    capture.Start();
                    return Tuple.Create(capture, true);
                }
                catch
                {
                    capture.Dispose();
                    throw;
                }
            }
        }

        private void QueueAudioTranscription(byte[] wav, int generation, bool hasOverlap)
        {
            DateTime capturedAt = DateTime.Now;
            try
            {
                dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!active || generation != audioSessionGeneration || audioCapture == null) return;
                    CaptionAudioChunk dropped = queuedAudio.Enqueue(wav, capturedAt, hasOverlap, ++audioChunkSequence);
                    if (dropped != null)
                    {
                        dropped.Complete("dropped", services.RecordCaptionMetric);
                        RecordAudioGap(dropped.CapturedAt, "识别服务积压");
                        services.Log("Audio transcription queue full; marked oldest pending chunk as gap");
                        audioCompletions.Add(dropped, delegate { return true; });
                        audioCompletions.Drain(delegate { return active && generation == audioSessionGeneration; });
                    }
                    if (DateTime.UtcNow < audioRetryNotBeforeUtc)
                        audioRetryTimer.Start();
                    else BeginAudioTranscription(generation);
                }));
            }
            catch { }
        }

        private void BeginAudioTranscription(int generation)
        {
            if (!active || generation != audioSessionGeneration || DateTime.UtcNow < audioRetryNotBeforeUtc)
                return;
            audioRetryTimer.Stop();
            while (queuedAudio.HasWork && CanStartCaptionRequest(audioTranscriptionsInFlight, audioCompletions.Count))
            {
                CaptionAudioChunk chunk = queuedAudio.TakeNext();
                if (chunk == null) break;
                LaunchAudioTranscription(chunk, generation);
            }
        }

        internal static bool CanStartCaptionRequest(int inFlight, int completed)
        {
            return inFlight >= 0 && completed >= 0 && inFlight < 2 && completed < AudioQueueLimit;
        }

        private void LaunchAudioTranscription(CaptionAudioChunk chunk, int generation)
        {
            audioTranscriptionsInFlight++;
            audioTranscriptionRunning = true;
            SetCaptionStatus(audioDroppedChunks > 0
                ? "字幕：正在转写 · 缺失" + audioDroppedChunks + "段"
                : "字幕：正在转写语音…");
            Task.Factory.StartNew(delegate { return services.TranscribeAudio(chunk.Wav); })
                .ContinueWith(delegate(Task<AudioTranscriptionResponse> task)
                {
                    try
                    {
                        dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (generation != audioSessionGeneration) {
                                chunk.Complete("cancelled", services.RecordCaptionMetric);
                                return;
                            }
                            audioTranscriptionsInFlight--;
                            audioTranscriptionRunning = audioTranscriptionsInFlight > 0;
                            audioCompletions.Add(chunk, delegate {
                                return CompleteAudioTranscription(chunk, task, generation);
                            });
                            audioCompletions.Drain(delegate { return active && generation == audioSessionGeneration; });
                            BeginAudioTranscription(generation);
                        }));
                    }
                    catch { chunk.Complete("cancelled", services.RecordCaptionMetric); }
                });
        }

        private bool CompleteAudioTranscription(CaptionAudioChunk chunk,
            Task<AudioTranscriptionResponse> task, int generation)
        {
            if (task.IsFaulted || task.Result == null || !task.Result.ok)
            {
                Exception failure = task.IsFaulted ? task.Exception.GetBaseException() : null;
                string message = failure != null ? failure.Message :
                    (task.Result == null ? "语音识别没有返回结果" : task.Result.error);
                bool retryable = failure is TimeoutException ||
                    failure is System.Net.WebException || failure is IOException ||
                    (!task.IsFaulted && task.Result != null && task.Result.retryable);
                if (!retryable)
                {
                    chunk.Complete("gap", services.RecordCaptionMetric);
                    RecordAudioGap(chunk.CapturedAt, "识别服务已停止");
                    StopAudioAfterFailure(generation, message);
                    return true;
                }
                audioTransientFailures++;
                bool retryingSameChunk = queuedAudio.HoldForRetry(chunk);
                if (!retryingSameChunk)
                {
                    chunk.Complete("gap", services.RecordCaptionMetric);
                    RecordAudioGap(chunk.CapturedAt, "重试后仍无法转录");
                }
                int delaySeconds = Math.Min(12, 2 * audioTransientFailures);
                audioRetryNotBeforeUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                audioRetryTimer.Start();
                trayStatusItem.Text = "状态：" + message + "，" +
                    delaySeconds + " 秒后" + (retryingSameChunk ? "重试本段" : "继续后续") +
                    "；待转录 " + queuedAudio.PendingCount + " 段";
                SetCaptionStatus(retryingSameChunk ? "字幕：重试这段声音…" :
                    "字幕：缺失" + audioDroppedChunks + "段");
                services.Log("Transient audio transcription failure; retry_same_chunk=" +
                    retryingSameChunk + "; cooldown seconds=" + delaySeconds);
                return !retryingSameChunk;
            }
            else if (!String.IsNullOrWhiteSpace(task.Result.text))
            {
                audioTransientFailures = 0;
                audioRetryNotBeforeUtc = DateTime.MinValue;
                bool accepted = ApplyAudioTranscript(task.Result.text.Trim(), generation,
                    chunk.CapturedAt, task.Result.raw_text,
                    task.Result.term_corrections, chunk.HasOverlap, chunk.Sequence);
                chunk.Complete(accepted ? "transcript" : "empty", services.RecordCaptionMetric);
                SetCaptionStatus(accepted ? "字幕：已更新 · 分段后 " +
                    (chunk.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒" +
                    (queuedAudio.PendingCount > 0 ? " · 待处理 " + queuedAudio.PendingCount + " 段" : "") +
                    (audioDroppedChunks > 0 ? " · 缺失 " + audioDroppedChunks + " 段" : "") :
                    "字幕：本段没有可用文字 · 继续收音");
            }
            else if (task.Result != null && task.Result.ok)
            {
                chunk.Complete("empty", services.RecordCaptionMetric);
                audioTransientFailures = 0;
                audioRetryNotBeforeUtc = DateTime.MinValue;
                SetCaptionStatus("字幕：正在收音 · 等待发言");
            }
            return true;
        }

        private void ResumeAudioAfterCooldown(object sender, EventArgs args)
        {
            if (!active || audioCapture == null || services.WorkMode != "caption")
            {
                audioRetryTimer.Stop();
                return;
            }
            if (DateTime.UtcNow < audioRetryNotBeforeUtc) return;
            audioRetryTimer.Stop();
            if (queuedAudio.HasWork)
                BeginAudioTranscription(audioSessionGeneration);
            else
                SetCaptionStatus("字幕：正在收音 · 等待发言");
        }

        private bool ApplyAudioTranscript(string text, int generation, DateTime capturedAt,
            string rawText, int termCorrections, bool hasOverlap, int sequence)
        {
            if (!active || generation != audioSessionGeneration) return false;
            string contextText = (text ?? String.Empty).Trim();
            text = TrimAudioOverlap(lastAudioContextText, contextText,
                CanMergeAudioChunk(hasOverlap, sequence, lastAcceptedAudioSequence));
            string identity = NormalizeTranscriptIdentity(text);
            if (identity.Length == 0)
            {
                services.Log("Discarded empty or formatting-only audio transcript");
                return false;
            }
            if (IsRepeatedAudioResult(identity, capturedAt, lastAudioTranscriptIdentity, lastAudioTranscriptCapturedAt))
            {
                services.Log("Discarded duplicate audio transcript");
                return false;
            }
            lastAudioContextText = contextText;
            lastAcceptedAudioSequence = sequence;
            lastAudioTranscript = text;
            lastAudioTranscriptIdentity = identity;
            lastAudioTranscriptCapturedAt = capturedAt;
            pendingCaptionText = text;
            pendingCaptionCommitted = true;
            audioLastTranscriptUtc = DateTime.UtcNow;
            rawText = (rawText ?? String.Empty).Trim();
            if (rawText.Length > 500 || String.Equals(rawText, text, StringComparison.Ordinal))
                rawText = String.Empty;
            CaptionEntry entry = new CaptionEntry {
                timestamp = capturedAt, text = text,
                raw_text = rawText
            };
            bool saved = SaveCaptionEntry(entry, false);
            captionHistory.Add(entry);
            captionHistory.Sort(delegate(CaptionEntry left, CaptionEntry right) {
                return left.timestamp.CompareTo(right.timestamp);
            });
            if (captionHistory.Count > CaptionHistoryLimit) captionHistory.RemoveAt(0);
            captionHistoryWindow.AppendLiveEntry(entry, captionArchive.CurrentDate, saved);
            foreach (CaptionSpeechSegment segment in SplitCaptionSpeech(text))
                audioDisplayQueue.Enqueue(segment);
            if (!audioDisplayTimer.Enabled)
                AdvanceAudioDisplay(null, EventArgs.Empty);
            trayStatusItem.Text = "状态：" +
                (audioCapture != null && audioCapture.IsProcessIsolated ? "会议进程" : "全系统声音") +
                "字幕已更新";
            SetCaptionStatus(termCorrections > 0 && rawText.Length > 0
                ? "字幕：已更新 · 术语校正" + termCorrections + "处"
                : audioDroppedChunks > 0
                    ? "字幕：已更新，缺失" + audioDroppedChunks + "段"
                    : "字幕：已更新 · 继续收音");
            services.Log("Accepted audio transcript with " + text.Length + " characters");
            StartAudioTextAnalysis(text, generation, ++audioTextGeneration);
            return true;
        }

        internal static bool CanMergeAudioChunk(bool hasOverlap, int sequence, int previousSequence)
        {
            return hasOverlap && previousSequence > 0 && sequence == previousSequence + 1;
        }

        internal static string TrimAudioOverlap(string previous, string current, bool sharedAudio)
        {
            current = (current ?? String.Empty).Trim();
            if (!sharedAudio || String.IsNullOrWhiteSpace(previous)) return current;
            const string tokenPattern = @"[\u4e00-\u9fff]|[A-Za-z]+(?:['’][A-Za-z]+)*|[0-9]+";
            MatchCollection before = Regex.Matches(previous, tokenPattern);
            MatchCollection after = Regex.Matches(current, tokenPattern);
            for (int count = Math.Min(16, Math.Min(before.Count, after.Count)); count >= 1; count--)
            {
                bool equal = true, chineseOnly = true;
                for (int index = 0; index < count; index++)
                {
                    string left = before[before.Count - count + index].Value.Replace('’', '\'');
                    string right = after[index].Value.Replace('’', '\'');
                    if (!String.Equals(left, right, StringComparison.OrdinalIgnoreCase)) { equal = false; break; }
                    if (right.Length != 1 || right[0] < '\u4e00' || right[0] > '\u9fff') chineseOnly = false;
                }
                if (!equal || (chineseOnly && count < 4) ||
                    (count == 1 && after[0].Length < 5)) continue;
                int end = after[count - 1].Index + after[count - 1].Length;
                while (end < current.Length && (Char.IsWhiteSpace(current[end]) || Char.IsPunctuation(current[end]))) end++;
                return current.Substring(end).Trim();
            }
            return current;
        }

        private void RecordAudioGap(DateTime capturedAt, string reason)
        {
            audioDroppedChunks++;
            CaptionEntry gap = new CaptionEntry {
                timestamp = capturedAt,
                text = "这段声音未能转录（" + reason + "）",
                is_gap = true
            };
            bool saved = SaveCaptionEntry(gap, false);
            captionHistory.Add(gap);
            captionHistory.Sort(delegate(CaptionEntry left, CaptionEntry right) {
                return left.timestamp.CompareTo(right.timestamp);
            });
            if (captionHistory.Count > CaptionHistoryLimit) captionHistory.RemoveAt(0);
            captionHistoryWindow.AppendLiveEntry(gap, captionArchive.CurrentDate, saved);
            trayStatusItem.Text = "状态：字幕已有 " + audioDroppedChunks + " 处明确缺口";
            SetCaptionStatus("字幕：缺失" + audioDroppedChunks + "段");
        }

        private bool SaveCaptionEntry(CaptionEntry entry, bool replaceLast)
        {
            if (!services.CaptionArchiveEnabled) return false;
            if (captionArchive.Append(entry, replaceLast)) return true;
            if (archiveFailureShown) return false;
            archiveFailureShown = true;
            services.Log("Caption archive write failed; transcript remains in session memory");
            ShowNotice("字幕仍在本次会话中，但保存到本地历史失败。请检查磁盘空间。",
                ToolTipIcon.Warning);
            return false;
        }

        internal static string NormalizeTranscriptIdentity(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            StringBuilder builder = new StringBuilder(text.Length);
            foreach (char value in text)
                if (Char.IsLetterOrDigit(value)) builder.Append(Char.ToUpperInvariant(value));
            return builder.ToString();
        }

        internal static bool IsRepeatedAudioResult(string identity, DateTime capturedAt,
            string previousIdentity, DateTime previousCapturedAt)
        {
            // Equal speech in a different audio chunk is a real repeated utterance.
            return capturedAt == previousCapturedAt &&
                String.Equals(identity, previousIdentity, StringComparison.Ordinal);
        }

        internal static List<CaptionSpeechSegment> SplitCaptionSpeech(string text)
        {
            var result = new List<CaptionSpeechSegment>();
            if (String.IsNullOrWhiteSpace(text)) return result;
            int start = 0;
            for (int index = 0; index < text.Length; index++)
            {
                char value = text[index];
                bool boundary = value == '。' || value == '！' || value == '？' ||
                    ((value == '.' || value == '!' || value == '?') &&
                     (index + 1 == text.Length || Char.IsWhiteSpace(text[index + 1])));
                if (!boundary) continue;
                AddCaptionSpeechSegment(result, text, start, index + 1);
                start = index + 1;
            }
            AddCaptionSpeechSegment(result, text, start, text.Length);
            return result;
        }

        private static void AddCaptionSpeechSegment(
            List<CaptionSpeechSegment> result, string source, int start, int end)
        {
            while (start < end && Char.IsWhiteSpace(source[start])) start++;
            while (end > start && Char.IsWhiteSpace(source[end - 1])) end--;
            if (end > start)
                result.Add(new CaptionSpeechSegment { Text = source.Substring(start, end - start), Offset = start });
        }

        private void AdvanceAudioDisplay(object sender, EventArgs args)
        {
            if (!active || services.WorkMode != "caption" || audioDisplayQueue.Count == 0)
            {
                audioDisplayTimer.Stop();
                return;
            }
            CaptionSpeechSegment segment = audioDisplayQueue.Dequeue();
            audioDisplayLines.Add(segment);
            if (audioDisplayLines.Count > 2) audioDisplayLines.RemoveAt(0);
            bool targetForeground = NativeMethods.GetForegroundWindow() == targetWindow;
            if (targetForeground)
                ShowAudioDisplay();
            services.Log("Audio display: foreground_match=" + targetForeground +
                " visible=" + captionLyricWindow.Visible +
                " bounds=" + captionLyricWindow.Bounds);
            if (audioDisplayQueue.Count > 0) audioDisplayTimer.Start();
            else audioDisplayTimer.Stop();
        }

        private void ShowAudioDisplay()
        {
            if (audioDisplayLines.Count == 0) return;
            CaptionSpeechSegment current = audioDisplayLines[audioDisplayLines.Count - 1];
            string previous = audioDisplayLines.Count > 1
                ? audioDisplayLines[audioDisplayLines.Count - 2].Text : String.Empty;
            captionLyricWindow.ShowAudioLines(previous, current.Text, current.Offset, targetRect);
        }

        private void StartAudioTextAnalysis(string text, int sessionGeneration, int textGeneration)
        {
            relativeHighlights.Clear();
            HideHighlights();
            Task.Factory.StartNew(delegate { return services.AnalyzeHistoricalCaption(text); })
                .ContinueWith(delegate(Task<AnalyzeResponse> task)
                {
                    try
                    {
                        dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (!active || sessionGeneration != audioSessionGeneration ||
                                textGeneration != audioTextGeneration || task.IsFaulted || task.Result == null) return;
                            AddAudioRanges(task.Result.entities, text, false);
                            AddAudioRanges(task.Result.actions, text, true);
                            highlightsCurrent = relativeHighlights.Count > 0;
                            RenderHighlights();
                            services.Log("Rendered " + relativeHighlights.Count + " audio-caption term windows");
                        }));
                    }
                    catch { }
                });
        }

        private void AddAudioRanges(List<AnalysisEntity> entities, string context, bool action)
        {
            if (entities == null) return;
            foreach (AnalysisEntity entity in entities)
            {
                if (entity == null || entity.start < 0 || entity.end <= entity.start ||
                    entity.end > context.Length || context.Substring(entity.start, entity.end - entity.start) != entity.text) continue;
                Rectangle screen;
                if (!captionLyricWindow.TryGetCurrentRange(entity.start, entity.end - entity.start, out screen)) continue;
                relativeHighlights.Add(new HighlightItem {
                    term = entity.text, context = context, kind = action ? "calendar" : "concept",
                    title = entity.title, time_text = entity.time_text, start_iso = entity.start_iso,
                    needs_confirmation = action || entity.needs_confirmation,
                    x = screen.Left - targetRect.Left, y = screen.Top - targetRect.Top,
                    w = screen.Width, h = screen.Height
                });
            }
        }

        private void ReportAudioFailure(int generation, string message)
        {
            StopAudioAfterFailure(generation, message);
        }

        private void StopAudioAfterFailure(int generation, string message)
        {
            if (generation != audioSessionGeneration) return;
            StopAudioCaptionSession();
            active = false;
            captionLyricWindow.Hide();
            HideHighlights();
            trayStatusItem.Text = "状态：语音字幕已停止 — " + message;
            captionStatusText = String.Empty;
            if (NativeMethods.GetForegroundWindow() == targetWindow)
                captionStatusWindow.ShowTemporary("字幕：已停止，请看托盘", targetRect, 5000);
            else
                captionStatusWindow.Hide();
            services.Log("Audio caption failure: " + message);
            if (!audioFailureShown)
            {
                audioFailureShown = true;
                ShowNotice("语音字幕已停止：" + message, ToolTipIcon.Error);
            }
        }

        private void StopAudioCaptionSession()
        {
            audioSessionGeneration++;
            audioRetryTimer.Stop();
            audioHealthTimer.Stop();
            audioDisplayTimer.Stop();
            audioDisplayQueue.Clear();
            audioDisplayLines.Clear();
            queuedAudio.Clear(services.RecordCaptionMetric);
            audioCompletions.Clear(services.RecordCaptionMetric);
            audioTranscriptionsInFlight = 0;
            audioTranscriptionRunning = false;
            audioTextGeneration++;
            if (audioCapture != null)
            {
                audioCapture.Dispose();
                audioCapture = null;
                services.Log("System audio caption capture stopped");
            }
            lastAudioTranscript = null;
            lastAudioContextText = null;
            lastAcceptedAudioSequence = -1;
            lastAudioTranscriptIdentity = null;
            audioCaptureStartedUtc = DateTime.MinValue;
            audioLastTranscriptUtc = DateTime.MinValue;
        }

        private void SetCaptionStatus(string message)
        {
            if (String.Equals(captionStatusText, message, StringComparison.Ordinal)) return;
            captionStatusText = message ?? String.Empty;
            if (active && targetWindow != IntPtr.Zero &&
                NativeMethods.GetForegroundWindow() == targetWindow)
                captionStatusWindow.ShowState(captionStatusText, targetRect);
        }

        private void RefreshAudioHealth(object sender, EventArgs args)
        {
            if (!active || audioCapture == null || services.WorkMode != "caption")
            {
                audioHealthTimer.Stop();
                return;
            }
            DateTime now = DateTime.UtcNow;
            DateTime packet = audioCapture.LastPacketUtc;
            DateTime voice = audioCapture.LastVoiceUtc;
            string sourceName = audioCapture.IsProcessIsolated ? "仅会议进程" : "全系统声音";
            bool noPackets = packet == DateTime.MinValue || (now - packet).TotalSeconds >= 10;
            bool noVoice = voice == DateTime.MinValue || (now - voice).TotalSeconds >= 10;
            int level = packet != DateTime.MinValue && (now - packet).TotalSeconds < 2
                ? audioCapture.RecentLevelBars : 0;
            string lastResult = audioLastTranscriptUtc == DateTime.MinValue ? "暂无" :
                Math.Max(0, (int)(now - audioLastTranscriptUtc).TotalSeconds) + "秒前";
            string activity = now < audioRetryNotBeforeUtc ? " · 等待重试" :
                (audioTranscriptionRunning ? " · 正在转写" : String.Empty);
            trayStatusItem.Text = "状态：目标 " + audioTargetLabel + " · 音源 " + sourceName +
                " · 声级 " + level + "/4 · 最近字幕 " + lastResult +
                activity + (audioDroppedChunks > 0 ? " · 缺失" + audioDroppedChunks + "段" : String.Empty);
            if (audioTranscriptionRunning || now < audioRetryNotBeforeUtc) return;
            if (audioCaptureStartedUtc != DateTime.MinValue &&
                (now - audioCaptureStartedUtc).TotalSeconds >= 10 && noPackets)
                SetCaptionStatus("字幕：音源无数据");
            else if (audioCaptureStartedUtc != DateTime.MinValue &&
                     (now - audioCaptureStartedUtc).TotalSeconds >= 10 && noVoice)
                SetCaptionStatus("字幕：当前音源静音");
            else if (!noVoice)
                SetCaptionStatus("字幕：有声，等待出字");
        }

    }
}
