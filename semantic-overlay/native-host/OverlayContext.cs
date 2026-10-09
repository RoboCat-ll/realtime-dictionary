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
        private const int HotkeyHighlight = 1;
        private const int HotkeyClear = 2;
        private const int HotkeyLookup = 3;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkK = 0x4B;
        private const uint VkG = 0x47;
        private const int ContentRefreshDelay = 220;
        private const int CaptionProbeInterval = 700;
        private const int CaptionModelInterval = 4000;
        private const int CaptionStabilityDelay = 900;
        private const int CaptionHistoryLimit = 5000;
        private const int AudioQueueLimit = 3;

        private readonly MessageForm dispatcher;
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem trayStatusItem = new ToolStripMenuItem("状态：正在启动…");
        private readonly ToolStripMenuItem trayUsageItem = new ToolStripMenuItem("模型分析：读取中…");
        private readonly System.Windows.Forms.Timer trackingTimer;
        private readonly List<HighlightForm> windows = new List<HighlightForm>();
        private readonly List<HighlightItem> relativeHighlights = new List<HighlightItem>();
        private readonly StatusForm statusWindow;
        private readonly StatusForm messageHintWindow;
        private IntPtr messageHintTarget;
        private int messageHintGeneration;
        private readonly CaptionStatusForm captionStatusWindow;
        private readonly DefinitionForm definitionWindow;
        private readonly AssistantPanelForm assistantPanel;
        private readonly HighlightForm assistantLookupAnchor = new HighlightForm();
        private readonly CaptionLyricForm captionLyricWindow;
        private readonly System.Windows.Forms.Timer audioDisplayTimer;
        private readonly System.Windows.Forms.Timer audioRetryTimer;
        private readonly Queue<CaptionSpeechSegment> audioDisplayQueue = new Queue<CaptionSpeechSegment>();
        private readonly List<CaptionSpeechSegment> audioDisplayLines = new List<CaptionSpeechSegment>();
        private readonly CaptionHistoryForm captionHistoryWindow;
        private readonly CaptionHistoryArchive captionArchive = new CaptionHistoryArchive(
            CaptionHistoryArchive.DefaultDirectory);
        private readonly System.Windows.Forms.Timer historyOpenTimer;
        private bool archiveFailureShown;
        private readonly LocalReminderManager reminders;
        private readonly ToolStripMenuItem reminderMenuItem = new ToolStripMenuItem();
        private readonly ServiceManager services;
        private readonly SelectionActionForm selectionAction = new SelectionActionForm();
        private SelectionAnalysisForm selectionAnalysis;
        private Point? selectionDragStart;
        private IntPtr selectionTarget;
        private int selectionGeneration;
        private int selectionProbeBusy;
        private const int MessageClickArmSeconds = 10;
        private DateTime messageClickArmedUntilUtc = DateTime.MinValue;
        private Func<List<OcrWord>, ScanResponse> refineWords;
        private bool choosingScanRegion;
        private IntPtr customRegionWindow;
        private RectangleF customRegion;
        private readonly object refreshLock = new object();
        private readonly List<LookupView> lookupHistory = new List<LookupView>();
        private readonly List<CaptionEntry> captionHistory = new List<CaptionEntry>();
        private readonly NativeMethods.WinEventDelegate winEventDelegate;
        private readonly NativeMethods.MouseHookDelegate mouseHookDelegate;

        private IntPtr winEventHook;
        private IntPtr mouseHook;
        private IntPtr targetWindow;
        private NativeRect targetRect;
        private DateTime refreshDueUtc = DateTime.MaxValue;
        private DateTime nextCaptionProbeUtc = DateTime.MaxValue;
        private DateTime lastCaptionModelUtc = DateTime.MinValue;
        private bool active;
        private bool scanRunning;
        private bool highlightsVisible;
        private bool highlightsCurrent;
        private bool refinementRunning;
        private int runningRefinementGeneration;
        private int queuedRefinementGeneration;
        private IntPtr queuedRefinementWindow;
        private NativeRect queuedRefinementRect;
        private List<OcrWord> queuedRefinementWords;
        private ScanResponse queuedRefinementFallback;
        private int queuedRefinementOffsetX;
        private int queuedRefinementOffsetY;
        private int scanGeneration;
        private int progressiveHighlightGeneration;
        private readonly Dictionary<int, ScanTiming> scanTimings =
            new Dictionary<int, ScanTiming>();
        private int lookupGeneration;
        private int geometryUpdateQueued;
        private int contentRefreshQueued;
        private int definiteContentMovementQueued;
        private IntPtr lastCaptureWindow;
        private byte[] lastCaptureFingerprint;
        private byte[] displayedFingerprint;
        private ScrollFrame scrollFrame;
        private DateTime scrollUntilUtc;
        private DateTime scrollProbeUtc;
        private int scrollProbeRunning;
        private int scrollTrackingGeneration;
        private int scrollTrackingFailures;
        private bool preserveTrackedHighlights;
        private Rectangle? trackingViewport;
        private HighlightForm selectedHighlight;
        private LookupView currentLookup;
        private string pendingCaptionText;
        private bool pendingCaptionCommitted;
        private DateTime pendingCaptionChangedUtc = DateTime.MinValue;
        private List<OcrWord> pendingCaptionWords;
        private int pendingCaptionGeneration;
        private IntPtr pendingCaptionWindow;
        private NativeRect pendingCaptionCaptureRect;
        private int pendingCaptionOffsetX;
        private int pendingCaptionOffsetY;
        private int lastCaptionRefinedGeneration;
        private SystemAudioCaptionCapture audioCapture;
        private int audioSessionGeneration;
        private bool audioTranscriptionRunning;
        private int audioTranscriptionsInFlight;
        private readonly CaptionOrderedCompletions audioCompletions = new CaptionOrderedCompletions();
        private readonly CaptionAudioBacklog queuedAudio = new CaptionAudioBacklog(AudioQueueLimit);
        private readonly System.Windows.Forms.Timer audioHealthTimer;
        private DateTime audioCaptureStartedUtc = DateTime.MinValue;
        private DateTime audioLastTranscriptUtc = DateTime.MinValue;
        private string audioTargetLabel = "会议窗口";
        private string lastAudioTranscript;
        private string lastAudioTranscriptIdentity;
        private DateTime lastAudioTranscriptCapturedAt;
        private bool audioFailureShown;
        private int audioTransientFailures;
        private int audioDroppedChunks;
        private DateTime audioRetryNotBeforeUtc = DateTime.MinValue;
        private int audioTextGeneration;
        private string captionStatusText = String.Empty;

        public OverlayContext()
        {
            services = new ServiceManager();
            DateTime lastPreferenceWarning = DateTime.MinValue;
            services.PreferenceSaveFailed = delegate {
                try { dispatcher.BeginInvoke(new Action(delegate {
                    if ((DateTime.UtcNow - lastPreferenceWarning).TotalSeconds < 5) return;
                    lastPreferenceWarning = DateTime.UtcNow;
                    ShowNotice("设置未能保存，仅本次运行生效。请检查配置目录后重试。", ToolTipIcon.Warning);
                })); } catch { }
            };
            services.CloudConsentRequested = delegate(string provider) {
                Func<bool> ask = delegate {
                    return MessageBox.Show("将把本次选中的消息或词语及上下文发送到 " + provider +
                        " 生成解释。\r\n消息解释只发送所选内容；兼容扫描发送所选区域文字。请求正文不写入日志。\r\n" +
                        "请勿提交公司保密内容。同一服务商确认一次，切换后重新询问。",
                        "文字发送告知", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
                };
                return dispatcher.InvokeRequired ? (bool)dispatcher.Invoke(ask) : ask();
            };
            refineWords = services.RefineWords;
            reminders = new LocalReminderManager();
            statusWindow = new StatusForm();
            messageHintWindow = new StatusForm(true);
            captionStatusWindow = new CaptionStatusForm();
            definitionWindow = new DefinitionForm();
            assistantPanel = new AssistantPanelForm();
            assistantPanel.ItemClicked += BeginAssistantItemAction;
            captionLyricWindow = new CaptionLyricForm();
            captionLyricWindow.HistoryRequested += QueueShowCaptionHistory;
            audioDisplayTimer = new System.Windows.Forms.Timer { Interval = 320 };
            audioDisplayTimer.Tick += AdvanceAudioDisplay;
            audioRetryTimer = new System.Windows.Forms.Timer { Interval = 500 };
            audioRetryTimer.Tick += ResumeAudioAfterCooldown;
            audioHealthTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            audioHealthTimer.Tick += RefreshAudioHealth;
            captionHistoryWindow = new CaptionHistoryForm();
            captionHistoryWindow.ArchiveDateRequested = captionArchive.LoadDate;
            captionHistoryWindow.ArchiveStatusRequested = captionArchive.LoadDateWithStatus;
            captionHistoryWindow.ArchivePageRequested = captionArchive.LoadPage;
            captionHistoryWindow.DeleteDateRequested = captionArchive.DeleteDate;
            captionHistoryWindow.SessionEntriesRequested = delegate { return new List<CaptionEntry>(captionHistory); };
            captionHistoryWindow.VisibleChanged += delegate {
                services.Log("Caption history visible=" + captionHistoryWindow.Visible);
            };
            historyOpenTimer = new System.Windows.Forms.Timer { Interval = 120 };
            historyOpenTimer.Tick += delegate {
                historyOpenTimer.Stop();
                ShowCaptionHistory();
            };
            captionHistoryWindow.Lookup = delegate(string term, string context)
            {
                services.EnsureRunning();
                return services.Lookup(term, context, false, null);
            };
            captionHistoryWindow.Translate = services.TranslateCaption;
            captionHistoryWindow.ClearRequested += ClearCaptionHistory;
            captionHistoryWindow.Analyze = services.AnalyzeHistoricalCaption;
            captionHistoryWindow.EditTaskRequested += delegate(HighlightItem candidate)
            {
                OpenReminderEditor(candidate, captionHistoryWindow);
            };
            definitionWindow.Dismissed += HideDefinition;
            definitionWindow.TermClicked += BeginNestedLookup;
            definitionWindow.BackRequested += NavigateBack;
            definitionWindow.EditTaskRequested += delegate(HighlightItem candidate)
            {
                OpenReminderEditor(candidate, null);
            };
            definitionWindow.RetryRequested += RetryCurrentLookup;
            definitionWindow.FeedbackSubmitted += OnDefinitionFeedback;
            dispatcher = new MessageForm();
            dispatcher.HotkeyPressed += OnHotkey;

            bool kRegistered = NativeMethods.RegisterHotKey(
                dispatcher.Handle, HotkeyHighlight, ModControl | ModAlt | ModNoRepeat, VkK);
            int kRegistrationError = kRegistered ? 0 : Marshal.GetLastWin32Error();
            bool gRegistered = NativeMethods.RegisterHotKey(
                dispatcher.Handle, HotkeyClear, ModControl | ModAlt | ModNoRepeat, VkG);
            int gRegistrationError = gRegistered ? 0 : Marshal.GetLastWin32Error();
            services.Log("Ctrl+Alt+K registration: " +
                (kRegistered ? "ok" : "failed (Win32 error " + kRegistrationError + ")"));
            services.Log("Ctrl+Alt+G registration: " +
                (gRegistered ? "ok" : "failed (Win32 error " + gRegistrationError + ")"));
            bool dRegistered = NativeMethods.RegisterHotKey(
                dispatcher.Handle, HotkeyLookup, ModControl | ModAlt | ModNoRepeat, 0x44);
            services.Log("Ctrl+Alt+D registration: " + (dRegistered ? "ok" : "failed"));

            tray = new NotifyIcon();
            InitializeTrayMenu();

            trackingTimer = new System.Windows.Forms.Timer();
            trackingTimer.Interval = 16;
            trackingTimer.Tick += TrackTarget;
            trackingTimer.Start();

            winEventDelegate = OnWinEvent;
            winEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EventObjectShow,
                NativeMethods.EventObjectValueChange,
                IntPtr.Zero,
                winEventDelegate,
                0,
                0,
                NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);

            mouseHookDelegate = OnMouseHook;
            mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhMouseLl, mouseHookDelegate, IntPtr.Zero, 0);

            RefreshModelStatus();
            if (!kRegistered)
                ShowNotice("Ctrl+Alt+K 已被其他程序占用，请先关闭冲突程序。", ToolTipIcon.Error);
            if (!dRegistered)
                ShowNotice("Ctrl+Alt+D 已被占用；仍可从托盘打开主动查词。", ToolTipIcon.Warning);
        }

        private void OnHotkey(int id)
        {
            if (id == HotkeyHighlight)
            {
                if (services.WorkMode == "caption" && services.ExperimentalFeaturesEnabled)
                {
                    IntPtr foreground = NativeMethods.GetForegroundWindow();
                    if (ShouldReuseCaptionSession(active, targetWindow, foreground))
                    {
                        captionStatusWindow.ShowState(
                            String.IsNullOrEmpty(captionStatusText)
                                ? "字幕：正在启动…" : captionStatusText, targetRect);
                        services.Log("Caption hotkey reused active session for same target");
                    }
                    else BeginSession();
                }
                else
                    ArmMessageClick();
            }
            else if (id == HotkeyClear)
                DisableSession();
            else if (id == HotkeyLookup)
            {
                services.Log("Ctrl+Alt+D received");
                OpenManualLookup(true);
            }
        }

        internal static bool ShouldReuseCaptionSession(bool sessionActive,
            IntPtr boundWindow, IntPtr foreground)
        {
            return sessionActive && boundWindow != IntPtr.Zero && boundWindow == foreground;
        }

        internal static bool IsMessageClickArmed(DateTime nowUtc, DateTime armedUntilUtc)
        {
            return armedUntilUtc != DateTime.MinValue && nowUtc <= armedUntilUtc;
        }

        private bool MessageClickArmed
        {
            get { return services.SelectionToolbarEnabled &&
                IsMessageClickArmed(DateTime.UtcNow, messageClickArmedUntilUtc); }
        }

        private void ArmMessageClick()
        {
            DismissSelectionAction();
            if (!services.SelectionToolbarEnabled)
            {
                ShowNotice("消息解释当前已关闭；请从托盘重新开启。", ToolTipIcon.Warning);
                return;
            }
            messageClickArmedUntilUtc = DateTime.UtcNow.AddSeconds(MessageClickArmSeconds);
            trayStatusItem.Text = "状态：等待点击消息（10 秒）";
            services.Log("One-click message armed for 10 seconds");
            ShowMessageHint("点击消息 · 10 秒", NativeMethods.GetForegroundWindow(),
                MessageClickArmSeconds * 1000, selectionGeneration);
        }

        private void ShowMessageHint(string text, IntPtr target, int milliseconds, int generation)
        {
            if (messageHintWindow == null || target == IntPtr.Zero) return;
            NativeRect bounds;
            if (!NativeMethods.GetWindowRect(target, out bounds)) return;
            messageHintTarget = target;
            messageHintGeneration = generation;
            messageHintWindow.ShowMessage(text, bounds, milliseconds);
        }

        private void HideMessageHint(int generation)
        {
            if (messageHintWindow == null || generation != messageHintGeneration) return;
            messageHintWindow.Hide();
            messageHintTarget = IntPtr.Zero;
        }

        private void DisarmMessageClick(string reason)
        {
            if (messageClickArmedUntilUtc == DateTime.MinValue) return;
            messageClickArmedUntilUtc = DateTime.MinValue;
            if (messageHintWindow != null) messageHintWindow.Hide();
            messageHintTarget = IntPtr.Zero;
            services.Log("One-click message disarmed: " + reason);
        }

        private ManualLookupForm manualLookup;
        private void OpenManualLookup(bool readSelection)
        {
            string sourceApp = ClassifyChatApp(NativeMethods.GetForegroundWindow());
            if (manualLookup != null && !manualLookup.IsDisposed)
            {
                // The existing form may be behind the source application and may
                // still contain the previous query. A new explicit invocation must
                // reread the current selection instead of merely activating stale UI.
                manualLookup.Open(readSelection, sourceApp);
                return;
            }
            manualLookup = new ManualLookupForm(services);
            manualLookup.Open(readSelection, sourceApp);
        }

        private void BeginSession()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || IsOwnWindow(foreground))
                return;

            NativeRect rect;
            if (!TryGetUsableRect(foreground, out rect))
                return;

            if (services.WorkMode == "caption" && (services.CaptionPromptEnabled || !services.CaptionDisclosureAccepted))
            {
                using (var consent = new CaptionConsentForm())
                {
                    if (consent.ShowDialog() != DialogResult.OK) return;
                    services.AcceptCaptionDisclosure();
                    if (consent.RememberApproval) services.SetCaptionPromptEnabled(false);
                }
            }

            targetWindow = foreground;
            services.BeginAnalysisContext();
            scrollTrackingGeneration++;
            scrollTrackingFailures = 0;
            if (lastCaptureWindow != foreground)
            {
                lastCaptureWindow = foreground;
                lastCaptureFingerprint = null;
            }
            targetRect = rect;
            active = true;
            assistantPanel.StartForTarget(targetRect);

            highlightsCurrent = false;
            nextCaptionProbeUtc = DateTime.MaxValue;
            lastCaptionModelUtc = DateTime.MinValue;
            lastCaptionRefinedGeneration = 0;
            relativeHighlights.Clear();
            pendingCaptionText = null;
            pendingCaptionCommitted = false;
            pendingCaptionWords = null;
            captionLyricWindow.SetSourceTop(-1);
            audioDisplayTimer.Stop();
            audioDisplayQueue.Clear();
            audioDisplayLines.Clear();
            HideHighlights();
            captionLyricWindow.Hide();
            HideDefinition();
            services.Log("Highlight session started for HWND " + foreground.ToInt64());
            trayStatusItem.Text = services.WorkMode == "caption"
                ? "状态：正在启动系统声音字幕"
                : "状态：正在识别当前对话窗口";
            if (services.WorkMode == "caption")
            {
                SetCaptionStatus("字幕：正在连接音源…");
                StartAudioCaptionSession();
                return;
            }
            if (scanRunning)
            {
                statusWindow.ShowScanning(targetRect);
                // Invalidate the in-flight result and leave an immediate refresh
                // queued for the most recently selected target.
                scanGeneration++;
                ScheduleRefresh(0);
                services.Log("Scan already running; queued refresh for latest target");
            }
            else
            {
                StartScan();
            }
        }

        private void DisableSession()
        {
            DisarmMessageClick("session cleared");
            DismissSelectionAction();
            services.EndAnalysisContext();
            scrollTrackingGeneration++;
            scrollTrackingFailures = 0;
            scrollFrame = null;
            scrollUntilUtc = DateTime.MinValue;
            preserveTrackedHighlights = false;
            trackingViewport = null;
            StopAudioCaptionSession();

            active = false;
            nextCaptionProbeUtc = DateTime.MaxValue;
            highlightsCurrent = false;
            targetWindow = IntPtr.Zero;
            scanGeneration++;
            queuedRefinementWords = null;
            queuedRefinementFallback = null;
            lastCaptureFingerprint = null;
            relativeHighlights.Clear();
            HideHighlights();
            captionLyricWindow.Hide();
            statusWindow.Hide();
            captionStatusWindow.Hide();
            captionStatusText = String.Empty;
            HideDefinition();
            services.Log("Highlight session disabled");
            trayStatusItem.Text = services.WorkMode == "caption"
                ? "状态：会议字幕模式，未启用"
                : "状态：对话窗口模式，未启用";

        }

        private bool IsOwnWindow(IntPtr hwnd)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            return processId == (uint)Process.GetCurrentProcess().Id;
        }

        internal static string ClassifyChatApp(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "other";
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    return ClassifyChatProcessName(process.ProcessName);
            }
            catch { }
            return "other";
        }

        internal static string ClassifyChatProcessName(string processName)
        {
            string name = (processName ?? String.Empty).ToLowerInvariant();
            if (name.Contains("wechat") || name.Contains("weixin")) return "wechat";
            if (name == "qq" || name.StartsWith("qq")) return "qq";
            return "other";
        }

        private static string GetTargetProcessLabel(IntPtr hwnd)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    return process.ProcessName;
            }
            catch
            {
                return "当前窗口";
            }
        }

        private static bool IsKnownProcessLoopbackUnsupported(IntPtr hwnd)
        {
            string name = GetTargetProcessLabel(hwnd).ToLowerInvariant();
            return name == "ms-teams" || name == "teams";
        }

        private static NativeRect GetPreferredScanRect(
            IntPtr hwnd, NativeRect full, string scanScope, string workMode)
        {
            if (scanScope == "full" || full.Width < 900 || full.Height < 600)
                return full;
            if (workMode == "caption")
            {
                int horizontalInset = Math.Max(20, full.Width * 10 / 100);
                int captionTopInset = full.Height * 52 / 100;
                int captionBottomInset = Math.Max(20, full.Height * 5 / 100);
                NativeRect captions = new NativeRect();
                captions.Left = full.Left + horizontalInset;
                captions.Top = full.Top + captionTopInset;
                captions.Right = full.Right - horizontalInset;
                captions.Bottom = full.Bottom - captionBottomInset;
                return captions.Width > 300 && captions.Height > 120 ? captions : full;
            }
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    if (!IsChatProcessName(process.ProcessName))
                        return full;
                }
            }
            catch
            {
                return full;
            }

            int leftInset = Math.Max(180, full.Width * 21 / 100);
            int topInset = Math.Max(42, full.Height * 4 / 100);
            int rightInset = Math.Max(10, full.Width / 150);
            int bottomInset = Math.Max(95, full.Height * 17 / 100);
            NativeRect content = new NativeRect();
            content.Left = full.Left + leftInset;
            content.Top = full.Top + topInset;
            content.Right = full.Right - rightInset;
            content.Bottom = full.Bottom - bottomInset;
            return content.Width > 300 && content.Height > 200 ? content : full;
        }

        internal static bool IsChatProcessName(string value)
        {
            string name = (value ?? String.Empty).ToLowerInvariant();
            return name == "wechat" || name == "weixin" || name == "qq" ||
                   name == "dingtalk" || name == "feishu" || name == "lark" ||
                   name == "wxwork" || name == "chatgpt";
        }

        private static bool TryGetUsableRect(IntPtr hwnd, out NativeRect rect)
        {
            rect = new NativeRect();
            return NativeMethods.IsWindow(hwnd) &&
                   !NativeMethods.IsIconic(hwnd) &&
                   NativeMethods.GetWindowRect(hwnd, out rect) &&
                   rect.Width > 50 && rect.Height > 50;
        }

        private void ShowNotice(string message, ToolTipIcon icon)
        {
            tray.BalloonTipTitle = "实时字典";
            tray.BalloonTipText = message;
            tray.BalloonTipIcon = icon;
            tray.ShowBalloonTip(2500);
        }

        internal void RevealRunningApplication()
        {
            services.Log("Existing instance activation received");
            if (selectionAnalysis != null && !selectionAnalysis.IsDisposed)
            {
                selectionAnalysis.RestoreReadingCard();
                return;
            }
            if (manualLookup != null && !manualLookup.IsDisposed)
            {
                manualLookup.Show();
                manualLookup.Activate();
                return;
            }
            ShowNotice(services.ContinuousLookupEnabled
                ? "已在运行。回到 QQ 或微信，" +
                    (services.ContinuousLookupTrigger == "alt_click" ? "按住 Alt 单击消息" : "双击消息") +
                    "即可解释；选词查询可按 Ctrl+Alt+D。"
                : "已在运行。按 Ctrl+Alt+K，再点击一条消息；选中文字后按 Ctrl+Alt+D 查词。",
                ToolTipIcon.Info);
        }

        private void QueueShowCaptionHistory()
        {
            historyOpenTimer.Stop();
            historyOpenTimer.Start();
        }

        private void ShowCaptionHistory()
        {
            try
            {
                captionHistoryWindow.PresentArchive();
                services.Log("Opened caption history; visible=" + captionHistoryWindow.Visible +
                    "; native_visible=" + NativeMethods.IsWindowVisible(captionHistoryWindow.Handle));
            }
            catch (Exception error)
            {
                services.Log("Failed to open caption history: " + error.GetType().Name);
                ShowNotice("字幕记录窗口打开失败，请重新启动后再试。", ToolTipIcon.Error);
            }
        }

        private void UpdateReminderMenu()
        {
            reminderMenuItem.Text = "本地提醒（" + reminders.Count + "）…";
        }

        private void ClearCaptionHistory()
        {
            captionHistory.Clear();
            captionHistoryWindow.RefreshArchiveDate();
            if (active && services.WorkMode == "caption" &&
                !String.IsNullOrWhiteSpace(pendingCaptionText))
                captionLyricWindow.ShowLines(String.Empty, pendingCaptionText, targetRect);
            services.Log("Cleared in-memory caption history");
        }

        protected override void ExitThreadCore()
        {
            DisableSession();
            trackingTimer.Stop();
            if (winEventHook != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(winEventHook);
            if (mouseHook != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(mouseHook);
            NativeMethods.UnregisterHotKey(dispatcher.Handle, HotkeyHighlight);
            NativeMethods.UnregisterHotKey(dispatcher.Handle, HotkeyClear);
            NativeMethods.UnregisterHotKey(dispatcher.Handle, HotkeyLookup);
            if (manualLookup != null) manualLookup.Dispose();
            selectionGeneration++;
            selectionAction.Dispose();
            historyOpenTimer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            foreach (HighlightForm window in windows)
                window.Dispose();
            statusWindow.Dispose();
            messageHintWindow.Dispose();
            captionStatusWindow.Dispose();
            definitionWindow.Dispose();
            captionLyricWindow.Dispose();
            audioDisplayTimer.Dispose();
            audioRetryTimer.Dispose();
            audioHealthTimer.Dispose();
            captionHistoryWindow.Dispose();
            reminders.Dispose();
            services.Dispose();
            dispatcher.Dispose();
            base.ExitThreadCore();
        }
    }

}
