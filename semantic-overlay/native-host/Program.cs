using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\RealtimeDictionary.NativeHost.v1", out created))
            {
                if (!created)
                    return;
                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new OverlayContext());
            }
        }
    }

    internal sealed class OverlayContext : ApplicationContext
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
        private readonly ToolStripMenuItem trayStatusItem;
        private readonly ToolStripMenuItem trayUsageItem;
        private readonly System.Windows.Forms.Timer trackingTimer;
        private readonly List<HighlightForm> windows = new List<HighlightForm>();
        private readonly List<HighlightItem> relativeHighlights = new List<HighlightItem>();
        private readonly StatusForm statusWindow;
        private readonly DefinitionForm definitionWindow;
        private readonly CaptionLyricForm captionLyricWindow;
        private readonly CaptionHistoryForm captionHistoryWindow;
        private readonly LocalReminderManager reminders;
        private readonly ToolStripMenuItem reminderMenuItem;
        private readonly ServiceManager services;
        private readonly SelectionActionForm selectionAction = new SelectionActionForm();
        private Point? selectionDragStart;
        private IntPtr selectionTarget;
        private int selectionGeneration;
        private int selectionProbeBusy;
        private Func<List<OcrWord>, ScanResponse> refineWords;
        private bool choosingScanRegion;
        private IntPtr customRegionWindow;
        private RectangleF customRegion;
        private readonly object refreshLock = new object();
        private readonly Dictionary<string, LookupResponse> lookupCache =
            new Dictionary<string, LookupResponse>(StringComparer.OrdinalIgnoreCase);
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
        private bool browserBridgeRunning;
        private bool browserDomActive;
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
        private readonly Queue<byte[]> queuedAudio = new Queue<byte[]>();
        private string lastAudioTranscript;
        private string lastAudioTranscriptIdentity;
        private bool audioFailureShown;
        private int audioTransientFailures;
        private DateTime audioRetryNotBeforeUtc = DateTime.MinValue;
        private int audioTextGeneration;

        public OverlayContext()
        {
            services = new ServiceManager();
            refineWords = services.RefineWords;
            reminders = new LocalReminderManager();
            statusWindow = new StatusForm();
            definitionWindow = new DefinitionForm();
            captionLyricWindow = new CaptionLyricForm();
            captionHistoryWindow = new CaptionHistoryForm();
            captionHistoryWindow.Lookup = delegate(string term, string context)
            {
                services.EnsureRunning();
                return services.Lookup(term, context, false, null);
            };
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

            ContextMenuStrip menu = new ContextMenuStrip();
            trayStatusItem = new ToolStripMenuItem("状态：正在启动…");
            trayStatusItem.Enabled = false;
            menu.Items.Add(trayStatusItem);
            trayUsageItem = new ToolStripMenuItem("模型分析：读取中…");
            trayUsageItem.Enabled = false;
            menu.Items.Add(trayUsageItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("识别/刷新当前窗口", null, delegate { BeginSession(); });
            menu.Items.Add("清除当前高亮", null, delegate { DisableSession(); });
            menu.Items.Add("主动查词（选中文字后 Ctrl+Alt+D）…", null,
                delegate { OpenManualLookup(false); });
            ToolStripMenuItem selectionMenu = new ToolStripMenuItem("划词后显示 AI 解释") {
                Checked = services.SelectionToolbarEnabled };
            selectionMenu.Click += delegate {
                services.SetSelectionToolbarEnabled(!services.SelectionToolbarEnabled);
                selectionMenu.Checked = services.SelectionToolbarEnabled;
                DismissSelectionAction();
            };
            menu.Items.Add(selectionMenu);
            selectionAction.ExplainRequested += ExplainSelection;
            ToolStripMenuItem workModeMenu = new ToolStripMenuItem("工作模式");
            ToolStripMenuItem conversationModeItem = new ToolStripMenuItem("对话窗口（默认）");
            ToolStripMenuItem captionModeItem = new ToolStripMenuItem("会议语音字幕");
            conversationModeItem.Tag = "conversation";
            captionModeItem.Tag = "caption";
            ToolStripMenuItem[] workModeItems = { conversationModeItem, captionModeItem };
            foreach (ToolStripMenuItem item in workModeItems)
            {
                item.Checked = String.Equals(services.WorkMode, item.Tag as string, StringComparison.Ordinal);
                item.Click += delegate(object sender, EventArgs args)
                {
                    ToolStripMenuItem selected = sender as ToolStripMenuItem;
                    if (selected == null)
                        return;
                    string value = selected.Tag as string;
                    if (String.Equals(services.WorkMode, value, StringComparison.Ordinal))
                        return;
                    bool browserWasActive = browserBridgeRunning || browserDomActive;
                    if (active)
                        DisableSession();
                    services.SetWorkMode(value);
                    foreach (ToolStripMenuItem option in workModeItems)
                        option.Checked = option == selected;
                    lastCaptureFingerprint = null;
                    lastCaptionModelUtc = DateTime.MinValue;
                    lastCaptionRefinedGeneration = 0;
                    nextCaptionProbeUtc = DateTime.MaxValue;
                    if (active)
                    {
                        scanGeneration++;
                        highlightsCurrent = false;
                        browserBridgeRunning = false;
                        browserDomActive = false;
                        HideHighlights();
                        HideDefinition();
                        if (browserWasActive)
                            Task.Factory.StartNew(delegate { services.ClearBrowser(); });
                        if (value == "conversation" && IsBrowserWindow(targetWindow))
                            StartBrowserAdapter(targetWindow);
                        else
                            ScheduleRefresh(0);
                    }
                    if (value != "caption")
                        captionLyricWindow.Hide();
                    trayStatusItem.Text = value == "caption"
                        ? "状态：会议字幕模式"
                        : "状态：对话窗口模式";
                    tray.Text = value == "caption"
                        ? "实时字典 - 会议字幕模式"
                        : "实时字典 - 对话窗口模式";
                };
                workModeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(workModeMenu);
            ToolStripMenuItem audioSourceMenu = new ToolStripMenuItem("会议音源");
            ToolStripMenuItem processAudioItem = new ToolStripMenuItem("当前会议进程优先（推荐）");
            ToolStripMenuItem systemAudioItem = new ToolStripMenuItem("全系统声音（兼容模式）");
            processAudioItem.Tag = "process";
            systemAudioItem.Tag = "system";
            ToolStripMenuItem[] audioSourceItems = { processAudioItem, systemAudioItem };
            foreach (ToolStripMenuItem item in audioSourceItems)
            {
                item.Checked = String.Equals(services.CaptionAudioScope,
                    item.Tag as string, StringComparison.Ordinal);
                item.Click += delegate(object sender, EventArgs args)
                {
                    ToolStripMenuItem selected = sender as ToolStripMenuItem;
                    if (selected == null) return;
                    string value = selected.Tag as string;
                    if (active && services.WorkMode == "caption") DisableSession();
                    services.SetCaptionAudioScope(value);
                    foreach (ToolStripMenuItem option in audioSourceItems)
                        option.Checked = option == selected;
                    ShowNotice("会议音源已切换；请回到会议窗口按 Ctrl+Alt+K 重新开始。",
                        ToolTipIcon.Info);
                };
                audioSourceMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(audioSourceMenu);
            menu.Items.Add("查看字幕记录…", null, delegate { ShowCaptionHistory(); });
            reminderMenuItem = new ToolStripMenuItem();
            reminderMenuItem.Click += delegate { reminders.ShowList(); };
            reminders.CountChanged += UpdateReminderMenu;
            UpdateReminderMenu();
            menu.Items.Add(reminderMenuItem);
            ToolStripMenuItem difficultyMenu = new ToolStripMenuItem("标注密度");
            ToolStripMenuItem conciseItem = new ToolStripMenuItem("精简（只标核心术语）");
            ToolStripMenuItem standardItem = new ToolStripMenuItem("标准（推荐）");
            ToolStripMenuItem detailedItem = new ToolStripMenuItem("深入（更多疑难词）");
            conciseItem.Tag = "concise";
            standardItem.Tag = "standard";
            detailedItem.Tag = "detailed";
            ToolStripMenuItem[] difficultyItems = { conciseItem, standardItem, detailedItem };
            foreach (ToolStripMenuItem item in difficultyItems)
            {
                item.Checked = String.Equals(services.Difficulty, item.Tag as string, StringComparison.Ordinal);
                item.Click += delegate(object sender, EventArgs args)
                {
                    ToolStripMenuItem selected = sender as ToolStripMenuItem;
                    if (selected == null)
                        return;
                    string value = selected.Tag as string;
                    services.SetDifficulty(value);
                    foreach (ToolStripMenuItem option in difficultyItems)
                        option.Checked = option == selected;
                    lastCaptureFingerprint = null;
                    if (active)
                    {
                        scanGeneration++;
                        highlightsCurrent = false;
                        ScheduleRefresh(0);
                        trayStatusItem.Text = "状态：正在按新密度刷新…";
                    }
                };
                difficultyMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(difficultyMenu);
            ToolStripMenuItem scopeMenu = new ToolStripMenuItem("识别范围");
            ToolStripMenuItem autoScopeItem = new ToolStripMenuItem("自动估算对话区域（可框选校准）");
            ToolStripMenuItem fullScopeItem = new ToolStripMenuItem("整个窗口");
            ToolStripMenuItem manualScopeItem = new ToolStripMenuItem("手动框选（当前窗口）");
            manualScopeItem.Click += delegate { ChooseScanRegion(); };
            scopeMenu.DropDownOpening += delegate {
                bool manual = active && customRegionWindow != IntPtr.Zero && customRegionWindow == targetWindow;
                manualScopeItem.Checked = manual;
                autoScopeItem.Checked = !manual && services.ScanScope == "auto";
                fullScopeItem.Checked = !manual && services.ScanScope == "full";
                scopeMenu.Text = manual ? "识别范围：手动框选" : "识别范围";
            };
            autoScopeItem.Tag = "auto";
            fullScopeItem.Tag = "full";
            ToolStripMenuItem[] scopeItems = { autoScopeItem, fullScopeItem };
            foreach (ToolStripMenuItem item in scopeItems)
            {
                item.Checked = String.Equals(services.ScanScope, item.Tag as string, StringComparison.Ordinal);
                item.Click += delegate(object sender, EventArgs args)
                {
                    ToolStripMenuItem selected = sender as ToolStripMenuItem;
                    if (selected == null)
                        return;
                    services.SetScanScope(selected.Tag as string);
                    customRegionWindow = IntPtr.Zero;
                    foreach (ToolStripMenuItem option in scopeItems)
                        option.Checked = option == selected;
                    lastCaptureFingerprint = null;
                    if (active)
                    {
                        InvalidateContentAndSchedule(0);
                        trayStatusItem.Text = "状态：正在按新范围刷新…";
                    }
                };
                scopeMenu.DropDownItems.Add(item);
            }
            scopeMenu.DropDownItems.Add(manualScopeItem);
            scopeMenu.DropDownItems.Add("清除框选，恢复当前范围设置", null, delegate {
                customRegionWindow = IntPtr.Zero;
                InvalidateContentAndSchedule(0);
            });
            menu.Items.Add(scopeMenu);
            menu.Items.Add("恢复所有被忽略的词", null, delegate
            {
                if (services.IgnoredTermCount == 0)
                {
                    ShowNotice("当前没有被忽略的词。", ToolTipIcon.Info);
                    return;
                }
                if (MessageBox.Show(
                        "恢复所有被忽略的词吗？",
                        "实时字典",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                services.ClearIgnoredTerms();
                lastCaptureFingerprint = null;
                if (active)
                {
                    scanGeneration++;
                    highlightsCurrent = false;
                    ScheduleRefresh(0);
                }
                ShowNotice("已恢复所有被忽略的词。", ToolTipIcon.Info);
            });
            ToolStripMenuItem familiarityItem = new ToolStripMenuItem("自动减少多次未点击的词");
            familiarityItem.Checked = services.AutoLearnFamiliarTerms;
            familiarityItem.Click += delegate
            {
                bool enabled = !services.AutoLearnFamiliarTerms;
                services.SetAutoLearnFamiliarTerms(enabled);
                familiarityItem.Checked = enabled;
                if (active)
                {
                    lastCaptureFingerprint = null;
                    scanGeneration++;
                    highlightsCurrent = false;
                    ScheduleRefresh(0);
                }
                ShowNotice(enabled
                    ? "熟悉度学习已开启：同一词连续 4 次未点击后将不再标注。"
                    : "熟悉度学习已关闭：自动隐藏的词将在刷新后重新显示。",
                    ToolTipIcon.Info);
            };
            menu.Items.Add(familiarityItem);
            ToolStripMenuItem resetFamiliarityItem = new ToolStripMenuItem();
            resetFamiliarityItem.Click += delegate
            {
                services.ClearFamiliarTerms();
                lastCaptureFingerprint = null;
                if (active)
                {
                    scanGeneration++;
                    highlightsCurrent = false;
                    ScheduleRefresh(0);
                }
                ShowNotice("已恢复自动隐藏的词。", ToolTipIcon.Info);
            };
            menu.Items.Add(resetFamiliarityItem);
            menu.Items.Add("配置模型 API Key…", null, delegate { ConfigureApiKey(); });
            menu.Items.Add("打开浏览器扩展目录…", null, delegate { ShowBrowserExtensionHelp(); });
            menu.Items.Add("使用帮助…", null, delegate { ShowHelp(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Information;
            tray.Text = services.WorkMode == "caption"
                ? "实时字典 - 会议字幕模式"
                : "实时字典 - 对话窗口模式";
            tray.ContextMenuStrip = menu;
            menu.Opening += delegate
            {
                familiarityItem.Checked = services.AutoLearnFamiliarTerms;
                resetFamiliarityItem.Text = "恢复自动隐藏的词（" +
                    services.AutoSuppressedTermCount + "）";
                resetFamiliarityItem.Enabled = services.AutoSuppressedTermCount > 0;
                RefreshUsageStatus();
            };
            tray.Visible = true;
            tray.DoubleClick += delegate { BeginSession(); };

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
                BeginSession();
            else if (id == HotkeyClear)
                DisableSession();
            else if (id == HotkeyLookup)
            {
                services.Log("Ctrl+Alt+D received");
                OpenManualLookup(true);
            }
        }

        private ManualLookupForm manualLookup;
        private void OpenManualLookup(bool readSelection)
        {
            if (manualLookup != null && !manualLookup.IsDisposed)
            {
                // The existing form may be behind the source application and may
                // still contain the previous query. A new explicit invocation must
                // reread the current selection instead of merely activating stale UI.
                manualLookup.Open(readSelection);
                return;
            }
            manualLookup = new ManualLookupForm(services);
            manualLookup.Open(readSelection);
        }

        private void ConfigureApiKey()
        {
            using (ApiKeyForm form = new ApiKeyForm(services.ValidateApiKey))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return;
                try
                {
                    services.SaveApiKey(form.ApiKey, form.BaseUrl, form.Model);
                    trayStatusItem.Text = "状态：正在应用模型配置…";
                    Task.Factory.StartNew(delegate { return services.RestartAnalysisService(); })
                        .ContinueWith(delegate(Task<KeyValidationResult> task)
                        {
                            dispatcher.BeginInvoke(new Action(delegate
                            {
                                if (task.IsFaulted || task.Result == null || !task.Result.ok)
                                {
                                    trayStatusItem.Text = "状态：模型异常，使用本地模式";
                                    string detail = task.IsFaulted
                                        ? task.Exception.GetBaseException().Message
                                        : task.Result.message;
                                    ShowNotice("配置已保存，但应用失败：" + detail, ToolTipIcon.Warning);
                                    return;
                                }
                                trayStatusItem.Text = "状态：模型已连接，等待快捷键";
                                ShowNotice("模型服务已连接，无需重启程序。", ToolTipIcon.Info);
                            }));
                        });
                }
                catch (Exception error)
                {
                    services.Log("API Key save failed: " + error.Message);
                    MessageBox.Show(
                        "保存失败，请查看 _native_host.log。",
                        "实时字典",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private void RefreshModelStatus()
        {
            Task.Factory.StartNew(delegate
            {
                services.EnsureRunning();
                return services.GetServiceStatus();
            }).ContinueWith(delegate(Task<ServiceHealth> task)
            {
                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (task.IsFaulted || task.Result == null)
                        {
                            trayStatusItem.Text = "状态：服务异常";
                            return;
                        }
                        string mode = services.WorkMode == "caption" ? "会议字幕" : "对话窗口";
                        if (task.Result.ok && task.Result.has_key)
                            trayStatusItem.Text = "状态：" + mode + "模式，模型已配置";
                        else if (!task.Result.has_key)
                        {
                            trayStatusItem.Text = "状态：" + mode + "模式，仅本地识别";
                            if (services.TakeFirstRunNotice())
                                ShowNotice("已启动：按 Ctrl+Alt+K 识别；右键托盘可配置模型和查看帮助。", ToolTipIcon.Info);
                        }
                        else
                            trayStatusItem.Text = "状态：" + mode + "模式，模型异常";
                        UpdateUsageStatus(task.Result);
                    }));
                }
                catch (Exception error)
                {
                    services.Log("Model status UI update failed: " + error.Message);
                }
            });
        }

        private void RefreshUsageStatus()
        {
            Task.Factory.StartNew(delegate { return services.GetServiceStatus(); })
                .ContinueWith(delegate(Task<ServiceHealth> task)
                {
                    if (task.IsFaulted || task.Result == null)
                        return;
                    try
                    {
                        dispatcher.BeginInvoke(new Action(delegate { UpdateUsageStatus(task.Result); }));
                    }
                    catch (Exception error)
                    {
                        services.Log("Usage status UI update failed: " + error.Message);
                    }
                });
        }

        private void UpdateUsageStatus(ServiceHealth health)
        {
            if (health == null || !health.ok)
            {
                trayUsageItem.Text = "模型分析：服务不可用";
                return;
            }
            trayUsageItem.Text = String.Format(
                "模型分析：{0}/{1}（近1小时） · 缓存 {2}",
                health.model_analysis_calls_last_hour,
                health.model_analysis_limit_per_hour,
                health.analysis_cache_entries);
        }

        private void ShowHelp()
        {
            MessageBox.Show(
                "对话窗口（默认）\r\n" +
                "1. 切换到微信、QQ、钉钉或飞书。\r\n" +
                "2. 按 Ctrl + Alt + K 识别聊天正文。\r\n\r\n" +
                "会议语音字幕（无原生字幕也可用）\r\n" +
                "1. 从托盘的“工作模式”切换为“会议语音字幕”。\r\n" +
                "2. “会议音源”默认优先当前进程；遇到无声可切换全系统兼容模式。\r\n" +
                "3. 切回会议窗口，按 Ctrl + Alt + K，并确认发送语音片段。\r\n" +
                "4. 托盘状态会明确显示实际音源；麦克风默认不采集。\r\n\r\n" +
                "左键点击高亮词查看中文解释；解释打开时字幕刷新会暂停。\r\n" +
                "右键高亮词可选择以后不再标注。\r\n" +
                "蓝色时间可编辑为本地提醒；提醒保存在本机，到点弹窗，可延后10分钟。\r\n" +
                "实时字典必须在托盘运行才能准时提醒；托盘“本地提醒”可查看和删除。\r\n" +
                "按 Ctrl + Alt + G 结束当前高亮或字幕会话。\r\n\r\n" +
                "未高亮的词：选中文字后按 Ctrl + Alt + D；读取不到时可输入或粘贴。\r\n\r\n" +
                "会议模式不读取屏幕文字。静音在本地丢弃；检测到的语音片段发送到硅基流动生成字幕。临时网络错误会冷却后继续，密钥或余额错误才会停止。",
                "实时字典使用帮助",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ShowBrowserExtensionHelp()
        {
            try
            {
                services.OpenBrowserExtensionDirectory();
                MessageBox.Show(
                    "扩展目录已经打开。\r\n\r\n" +
                    "1. 打开 Edge/Chrome 的‘扩展程序’页面。\r\n" +
                    "2. 开启‘开发者模式’，选择‘加载已解压的扩展程序’。\r\n" +
                    "3. 选择刚刚打开的 browser-extension 文件夹。\r\n\r\n" +
                    "浏览器以外的软件不需要安装扩展。",
                    "实时字典 - 浏览器扩展",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                services.Log("Open browser extension directory failed: " + error.Message);
                MessageBox.Show(
                    "无法打开扩展目录，请重新安装实时字典。",
                    "实时字典",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void BeginSession()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || IsOwnWindow(foreground))
                return;

            NativeRect rect;
            if (!TryGetUsableRect(foreground, out rect))
                return;

            if (services.WorkMode == "caption" && MessageBox.Show(
                    "会议字幕会根据托盘中的“会议音源”设置捕获播放声音，并把本地检测到的语音片段发送到硅基流动生成字幕。\r\n\r\n" +
                    "如果 Windows 或会议软件不支持隔离，会明确回退为全系统声音；不会读取屏幕文字，也不会采集麦克风或保存录音。是否开始？",
                    "开始语音字幕", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

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
            browserBridgeRunning = false;
            browserDomActive = false;
            highlightsCurrent = false;
            nextCaptionProbeUtc = DateTime.MaxValue;
            lastCaptionModelUtc = DateTime.MinValue;
            lastCaptionRefinedGeneration = 0;
            relativeHighlights.Clear();
            pendingCaptionText = null;
            pendingCaptionCommitted = false;
            pendingCaptionWords = null;
            captionLyricWindow.SetSourceTop(-1);
            HideHighlights();
            captionLyricWindow.Hide();
            HideDefinition();
            services.Log("Highlight session started for HWND " + foreground.ToInt64());
            trayStatusItem.Text = services.WorkMode == "caption"
                ? "状态：正在启动系统声音字幕"
                : "状态：正在识别当前对话窗口";
            if (services.WorkMode == "caption")
            {
                StartAudioCaptionSession();
                return;
            }
            if (services.WorkMode != "caption" && IsBrowserWindow(foreground) &&
                customRegionWindow != foreground)
            {
                StartBrowserAdapter(foreground);
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
            services.EndAnalysisContext();
            scrollTrackingGeneration++;
            scrollTrackingFailures = 0;
            scrollFrame = null;
            scrollUntilUtc = DateTime.MinValue;
            preserveTrackedHighlights = false;
            trackingViewport = null;
            StopAudioCaptionSession();
            bool browserWasActive = browserBridgeRunning || browserDomActive;
            browserBridgeRunning = false;
            browserDomActive = false;
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
            HideDefinition();
            services.Log("Highlight session disabled");
            trayStatusItem.Text = services.WorkMode == "caption"
                ? "状态：会议字幕模式，未启用"
                : "状态：对话窗口模式，未启用";
            if (browserWasActive)
                Task.Factory.StartNew(delegate { services.ClearBrowser(); });
        }

        private void StartAudioCaptionSession()
        {
            StopAudioCaptionSession();
            int generation = ++audioSessionGeneration;
            audioFailureShown = false;
            audioTransientFailures = 0;
            audioRetryNotBeforeUtc = DateTime.MinValue;
            lastAudioTranscript = null;
            lastAudioTranscriptIdentity = null;
            queuedAudio.Clear();
            audioTranscriptionRunning = false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(targetWindow, out processId);
            bool preferIsolated = processId != 0 &&
                services.CaptionAudioScope == "process" &&
                ProcessLoopbackAudioClient.IsSupported &&
                !IsKnownProcessLoopbackUnsupported(targetWindow);
            string targetLabel = GetTargetProcessLabel(targetWindow);
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
                        if (task.Result.Item2)
                            ShowNotice("当前会议进程无法单独捕获，已回退为全系统声音。请关闭其他会发声的软件。",
                                ToolTipIcon.Warning);
                        string sourceName = audioCapture.IsProcessIsolated ? "仅会议进程" : "全系统声音";
                        trayStatusItem.Text = "状态：字幕目标 " + targetLabel + " · 音源：" + sourceName;
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
            capture.AudioChunkReady += delegate(byte[] wav) { QueueAudioTranscription(wav, generation); };
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
                capture.AudioChunkReady += delegate(byte[] wav) { QueueAudioTranscription(wav, generation); };
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

        private void QueueAudioTranscription(byte[] wav, int generation)
        {
            try
            {
                dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!active || generation != audioSessionGeneration || audioCapture == null) return;
                    if (DateTime.UtcNow < audioRetryNotBeforeUtc)
                    {
                        services.Log("Discarded audio chunk during transient provider cooldown");
                        return;
                    }
                    if (audioTranscriptionRunning)
                    {
                        if (queuedAudio.Count >= AudioQueueLimit)
                        {
                            queuedAudio.Dequeue();
                            services.Log("Audio transcription queue full; dropped oldest pending chunk");
                        }
                        queuedAudio.Enqueue(wav);
                        return;
                    }
                    BeginAudioTranscription(wav, generation);
                }));
            }
            catch { }
        }

        private void BeginAudioTranscription(byte[] wav, int generation)
        {
            audioTranscriptionRunning = true;
            Task.Factory.StartNew(delegate { return services.TranscribeAudio(wav); })
                .ContinueWith(delegate(Task<AudioTranscriptionResponse> task)
                {
                    try
                    {
                        dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (generation != audioSessionGeneration) return;
                            audioTranscriptionRunning = false;
                            if (task.IsFaulted || task.Result == null || !task.Result.ok)
                            {
                                string message = task.IsFaulted ? task.Exception.GetBaseException().Message :
                                    (task.Result == null ? "语音识别没有返回结果" : task.Result.error);
                                bool retryable = !task.IsFaulted && task.Result != null && task.Result.retryable;
                                if (!retryable)
                                {
                                    StopAudioAfterFailure(generation, message);
                                    return;
                                }
                                audioTransientFailures++;
                                int delaySeconds = Math.Min(20, 4 * audioTransientFailures);
                                audioRetryNotBeforeUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                                queuedAudio.Clear();
                                trayStatusItem.Text = "状态：语音服务暂时不可用，" + delaySeconds + " 秒后继续";
                                services.Log("Transient audio transcription failure; cooldown seconds=" + delaySeconds);
                            }
                            else if (!String.IsNullOrWhiteSpace(task.Result.text))
                            {
                                audioTransientFailures = 0;
                                audioRetryNotBeforeUtc = DateTime.MinValue;
                                ApplyAudioTranscript(task.Result.text.Trim(), generation);
                            }
                            byte[] next = queuedAudio.Count > 0 ? queuedAudio.Dequeue() : null;
                            if (next != null && active && generation == audioSessionGeneration &&
                                DateTime.UtcNow >= audioRetryNotBeforeUtc)
                                BeginAudioTranscription(next, generation);
                        }));
                    }
                    catch { }
                });
        }

        private void ApplyAudioTranscript(string text, int generation)
        {
            if (!active || generation != audioSessionGeneration) return;
            text = (text ?? String.Empty).Trim();
            string identity = NormalizeTranscriptIdentity(text);
            if (identity.Length == 0)
            {
                services.Log("Discarded empty or formatting-only audio transcript");
                return;
            }
            if (String.Equals(identity, lastAudioTranscriptIdentity, StringComparison.Ordinal))
            {
                services.Log("Discarded duplicate audio transcript");
                return;
            }
            string previous = lastAudioTranscript ?? String.Empty;
            lastAudioTranscript = text;
            lastAudioTranscriptIdentity = identity;
            pendingCaptionText = text;
            pendingCaptionCommitted = true;
            captionHistory.Add(new CaptionEntry { timestamp = DateTime.Now, text = text });
            if (captionHistory.Count > CaptionHistoryLimit) captionHistory.RemoveAt(0);
            captionHistoryWindow.SetEntries(captionHistory);
            if (NativeMethods.GetForegroundWindow() == targetWindow)
                captionLyricWindow.ShowLines(previous, text, targetRect);
            trayStatusItem.Text = "状态：系统声音字幕已更新";
            services.Log("Accepted audio transcript with " + text.Length + " characters");
            StartAudioTextAnalysis(text, generation, ++audioTextGeneration);
        }

        internal static string NormalizeTranscriptIdentity(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            StringBuilder builder = new StringBuilder(text.Length);
            foreach (char value in text)
                if (Char.IsLetterOrDigit(value)) builder.Append(Char.ToUpperInvariant(value));
            return builder.ToString();
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
            if (generation != audioSessionGeneration) return;
            trayStatusItem.Text = "状态：语音字幕异常";
            services.Log("Audio caption failure: " + message);
            if (!audioFailureShown)
            {
                audioFailureShown = true;
                ShowNotice("语音字幕失败：" + message, ToolTipIcon.Error);
            }
        }

        private void StopAudioAfterFailure(int generation, string message)
        {
            if (generation != audioSessionGeneration) return;
            StopAudioCaptionSession();
            active = false;
            captionLyricWindow.Hide();
            HideHighlights();
            trayStatusItem.Text = "状态：语音字幕已停止 — " + message;
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
            queuedAudio.Clear();
            audioTranscriptionRunning = false;
            audioTextGeneration++;
            if (audioCapture != null)
            {
                audioCapture.Dispose();
                audioCapture = null;
                services.Log("System audio caption capture stopped");
            }
            lastAudioTranscript = null;
            lastAudioTranscriptIdentity = null;
        }

        private void StartBrowserAdapter(IntPtr capturedWindow)
        {
            browserBridgeRunning = true;
            browserDomActive = false;
            statusWindow.ShowScanning(targetRect);
            Task.Factory.StartNew(delegate
            {
                try
                {
                    services.EnsureRunning();
                    return services.TryTriggerBrowser();
                }
                catch (Exception error)
                {
                    services.Log("Browser adapter trigger failed: " + error);
                    return false;
                }
            }).ContinueWith(delegate(Task<bool> task)
            {
                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        browserBridgeRunning = false;
                        if (!active || targetWindow != capturedWindow)
                            return;
                        if (services.WorkMode == "caption")
                        {
                            browserDomActive = false;
                            ScheduleRefresh(0);
                            return;
                        }
                        if (task.Result)
                        {
                            browserDomActive = true;
                            statusWindow.Hide();
                            services.Log("Browser DOM adapter acknowledged scan");
                        }
                        else
                        {
                            services.Log("Browser DOM adapter unavailable; falling back to OCR");
                            StartScan();
                        }
                    }));
                }
                catch (Exception error)
                {
                    services.Log("Browser adapter UI dispatch failed: " + error);
                }
            });
        }

        private void StartScan()
        {
            if (!active || scanRunning || choosingScanRegion || targetWindow == IntPtr.Zero)
                return;
            if (services.WorkMode == "caption" && audioCapture != null)
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
                catch (Exception error) { services.Log("Temporary scan cleanup failed: " + error.Message); }
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
                    catch (Exception error) { services.Log("Temporary scan cleanup failed: " + error.Message); }
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
                            bool browserWasActive = browserBridgeRunning || browserDomActive;
                            browserBridgeRunning = false;
                            browserDomActive = false;
                            if (browserWasActive)
                                Task.Factory.StartNew(delegate { services.ClearBrowser(); });
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
                    services.Log("Background refinement failed: " + error.Message);
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
                    services.Log("Refinement UI dispatch failed: " + error.Message);
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
                String.Equals(mode, "jev", StringComparison.OrdinalIgnoreCase) ||
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

        private void UpdateCaptionFromScan(
            int generation,
            IntPtr capturedWindow,
            NativeRect captureRect,
            List<OcrWord> words,
            int scanOffsetX,
            int scanOffsetY)
        {
            List<OcrWord> selectedCaptionWords;
            string text = BuildCaptionText(words, out selectedCaptionWords);
            if (String.IsNullOrWhiteSpace(text))
                return;
            DateTime now = DateTime.UtcNow;
            if (!String.Equals(text, pendingCaptionText, StringComparison.Ordinal))
            {
                bool variant = AreCaptionVariants(pendingCaptionText, text);
                if (!String.IsNullOrWhiteSpace(pendingCaptionText))
                {
                    if (!variant && !pendingCaptionCommitted)
                        CommitPendingCaption(false);
                    else if (variant && pendingCaptionCommitted && captionHistory.Count > 0 &&
                        String.Equals(captionHistory[captionHistory.Count - 1].text,
                            pendingCaptionText, StringComparison.Ordinal))
                    {
                        captionHistory[captionHistory.Count - 1].text = text;
                        captionHistoryWindow.SetEntries(captionHistory);
                    }
                }
                pendingCaptionText = text;
                pendingCaptionCommitted = false;
                pendingCaptionChangedUtc = now;
            }
            pendingCaptionWords = selectedCaptionWords;
            pendingCaptionGeneration = generation;
            pendingCaptionWindow = capturedWindow;
            pendingCaptionCaptureRect = captureRect;
            pendingCaptionOffsetX = scanOffsetX;
            pendingCaptionOffsetY = scanOffsetY;
            if (selectedCaptionWords.Count > 0)
            {
                double sourceTop = Double.MaxValue;
                foreach (OcrWord word in selectedCaptionWords) sourceTop = Math.Min(sourceTop, word.y);
                captionLyricWindow.SetSourceTop(scanOffsetY + (int)sourceTop);
            }
            captionLyricWindow.ShowLines(PreviousCaptionText(), pendingCaptionText, targetRect);
        }

        private void CommitPendingCaption(bool allowModel)
        {
            if (String.IsNullOrWhiteSpace(pendingCaptionText) || pendingCaptionCommitted)
                return;
            if (captionHistory.Count == 0 || !String.Equals(
                    captionHistory[captionHistory.Count - 1].text,
                    pendingCaptionText,
                    StringComparison.Ordinal))
            {
                captionHistory.Add(new CaptionEntry {
                    timestamp = DateTime.Now,
                    text = pendingCaptionText
                });
                if (captionHistory.Count > CaptionHistoryLimit)
                    captionHistory.RemoveAt(0);
                captionHistoryWindow.SetEntries(captionHistory);
            }
            pendingCaptionCommitted = true;
            captionLyricWindow.ShowLines(PreviousCaptionText(), pendingCaptionText, targetRect);
            services.Log("Committed stable caption to in-memory history");
            if (allowModel)
                TryStartCaptionRefinement();
        }

        private void TryStartCaptionRefinement()
        {
            if (!active || services.WorkMode != "caption" ||
                !pendingCaptionCommitted || pendingCaptionWords == null ||
                pendingCaptionWords.Count == 0 || refinementRunning ||
                pendingCaptionGeneration <= lastCaptionRefinedGeneration ||
                DateTime.UtcNow - lastCaptionModelUtc <
                    TimeSpan.FromMilliseconds(CaptionModelInterval))
                return;
            refinementRunning = true;
            lastCaptionModelUtc = DateTime.UtcNow;
            lastCaptionRefinedGeneration = pendingCaptionGeneration;
            services.Log(
                "Started stable caption refinement generation " +
                pendingCaptionGeneration + " with " + pendingCaptionWords.Count + " OCR words");
            StartRefinement(
                pendingCaptionGeneration,
                pendingCaptionWindow,
                pendingCaptionCaptureRect,
                new List<OcrWord>(pendingCaptionWords),
                pendingCaptionOffsetX,
                pendingCaptionOffsetY);
        }

        private string PreviousCaptionText()
        {
            int index = captionHistory.Count - 1;
            if (index >= 0 && String.Equals(
                    captionHistory[index].text, pendingCaptionText, StringComparison.Ordinal))
                index--;
            return index >= 0 ? captionHistory[index].text : String.Empty;
        }

        private static bool AreCaptionVariants(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second))
                return false;
            string left = first.Trim().ToLowerInvariant();
            string right = second.Trim().ToLowerInvariant();
            if (left.StartsWith(right, StringComparison.Ordinal) ||
                right.StartsWith(left, StringComparison.Ordinal))
                return true;
            int limit = Math.Min(left.Length, right.Length);
            int common = 0;
            while (common < limit && left[common] == right[common])
                common++;
            return limit >= 6 && common >= limit * 3 / 5;
        }

        private static string BuildCaptionText(
            List<OcrWord> words,
            out List<OcrWord> selectedWords)
        {
            selectedWords = new List<OcrWord>();
            if (words == null || words.Count == 0)
                return String.Empty;
            List<OcrWord> ordered = words.FindAll(delegate(OcrWord word) {
                return word != null && !String.IsNullOrWhiteSpace(word.text) && word.h > 0;
            });
            ordered.Sort(delegate(OcrWord left, OcrWord right) {
                int vertical = left.y.CompareTo(right.y);
                return Math.Abs(left.y - right.y) <= Math.Max(left.h, right.h) * 0.55
                    ? left.x.CompareTo(right.x) : vertical;
            });
            List<CaptionOcrLine> lines = new List<CaptionOcrLine>();
            foreach (OcrWord word in ordered)
            {
                double center = word.y + word.h / 2.0;
                CaptionOcrLine line = null;
                foreach (CaptionOcrLine candidate in lines)
                {
                    if (Math.Abs(candidate.centerY - center) <=
                        Math.Max(candidate.height, word.h) * 0.65)
                    {
                        line = candidate;
                        break;
                    }
                }
                if (line == null)
                {
                    line = new CaptionOcrLine();
                    lines.Add(line);
                }
                line.Add(word);
            }
            foreach (CaptionOcrLine line in lines)
                line.Finish();
            lines.RemoveAll(delegate(CaptionOcrLine line) {
                return line.text.Length < 4;
            });
            if (lines.Count == 0)
                return String.Empty;
            lines.Sort(delegate(CaptionOcrLine left, CaptionOcrLine right) {
                int score = right.Score.CompareTo(left.Score);
                return score != 0 ? score : right.centerY.CompareTo(left.centerY);
            });
            CaptionOcrLine best = lines[0];
            CaptionOcrLine adjacent = null;
            foreach (CaptionOcrLine line in lines)
            {
                if (line == best)
                    continue;
                if (Math.Abs(line.centerY - best.centerY) <= Math.Max(line.height, best.height) * 2.6 &&
                    Math.Abs(line.centerX - best.centerX) <= Math.Max(line.width, best.width) * 0.35)
                {
                    adjacent = line;
                    break;
                }
            }
            string result;
            if (adjacent == null)
            {
                result = best.text;
                selectedWords.AddRange(best.Words);
            }
            else if (adjacent.centerY < best.centerY)
            {
                result = adjacent.text + " " + best.text;
                selectedWords.AddRange(adjacent.Words);
                selectedWords.AddRange(best.Words);
            }
            else
            {
                result = best.text + " " + adjacent.text;
                selectedWords.AddRange(best.Words);
                selectedWords.AddRange(adjacent.Words);
            }
            result = result.Trim();
            return result.Length <= 500 ? result : result.Substring(0, 500).TrimEnd();
        }

        private static void OffsetHighlights(ScanResponse response, int x, int y)
        {
            if (response == null || response.highlights == null || (x == 0 && y == 0))
                return;
            foreach (HighlightItem item in response.highlights)
            {
                item.x += x;
                item.y += y;
            }
        }

        private static ScanResponse CloneScanResponseHighlights(ScanResponse source)
        {
            ScanResponse clone = new ScanResponse {
                ok = source != null && source.ok,
                error = source == null ? null : source.error,
                duration_ms = source == null ? 0 : source.duration_ms,
                ocr_ms = source == null ? 0 : source.ocr_ms,
                local_analysis_ms = source == null ? 0 : source.local_analysis_ms,
                analysis_duration_ms = source == null ? 0 : source.analysis_duration_ms,
                analysis_mode = source == null ? null : source.analysis_mode,
                words = source == null ? null : source.words,
                highlights = new List<HighlightItem>()
            };
            if (source == null || source.highlights == null)
                return clone;
            foreach (HighlightItem item in source.highlights)
            {
                if (item == null) continue;
                clone.highlights.Add(new HighlightItem {
                    term = item.term, context = item.context, kind = item.kind,
                    title = item.title, time_text = item.time_text,
                    start_iso = item.start_iso, end_iso = item.end_iso,
                    utc_offset = item.utc_offset,
                    needs_confirmation = item.needs_confirmation,
                    x = item.x, y = item.y, w = item.w, h = item.h
                });
            }
            return clone;
        }

        private void TrackTarget(object sender, EventArgs args)
        {
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
                return;
            }

            UpdateTargetGeometry();

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != targetWindow)
            {
                HideHighlights();
                captionLyricWindow.Hide();
                statusWindow.Hide();
                HideDefinition();
            }
            else
            {
                if (services.WorkMode == "caption" &&
                    !String.IsNullOrWhiteSpace(pendingCaptionText))
                    captionLyricWindow.ShowLines(
                        PreviousCaptionText(), pendingCaptionText, targetRect);

                if (browserBridgeRunning)
                    statusWindow.ShowScanning(targetRect);
                else if (browserDomActive && services.WorkMode != "caption")
                {
                    // The browser content script owns DOM highlights and refreshes them itself.
                    return;
                }
                else if (highlightsCurrent && relativeHighlights.Count > 0 && !highlightsVisible)
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

            if (foreground == targetWindow && services.WorkMode == "caption" &&
                !browserBridgeRunning && !browserDomActive && !scanRunning &&
                !definitionWindow.Visible && now >= nextCaptionProbeUtc)
            {
                nextCaptionProbeUtc = now.AddMilliseconds(CaptionProbeInterval);
                ScheduleRefresh(0);
            }

            DateTime due;
            lock (refreshLock)
                due = refreshDueUtc;
            bool popupBlocksCaptionScan = services.WorkMode == "caption" && definitionWindow.Visible;
            if (!scanRunning && !popupBlocksCaptionScan && due != DateTime.MaxValue &&
                DateTime.UtcNow >= due && NativeMethods.GetForegroundWindow() == targetWindow)
                StartScan();
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
                return item != null &&
                    !String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase) &&
                    services.ShouldSuppressTerm(item.term);
            });
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
            return renderWatch.ElapsedMilliseconds;
        }

        private void PositionHighlightWindows()
        {
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
            highlightsVisible = false;
        }

        private void BeginLookup(HighlightForm source, string term, string context)
        {
            if (!active || source == null || string.IsNullOrWhiteSpace(term))
                return;
            lookupHistory.Clear();
            currentLookup = null;
            BeginLookupTerm(source, term.Trim(), context, source.Bounds);
        }

        private void BeginItemAction(HighlightForm source, HighlightItem item)
        {
            if (item == null)
                return;
            if (String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase))
            {
                lookupGeneration++;
                lookupHistory.Clear();
                currentLookup = null;
                selectedHighlight = source;
                OpenReminderEditor(item, null);
                services.Log("Reminder confirmation opened");
                return;
            }
            services.NoteTermClicked(item.term);
            BeginLookup(source, item.term, item.context);
        }

        private void OpenReminderEditor(HighlightItem candidate, Form owner)
        {
            if (candidate == null)
                return;
            HideDefinition();
            using (CalendarForm editor = new CalendarForm(
                candidate, services.ExportCalendar, reminders.Create, reminders.ShowList,
                delegate(LocalReminderResult result)
                {
                    UpdateReminderMenu();
                    ShowNotice("提醒已创建，可从托盘的“本地提醒”查看。", ToolTipIcon.Info);
                }))
            {
                if (owner != null && owner.Visible)
                    editor.ShowDialog(owner);
                else
                    editor.ShowDialog();
            }
        }

        private void IgnoreHighlight(HighlightForm source, string term)
        {
            string value = (term ?? String.Empty).Trim();
            if (value.Length == 0)
                return;
            services.IgnoreTerm(value);
            if (selectedHighlight == source)
                HideDefinition();
            relativeHighlights.RemoveAll(delegate(HighlightItem item)
            {
                return String.Equals(item.term, value, StringComparison.OrdinalIgnoreCase);
            });
            RenderHighlights();
            trayStatusItem.Text = "状态：已忽略 1 个词";
            ShowNotice("以后不再标注“" + value + "”；可从托盘恢复。", ToolTipIcon.Info);
        }

        private void BeginNestedLookup(string term)
        {
            if (!active || selectedHighlight == null || string.IsNullOrWhiteSpace(term) ||
                currentLookup == null || lookupHistory.Count >= 3)
                return;
            lookupHistory.Add(currentLookup);
            BeginLookupTerm(
                selectedHighlight,
                term.Trim(),
                currentLookup.explanation,
                definitionWindow.Bounds);
        }

        private void RetryCurrentLookup()
        {
            if (!active || selectedHighlight == null || currentLookup == null ||
                !currentLookup.can_refresh)
                return;
            BeginLookupTerm(
                selectedHighlight,
                currentLookup.term,
                currentLookup.context,
                definitionWindow.Bounds,
                true,
                currentLookup.explanation);
        }

        private void BeginLookupTerm(
            HighlightForm source,
            string term,
            string context,
            Rectangle anchor,
            bool refresh = false,
            string previousExplanation = null)
        {
            selectedHighlight = source;
            int generation = ++lookupGeneration;
            definitionWindow.ShowLoading(term, anchor, lookupHistory.Count > 0, refresh);
            definitionWindow.Update();
            services.Log("Lookup started for " + term);
            string normalizedContext = (context ?? String.Empty).Trim();
            if (normalizedContext.Length > 500)
                normalizedContext = normalizedContext.Substring(0, 500);
            string cacheKey = term + "\n" + normalizedContext;

            LookupResponse cached;
            if (ShortcutLookup.TryExplain(term, normalizedContext, out cached))
            {
                ApplyLookupResponse(source, cached.term, normalizedContext, cacheKey, anchor, generation, cached, false);
                return;
            }
            if (!refresh && lookupCache.TryGetValue(cacheKey, out cached))
            {
                ApplyLookupResponse(source, term, normalizedContext, cacheKey, anchor, generation, cached, false);
                services.Log("Lookup cache hit for " + term);
                return;
            }

            Task.Factory.StartNew(delegate
            {
                try
                {
                    services.EnsureRunning();
                    return services.Lookup(term, normalizedContext, refresh, previousExplanation);
                }
                catch (Exception error)
                {
                    return new LookupResponse { term = term, error = error.Message };
                }
            }).ContinueWith(delegate(Task<LookupResponse> task)
            {
                LookupResponse response = task.Result;
                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (generation != lookupGeneration || selectedHighlight != source || !active)
                            return;
                        ApplyLookupResponse(
                            source, term, normalizedContext, cacheKey, anchor, generation, response, refresh);
                    }));
                }
                catch (Exception error)
                {
                    services.Log("Lookup UI dispatch failed: " + error);
                }
            });
        }

        private void ApplyLookupResponse(
            HighlightForm source,
            string term,
            string context,
            string cacheKey,
            Rectangle anchor,
            int generation,
            LookupResponse response,
            bool refresh)
        {
            if (generation != lookupGeneration || selectedHighlight != source || !active)
                return;
            if (refresh && currentLookup != null &&
                (response == null || !string.IsNullOrEmpty(response.error) ||
                 string.IsNullOrWhiteSpace(response.explanation)))
            {
                definitionWindow.ShowDefinition(
                    currentLookup.term,
                    currentLookup.explanation,
                    currentLookup.entities,
                    currentLookup.sources,
                    currentLookup.anchor,
                    lookupHistory.Count > 0,
                    currentLookup.can_refresh);
                services.Log("Lookup refresh failed for " + term);
                return;
            }
            if (response == null)
                response = new LookupResponse { term = term, error = "empty response" };
            lookupCache[cacheKey] = response;
            string explanation = response.explanation;
            if (string.IsNullOrWhiteSpace(explanation))
                explanation = "暂时无法获取可靠解释。";
            currentLookup = new LookupView
            {
                term = term,
                context = context,
                explanation = explanation,
                entities = response.entities,
                sources = response.sources,
                can_refresh = response.can_refresh,
                anchor = anchor
            };
            definitionWindow.ShowDefinition(
                term,
                explanation,
                response.entities,
                response.sources,
                anchor,
                lookupHistory.Count > 0,
                response.can_refresh);
            services.Log(
                "Lookup finished for " + term +
                (string.IsNullOrEmpty(response.error) ? ": ok" : ": failed"));
        }

        private void NavigateBack()
        {
            if (!active || selectedHighlight == null || lookupHistory.Count == 0)
                return;
            LookupView previous = lookupHistory[lookupHistory.Count - 1];
            lookupHistory.RemoveAt(lookupHistory.Count - 1);
            currentLookup = previous;
            lookupGeneration++;
            definitionWindow.ShowDefinition(
                previous.term,
                previous.explanation,
                previous.entities,
                previous.sources,
                previous.anchor,
                lookupHistory.Count > 0,
                previous.can_refresh);
            services.Log("Lookup history back to " + previous.term);
        }

        private void HideDefinition()
        {
            if (selectedHighlight == null && !definitionWindow.Visible)
                return;
            lookupGeneration++;
            selectedHighlight = null;
            currentLookup = null;
            lookupHistory.Clear();
            definitionWindow.Hide();
        }

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
            Task.Factory.StartNew(delegate
            {
                ScrollProbeResult result = new ScrollProbeResult();
                result.Next = ScrollFrame.Capture(viewport);
                result.Confident = previous != null &&
                    previous.TryDisplacement(result.Next, out result.Delta);
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
            if (browserDomActive || browserBridgeRunning)
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
            if (code >= 0 && services.SelectionToolbarEnabled &&
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

        private void ObserveSelectionGesture(int message, Point point)
        {
            if (!services.SelectionToolbarEnabled) return;
            if (selectionAction.Visible && selectionAction.Bounds.Contains(point)) return;
            if (message == 0x0201)
            {
                DismissSelectionAction();
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero || IsOwnWindow(foreground)) return;
                selectionTarget = foreground;
                selectionDragStart = point;
            }
            else if (message == NativeMethods.WmMouseWheel || message == 0x0204)
                DismissSelectionAction();
            else if (message == NativeMethods.WmLButtonUp && selectionDragStart.HasValue)
            {
                Point start = selectionDragStart.Value;
                selectionDragStart = null;
                if (Math.Abs(start.X - point.X) < 8 && Math.Abs(start.Y - point.Y) < 8) return;
                ProbeSelection(start, point, selectionTarget, selectionGeneration);
            }
        }

        private async void ProbeSelection(Point start, Point end, IntPtr target, int generation)
        {
            await Task.Delay(130);
            if (generation != selectionGeneration || NativeMethods.GetForegroundWindow() != target ||
                Interlocked.CompareExchange(ref selectionProbeBusy, 1, 0) != 0) return;
            Task<string> probe = Task.Factory.StartNew(delegate {
                try { return ManualLookupForm.ReadSelection(); }
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
                OpenSelectionResult(text, false);
            }
            else OpenSelectionResult(text, true);
        }

        private void OpenSelectionResult(string text, bool exactSelection)
        {
            if (manualLookup != null && !manualLookup.IsDisposed) manualLookup.Close();
            manualLookup = new ManualLookupForm(services);
            Rectangle area = Screen.FromPoint(selectionAction.Location).WorkingArea;
            manualLookup.StartPosition = FormStartPosition.Manual;
            manualLookup.Location = new Point(
                Math.Max(area.Left, Math.Min(selectionAction.Left, area.Right - manualLookup.Width)),
                Math.Max(area.Top, Math.Min(selectionAction.Top, area.Bottom - manualLookup.Height)));
            manualLookup.OpenText(text, exactSelection);
        }

        private bool IsOwnWindow(IntPtr hwnd)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            return processId == (uint)Process.GetCurrentProcess().Id;
        }

        private static bool IsBrowserWindow(IntPtr hwnd)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    string name = process.ProcessName.ToLowerInvariant();
                    return name == "chrome" || name == "msedge" || name == "firefox" ||
                           name == "brave" || name == "opera" || name == "vivaldi";
                }
            }
            catch
            {
                return false;
            }
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

        private void ShowCaptionHistory()
        {
            captionHistoryWindow.SetEntries(captionHistory);
            if (!captionHistoryWindow.Visible)
                captionHistoryWindow.Show();
            captionHistoryWindow.Activate();
        }

        private void UpdateReminderMenu()
        {
            reminderMenuItem.Text = "本地提醒（" + reminders.Count + "）…";
        }

        private void ClearCaptionHistory()
        {
            captionHistory.Clear();
            captionHistoryWindow.SetEntries(captionHistory);
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
            tray.Visible = false;
            tray.Dispose();
            foreach (HighlightForm window in windows)
                window.Dispose();
            statusWindow.Dispose();
            definitionWindow.Dispose();
            captionLyricWindow.Dispose();
            captionHistoryWindow.Dispose();
            reminders.Dispose();
            services.Dispose();
            dispatcher.Dispose();
            base.ExitThreadCore();
        }
    }

    internal sealed class CaptionEntry
    {
        public DateTime timestamp { get; set; }
        public string text { get; set; }
    }

    internal sealed class CaptionOcrLine
    {
        private readonly List<OcrWord> words = new List<OcrWord>();
        public string text { get; private set; }
        public double centerY { get; private set; }
        public double centerX { get; private set; }
        public double height { get; private set; }
        public double width { get; private set; }
        public int Score { get; private set; }
        public List<OcrWord> Words
        {
            get { return new List<OcrWord>(words); }
        }

        public void Add(OcrWord word)
        {
            words.Add(word);
            RecalculateBounds();
        }

        public void Finish()
        {
            words.Sort(delegate(OcrWord left, OcrWord right) {
                return left.x.CompareTo(right.x);
            });
            StringBuilder builder = new StringBuilder();
            OcrWord previousWord = null;
            foreach (OcrWord word in words)
            {
                string value = (word.text ?? String.Empty).Trim();
                if (value.Length == 0)
                    continue;
                if (builder.Length > 0 && NeedsSpace(previousWord, word,
                        builder[builder.Length - 1], value[0]))
                    builder.Append(' ');
                builder.Append(value);
                previousWord = word;
            }
            text = builder.ToString().Trim();
            Score = Math.Min(500, text.Length) * 2 + Math.Min(20, words.Count) * 7;
        }

        private void RecalculateBounds()
        {
            double left = Double.MaxValue;
            double top = Double.MaxValue;
            double right = Double.MinValue;
            double bottom = Double.MinValue;
            foreach (OcrWord word in words)
            {
                left = Math.Min(left, word.x);
                top = Math.Min(top, word.y);
                right = Math.Max(right, word.x + word.w);
                bottom = Math.Max(bottom, word.y + word.h);
            }
            width = Math.Max(1, right - left);
            height = Math.Max(1, bottom - top);
            centerX = left + width / 2.0;
            centerY = top + height / 2.0;
        }

        private static bool NeedsSpace(OcrWord previous, OcrWord current, char left, char right)
        {
            if (!IsAsciiWord(left) || !IsAsciiWord(right))
                return false;
            if (previous == null || current == null)
                return true;
            double gap = current.x - (previous.x + previous.w);
            double fragmentThreshold = Math.Max(
                2.0, Math.Min(previous.h, current.h) * 0.18);
            return gap > fragmentThreshold;
        }

        private static bool IsAsciiWord(char value)
        {
            return (value >= 'a' && value <= 'z') ||
                   (value >= 'A' && value <= 'Z') ||
                   (value >= '0' && value <= '9');
        }
    }

    internal sealed class CaptionLyricForm : Form
    {
        private static readonly Color Chroma = Color.Black;
        private string previousLine = String.Empty;
        private string currentLine = String.Empty;
        private int sourceTop = -1;

        public void SetSourceTop(int relativeTop) { sourceTop = relativeTop; }

        public CaptionLyricForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Chroma;
            TransparencyKey = Chroma;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExTransparent |
                                      NativeMethods.WsExToolWindow |
                                      NativeMethods.WsExNoActivate;
                return parameters;
            }
        }

        public void ShowLines(string previous, string current, NativeRect target)
        {
            string normalizedPrevious = (previous ?? String.Empty).Trim();
            string normalizedCurrent = (current ?? String.Empty).Trim();
            if (String.Equals(normalizedPrevious, normalizedCurrent, StringComparison.Ordinal))
                normalizedPrevious = String.Empty;
            if (!String.Equals(previousLine, normalizedPrevious, StringComparison.Ordinal) ||
                !String.Equals(currentLine, normalizedCurrent, StringComparison.Ordinal))
            {
                previousLine = normalizedPrevious;
                currentLine = normalizedCurrent;
                Invalidate();
            }
            PositionFor(target);
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                Handle, NativeMethods.HwndTopMost, Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        public void PositionFor(NativeRect target)
        {
            int width = Math.Max(420, Math.Min(1100, target.Width * 82 / 100));
            int height = 112;
            int x = target.Left + (target.Width - width) / 2;
            // Most meeting products already place their own caption bar at the bottom.
            // Keep the transparent lyric just above that area instead of duplicating
            // text directly on top of the source caption.
            int y = target.Top + target.Height * 52 / 100;
            if (sourceTop >= height + 36)
                y = Math.Min(y, target.Top + sourceTop - height - 12);
            y = Math.Max(target.Top + 24, Math.Min(y, target.Bottom - height - 48));
            Bounds = new Rectangle(x, y, width, height);
        }

        public bool TryGetCurrentRange(int start, int length, out Rectangle screen)
        {
            screen = Rectangle.Empty;
            if (start < 0 || length <= 0 || start + length > currentLine.Length || !Visible) return false;
            Rectangle bounds = new Rectangle(8, 48, Math.Max(1, Width - 16), 57);
            using (Graphics graphics = CreateGraphics())
            using (Font font = new Font("Microsoft YaHei UI", 25f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.SetMeasurableCharacterRanges(new[] { new CharacterRange(start, length) });
                Region[] regions = graphics.MeasureCharacterRanges(currentLine, font, bounds, format);
                if (regions.Length == 0) return false;
                RectangleF measured = regions[0].GetBounds(graphics);
                foreach (Region region in regions) region.Dispose();
                if (measured.Width < 1 || measured.Height < 1) return false;
                Rectangle client = Rectangle.Ceiling(measured);
                client.Inflate(2, 1);
                screen = RectangleToScreen(client);
                return true;
            }
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            args.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Rectangle previousBounds = new Rectangle(8, 6, Math.Max(1, Width - 16), 42);
            Rectangle currentBounds = new Rectangle(8, 48, Math.Max(1, Width - 16), 57);
            DrawOutlinedText(args.Graphics, previousLine, previousBounds, 18f,
                Color.FromArgb(210, 225, 225, 225), 3.2f);
            DrawOutlinedText(args.Graphics, currentLine, currentBounds, 25f,
                Color.White, 4.2f);
        }

        private static void DrawOutlinedText(
            Graphics graphics,
            string text,
            Rectangle bounds,
            float size,
            Color fill,
            float outline)
        {
            if (String.IsNullOrWhiteSpace(text))
                return;
            using (FontFamily family = new FontFamily("Microsoft YaHei UI"))
            using (StringFormat format = new StringFormat())
            using (GraphicsPath path = new GraphicsPath())
            using (Pen border = new Pen(Color.FromArgb(235, 18, 18, 18), outline) {
                LineJoin = LineJoin.Round
            })
            using (Brush brush = new SolidBrush(fill))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                float fittedSize = size;
                using (Font probe = new Font(family, fittedSize, FontStyle.Bold, GraphicsUnit.Point))
                {
                    SizeF measured = graphics.MeasureString(text, probe, Int32.MaxValue, format);
                    if (measured.Width > bounds.Width - 12)
                        fittedSize = Math.Max(14f, fittedSize * (bounds.Width - 12) / measured.Width);
                }
                float emSize = graphics.DpiY * fittedSize / 72f;
                path.AddString(text, family, (int)FontStyle.Bold, emSize, bounds, format);
                graphics.DrawPath(border, path);
                graphics.FillPath(brush, path);
            }
        }
    }

    internal sealed class CaptionHistoryForm : Form
    {
        public Func<string, string, LookupResponse> Lookup;
        public Func<string, AnalyzeResponse> Analyze;
        public event Action<HighlightItem> EditTaskRequested;
        private readonly Button taskButton = new Button();
        private int taskVersion;
        private readonly FlowLayoutPanel explanationPanel = new FlowLayoutPanel();
        private readonly LinkLabel explanation = new LinkLabel();
        private readonly Button lookupButton = new Button();
        private readonly Button backButton = new Button();
        private readonly List<LookupResponse> history = new List<LookupResponse>();
        private LookupResponse current;
        private int lookupVersion;
        private readonly RichTextBox transcript;
        private readonly Button copyButton;
        private readonly Button clearButton;
        private readonly Button exportButton;
        public event Action ClearRequested;

        public CaptionHistoryForm()
        {
            Text = "本次字幕记录";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 520);
            MinimumSize = new Size(520, 340);
            Font = new Font("Microsoft YaHei UI", 10f);
            ShowInTaskbar = true;

            Label notice = new Label();
            notice.Dock = DockStyle.Top;
            notice.Height = 42;
            notice.Padding = new Padding(10, 10, 10, 4);
            notice.Text = "选词查解释，选中会议安排可提取日程。最多保留 5000 条，退出前请导出。";

            transcript = new RichTextBox();
            transcript.Dock = DockStyle.Fill;
            transcript.ReadOnly = true;
            transcript.BackColor = Color.White;
            transcript.BorderStyle = BorderStyle.FixedSingle;
            transcript.DetectUrls = true;
            transcript.HideSelection = false;
            lookupButton.Text = "解释选中文字";
            lookupButton.AutoSize = true;
            lookupButton.Enabled = false;
            transcript.SelectionChanged += delegate
            {
                int length = transcript.SelectedText.Trim().Length;
                lookupButton.Enabled = length > 0 && length <= 80;
                taskButton.Enabled = length > 0;
            };
            lookupButton.Click += async delegate
            {
                string term = transcript.SelectedText.Trim();
                if (term.Length == 0 || term.Length > 80) return;
                string context = SelectedContext(500);
                history.Clear();
                current = null;
                await LookupTerm(term, context);
            };
            explanationPanel.Dock = DockStyle.Bottom;
            explanationPanel.Height = 160;
            explanationPanel.AutoScroll = true;
            explanation.AutoSize = true;
            explanation.MaximumSize = new Size(650, 0);
            explanation.Padding = new Padding(10);
            explanationPanel.Controls.Add(explanation);
            explanationPanel.SizeChanged += delegate
            {
                explanation.MaximumSize = new Size(Math.Max(200, explanationPanel.ClientSize.Width - 30), 0);
            };
            explanation.LinkClicked += async delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                if (current == null || history.Count >= 3) return;
                string context = current.explanation;
                history.Add(current);
                await LookupTerm((string)args.Link.LinkData, context);
            };
            backButton.Text = "返回上个解释";
            backButton.AutoSize = true;
            backButton.Enabled = false;
            backButton.Click += delegate
            {
                if (history.Count == 0) return;
                lookupVersion++;
                current = history[history.Count - 1];
                history.RemoveAt(history.Count - 1);
                RenderExplanation();
            };
            VisibleChanged += delegate
            {
                if (!Visible) { lookupVersion++; taskVersion++; }
            };
            taskButton.Text = "从所选内容设置提醒";
            taskButton.AutoSize = true;
            taskButton.Enabled = false;
            taskButton.Click += async delegate
            {
                string source = SelectedContext(2000);
                if (String.IsNullOrWhiteSpace(source)) return;
                int version = ++taskVersion;
                lookupVersion++;
                taskButton.Enabled = false;
                explanation.Links.Clear();
                explanation.Text = "正在识别这句话的日程…";
                try
                {
                    if (Analyze == null) throw new InvalidOperationException();
                    AnalyzeResponse response = await Task.Factory.StartNew(() => Analyze(source));
                    if (version != taskVersion || !Visible || IsDisposed) return;
                    var actions = response == null ? null : response.actions;
                    if (actions == null || actions.Count == 0)
                    {
                        explanation.Text = "没有识别到明确的日程安排。请选择包含时间和行动的完整句子后重试。";
                        return;
                    }
                    if (actions.Count > 1)
                    {
                        explanation.Text = "检测到多个安排，请只选中要处理的那一句后重试。";
                        return;
                    }
                    AnalysisEntity action = actions[0];
                    explanation.Text = "已识别安排，尚未创建提醒。请补齐并确认。";
                    if (EditTaskRequested != null) EditTaskRequested(new HighlightItem {
                        term = action.text, time_text = action.time_text, title = action.title,
                        start_iso = action.start_iso, end_iso = action.end_iso,
                        utc_offset = action.utc_offset, kind = "task", needs_confirmation = true
                    });
                }
                catch (Exception)
                {
                    if (version == taskVersion && Visible && !IsDisposed)
                        explanation.Text = "日程识别失败，请确认服务连接后重试。";
                }
                finally { if (!IsDisposed) taskButton.Enabled = transcript.SelectionLength > 0; }
            };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 90;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Padding = new Padding(8);

            Button closeButton = new Button { Text = "关闭", AutoSize = true };
            clearButton = new Button { Text = "清空记录", AutoSize = true };
            copyButton = new Button { Text = "复制全部", AutoSize = true };
            exportButton = new Button { Text = "导出文本…", AutoSize = true };
            exportButton.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(transcript.Text)) return;
                using (SaveFileDialog dialog = new SaveFileDialog())
                {
                    dialog.Title = "保存字幕记录";
                    dialog.Filter = "文本文件 (*.txt)|*.txt";
                    dialog.DefaultExt = "txt";
                    dialog.AddExtension = true;
                    dialog.OverwritePrompt = true;
                    dialog.FileName = "字幕记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        File.WriteAllText(dialog.FileName, transcript.Text, new UTF8Encoding(true));
                        MessageBox.Show(this, "字幕记录已保存。", "字幕记录");
                    }
                    catch (Exception error)
                    {
                        MessageBox.Show(this, "保存失败，记录仍保留在窗口中：" + error.Message,
                            "字幕记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            };
            closeButton.Click += delegate { Hide(); };
            clearButton.Click += delegate
            {
                if (ClearRequested != null)
                    ClearRequested();
            };
            copyButton.Click += delegate
            {
                if (!String.IsNullOrWhiteSpace(transcript.Text))
                    Clipboard.SetText(transcript.Text);
            };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(exportButton);
            buttons.Controls.Add(lookupButton);
            buttons.Controls.Add(backButton);
            buttons.Controls.Add(taskButton);

            Controls.Add(transcript);
            Controls.Add(explanationPanel);
            Controls.Add(buttons);
            Controls.Add(notice);
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (args.CloseReason == CloseReason.UserClosing)
                {
                    args.Cancel = true;
                    Hide();
                }
            };
        }

        private string SelectedContext(int limit)
        {
            string value = transcript.Text;
            int position = transcript.SelectionStart;
            int from = position == 0 ? 0 : value.LastIndexOf('\n', position - 1) + 1;
            int to = value.IndexOf('\n', Math.Min(value.Length, position + transcript.SelectionLength));
            if (to < 0) to = value.Length;
            if (to - from > limit) from = Math.Max(from, position - Math.Max(0, (limit - transcript.SelectionLength) / 2));
            string selected = value.Substring(from, Math.Min(limit, to - from));
            return System.Text.RegularExpressions.Regex.Replace(selected,
                @"(?m)^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] ", "");
        }

        private async Task LookupTerm(string term, string context)
        {
            taskVersion++;
            int version = ++lookupVersion;
            explanation.Links.Clear();
            explanation.Text = "正在解释“" + term + "”…";
            backButton.Enabled = history.Count > 0;
            try
            {
                if (Lookup == null) throw new InvalidOperationException();
                if (context.Length > 500) context = context.Substring(0, 500);
                LookupResponse response = await Task.Factory.StartNew(() => Lookup(term, context));
                if (version != lookupVersion || IsDisposed || !Visible) return;
                current = response;
                RenderExplanation();
            }
            catch (Exception)
            {
                if (version == lookupVersion && !IsDisposed && Visible)
                    explanation.Text = "暂时无法查询，请稍后重新点击解释选中文字。";
            }
        }

        private void RenderExplanation()
        {
            explanation.Links.Clear();
            string body = current == null ? null : current.explanation;
            explanation.Text = String.IsNullOrWhiteSpace(body) ? "暂时没有可靠解释，请重试。" : body;
            if (current != null && current.entities != null && history.Count < 3 && body != null)
            {
                foreach (AnalysisEntity entity in current.entities)
                {
                    if (entity.start >= 0 && entity.end > entity.start && entity.end <= body.Length &&
                        body.Substring(entity.start, entity.end - entity.start) == entity.text)
                        explanation.Links.Add(entity.start, entity.end - entity.start, entity.text);
                }
            }
            backButton.Enabled = history.Count > 0;
        }

        public void SetEntries(List<CaptionEntry> entries)
        {
            if (entries.Count == 0)
            {
                lookupVersion++;
                taskVersion++;
                history.Clear();
                current = null;
                explanation.Links.Clear();
                explanation.Text = "";
                backButton.Enabled = false;
            }
            StringBuilder builder = new StringBuilder();
            foreach (CaptionEntry entry in entries)
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append('[');
                builder.Append(entry.timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                builder.Append("] ");
                builder.Append(entry.text);
            }
            string updated = builder.ToString();
            copyButton.Enabled = clearButton.Enabled = exportButton.Enabled = updated.Length > 0;
            if (updated == transcript.Text) return;
            transcript.Text = updated;
            transcript.SelectionStart = transcript.TextLength;
            transcript.ScrollToCaret();
            copyButton.Enabled = transcript.TextLength > 0;
            clearButton.Enabled = transcript.TextLength > 0;
            exportButton.Enabled = transcript.TextLength > 0;
        }
    }

    internal sealed class MessageForm : Form
    {
        public event Action<int> HotkeyPressed;

        public MessageForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            Opacity = 0;
            IntPtr ignored = Handle;
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmHotkey && HotkeyPressed != null)
                HotkeyPressed(message.WParam.ToInt32());
            base.WndProc(ref message);
        }
    }

    internal static class TermColor
    {
        // FNV-1a over normalized UTF-16, also used by the browser adapter.
        public static Color ForTerm(string term)
        {
            string key = (term ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            uint hash = 2166136261;
            foreach (char c in key)
                if (!Char.IsWhiteSpace(c)) hash = unchecked((hash ^ c) * 16777619);
            // Warm + green + purple colors; reserve the blue hue range for schedules.
            double hue = hash % 240;
            if (hue >= 150) hue += 90;
            double x = 150 * (1 - Math.Abs((hue / 60) % 2 - 1));
            int hi = 235, lo = 85, mid = 85 + (int)Math.Floor(x + 0.5);
            if (hue < 60) return Color.FromArgb(hi, mid, lo);
            if (hue < 120) return Color.FromArgb(mid, hi, lo);
            if (hue < 180) return Color.FromArgb(lo, hi, mid);
            if (hue < 240) return Color.FromArgb(lo, mid, hi);
            if (hue < 300) return Color.FromArgb(mid, lo, hi);
            return Color.FromArgb(hi, lo, mid);
        }
    }

    internal sealed class HighlightForm : Form
    {
        public bool CaptureExcluded { get; private set; }
        public bool IsHighlightVisible { get { return Visible && clipVisible; } }
        private bool clipVisible = true;
        private Rectangle? appliedClip;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int build;
            string value = Convert.ToString(Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "0"));
            CaptureExcluded = Int32.TryParse(value, out build) && build >= 19041 &&
                NativeMethods.SetWindowDisplayAffinity(Handle, 0x11);
        }
        public void ClipTo(Rectangle viewport)
        {
            Rectangle clip = Rectangle.Intersect(Bounds, viewport);
            clipVisible = clip.Width > 0 && clip.Height > 0;
            if (!clipVisible) { appliedClip = null; Hide(); return; }
            if (clip == Bounds)
            {
                ClearClip();
                return;
            }
            clip.Offset(-Left, -Top);
            if (appliedClip.HasValue && appliedClip.Value == clip)
                return;
            Region old = Region;
            Region = new Region(clip);
            appliedClip = clip;
            if (old != null) old.Dispose();
        }
        public void ClearClip()
        {
            clipVisible = true;
            if (Region == null) { appliedClip = null; return; }
            Region old = Region; Region = null;
            appliedClip = null;
            if (old != null) old.Dispose();
        }
        private HighlightItem item = new HighlightItem();
        private Color borderColor = Color.FromArgb(240, 128, 24);
        private readonly ContextMenuStrip termMenu;
        private readonly ToolStripMenuItem ignoreItem;
        public event Action<HighlightForm, HighlightItem> ItemClicked;
        public event Action<HighlightForm, string> TermIgnored;

        public HighlightForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 184, 48);
            Opacity = 0.34;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            termMenu = new ContextMenuStrip();
            ignoreItem = new ToolStripMenuItem();
            ignoreItem.Click += delegate
            {
                if (TermIgnored != null)
                    TermIgnored(this, item.term);
            };
            termMenu.Items.Add(ignoreItem);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExToolWindow |
                                      NativeMethods.WsExNoActivate;
                return parameters;
            }
        }

        public void SetHighlightBounds(Rectangle bounds, HighlightItem value)
        {
            string previousTerm = item == null ? String.Empty : item.term;
            string previousKind = item == null ? String.Empty : item.kind;
            Size previousSize = Size;
            item = value ?? new HighlightItem();
            bool isTask = String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase);
            BackColor = isTask ? Color.FromArgb(82, 148, 255) : TermColor.ForTerm(item.term);
            borderColor = isTask ? Color.FromArgb(38, 94, 210) : ControlPaint.Dark(BackColor);
            if (!Visible)
                Bounds = bounds;
            if (!String.Equals(previousTerm, item.term, StringComparison.Ordinal) ||
                !String.Equals(previousKind, item.kind, StringComparison.Ordinal) ||
                previousSize != bounds.Size)
                Invalidate();
        }

        public void ShowInactive()
        {
            if (!clipVisible) return;
            if (!Visible)
                NativeMethods.SetWindowPos(
                    Handle,
                    NativeMethods.HwndTopMost,
                    Left,
                    Top,
                    Width,
                    Height,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(borderColor, 2))
                args.Graphics.DrawRectangle(border, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button == MouseButtons.Left && ItemClicked != null)
                ItemClicked(this, item);
            else if (args.Button == MouseButtons.Right &&
                     !String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase))
            {
                ignoreItem.Text = "不再标注“" + item.term + "”";
                termMenu.Show(Cursor.Position);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                termMenu.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class ScrollProbeResult
    {
        internal ScrollFrame Next;
        internal bool Confident;
        internal int Delta;
    }

    // Bounded vertical translation tracker. It measures pixels, never wheel units.
    internal sealed class ScrollFrame
    {
        internal const int Columns = 144;
        internal readonly byte[] Pixels;
        internal readonly int Height;
        internal ScrollFrame(byte[] pixels, int height) { Pixels = pixels; Height = height; }
        internal static ScrollFrame Capture(NativeRect rect)
        {
            // One broad copy keeps left/right chat bubbles in view. Matching below
            // evaluates three lanes without paying for three CopyFromScreen calls.
            int sourceWidth = Math.Min(rect.Width, Math.Min(1200, Math.Max(480, rect.Width * 3 / 4)));
            int sourceX = rect.Left + (rect.Width - sourceWidth) / 2;
            using (Bitmap strip = new Bitmap(sourceWidth, rect.Height, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(strip))
            {
                g.CopyFromScreen(sourceX, rect.Top, 0, 0,
                    new Size(sourceWidth, rect.Height), CopyPixelOperation.SourceCopy);
                return FromBitmap(strip);
            }
        }
        internal static ScrollFrame FromBitmap(Bitmap full)
        {
            using (Bitmap small = new Bitmap(Columns, full.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.Low;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g.DrawImage(full, 0, 0, Columns, full.Height);
                }
                return FromSample(small);
            }
        }
        private static ScrollFrame FromSample(Bitmap small)
        {
                BitmapData bits = small.LockBits(new Rectangle(0, 0, small.Width, small.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] raw = new byte[bits.Stride * small.Height];
                    Marshal.Copy(bits.Scan0, raw, 0, raw.Length);
                    byte[] pixels = new byte[Columns * small.Height];
                    for (int y = 0; y < small.Height; y++)
                        for (int x = 0; x < Columns; x++)
                        {
                            int p = y * bits.Stride + x * 4;
                            pixels[y * Columns + x] = (byte)((raw[p] * 11 + raw[p + 1] * 59 + raw[p + 2] * 30) / 100);
                        }
                    return new ScrollFrame(pixels, small.Height);
                }
                finally { small.UnlockBits(bits); }
        }
        internal bool TryDisplacement(ScrollFrame next, out int delta)
        {
            delta = 0;
            if (next == null || next.Height != Height) return false;
            // Identical repetitive text is stationary, not an ambiguous scroll.
            bool same = true;
            for (int i = 0; i < Pixels.Length; i++)
                if (Pixels[i] != next.Pixels[i]) { same = false; break; }
            if (same) return true;
            List<MotionVote> votes = new List<MotionVote>();
            MotionVote vote;
            if (TryRange(next, 2, Columns - 2, out vote)) votes.Add(vote);
            int lane = Columns / 3;
            if (TryRange(next, 2, lane + 8, out vote)) votes.Add(vote);
            if (TryRange(next, lane - 8, lane * 2 + 8, out vote)) votes.Add(vote);
            if (TryRange(next, lane * 2 - 8, Columns - 2, out vote)) votes.Add(vote);
            if (votes.Count < 2) return false;

            int bestCount = 0;
            double bestConfidence = Double.MinValue;
            int bestDelta = 0;
            foreach (MotionVote seed in votes)
            {
                int count = 0;
                double confidence = 0;
                double weightedDelta = 0;
                foreach (MotionVote candidate in votes)
                {
                    if (Math.Abs(candidate.Delta - seed.Delta) > 2) continue;
                    count++;
                    confidence += candidate.Confidence;
                    weightedDelta += candidate.Delta * candidate.Confidence;
                }
                if (count > bestCount || (count == bestCount && confidence > bestConfidence))
                {
                    bestCount = count;
                    bestConfidence = confidence;
                    bestDelta = (int)Math.Round(weightedDelta / Math.Max(0.01, confidence));
                }
            }
            if (bestCount < 2) return false;
            delta = bestDelta;
            return true;
        }

        private bool TryRange(ScrollFrame next, int xStart, int xEnd, out MotionVote vote)
        {
            vote = new MotionVote();
            List<int> anchors = new List<int>();
            xStart = Math.Max(2, xStart);
            xEnd = Math.Min(Columns - 2, xEnd);
            for (int y = 6; y < Height - 6; y += 3)
                for (int x = xStart; x < xEnd; x += 2)
                {
                    int p = y * Columns + x;
                    if (Math.Abs(Pixels[p] - Pixels[p - 1]) > 22) anchors.Add(p);
                }
            if (anchors.Count < 24) return false;
            int step = Math.Max(1, anchors.Count / 900);
            int range = Math.Min(240, Height / 3);
            double best = Double.MaxValue;
            int bestDelta = 0;
            double[] scores = new double[range * 2 + 1];
            for (int shift = -range; shift <= range; shift++)
            {
                long difference = 0; int count = 0;
                for (int i = 0; i < anchors.Count; i += step)
                {
                    int p = anchors[i], q = p + shift * Columns;
                    if (q < Columns || q >= next.Pixels.Length - Columns) continue;
                    difference += Math.Abs(Pixels[p] - next.Pixels[q]); count++;
                }
                double score = count < Math.Max(30, anchors.Count / step / 2)
                    ? Double.MaxValue : (double)difference / count;
                scores[shift + range] = score;
                if (score < best) { best = score; bestDelta = shift; }
            }
            if (best > 18) return false;
            double runner = Double.MaxValue;
            for (int shift = -range; shift <= range; shift++)
                if (Math.Abs(shift - bestDelta) > 3)
                    runner = Math.Min(runner, scores[shift + range]);
            if (runner - best < 3) return false;
            vote.Delta = bestDelta;
            vote.Confidence = Math.Min(20, runner - best) * Math.Min(2.0, anchors.Count / 120.0);
            return true;
        }

        private sealed class MotionVote
        {
            internal int Delta;
            internal double Confidence;
        }
    }

    internal sealed class CalendarResponse
    {
        public string user_code { get; set; }
        public string check_token { get; set; }
        public string event_id { get; set; }
        public string web_url { get; set; }
        public string start { get; set; }
        public string end { get; set; }
        public string utc_offset { get; set; }
        public string state { get; set; }
        public List<string> conflicts { get; set; }
        public List<string> duplicates { get; set; }
        public List<string> missing { get; set; }
        public bool ok { get; set; }
        public string error { get; set; }
        public string ics { get; set; }
        public string filename { get; set; }
        public string message { get; set; }
    }

    internal sealed class OutlookCalendarForm : Form
    {
        private static string lastClientId = ""; // Public ID only, remembered for this process.
        private readonly Dictionary<string, object> draft;
        private readonly Func<Dictionary<string, object>, CalendarResponse> request;
        private readonly TextBox clientId = new TextBox { Width = 480, Text = lastClientId };
        private readonly TextBox status = new TextBox { Multiline = true, ReadOnly = true, Width = 480, Height = 130,
            ScrollBars = ScrollBars.Vertical, Text = "首次使用请填写应用 ID 并连接。\r\n如果本次运行已登录，可直接检查默认日历。\r\n注册方法见安装目录中的 OUTLOOK_SETUP.md。" };
        private readonly Button login = new Button { Text = "连接微软日历", AutoSize = true };
        private readonly Button check = new Button { Text = "检查默认日历冲突", AutoSize = true };
        private readonly Button create = new Button { Text = "确认写入微软日历", AutoSize = true, Enabled = false };
        private readonly Button disconnect = new Button { Text = "断开日历连接", AutoSize = true };
        private readonly Button open = new Button { Text = "打开日历中的事件", AutoSize = true, Enabled = false };
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 5000 };
        private string ticket;
        private string eventUrl;
        private bool busy;

        public OutlookCalendarForm(Dictionary<string, object> value, Func<Dictionary<string, object>, CalendarResponse> sender)
        {
            draft = new Dictionary<string, object>(value);
            request = sender;
            Text = "微软日历：检查并创建";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(18) };
            Controls.Add(layout);
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(480, 0), Text =
                value["title"] + "\r\n" + value["start"] + " 至 " + value["end"] + " (UTC" + value["utc_offset"] + ")\r\n" +
                "读取并写入登录账户的默认日历；不发送邀请。\r\n登录仅本次程序运行有效，退出后需重新登录。" });
            layout.Controls.Add(new Label { AutoSize = true, Text = "首次使用：填写自己注册的应用程序（客户端）ID" });
            layout.Controls.Add(clientId);
            layout.Controls.Add(login);
            layout.Controls.Add(status);
            layout.Controls.Add(check);
            layout.Controls.Add(create);
            layout.Controls.Add(open);
            layout.Controls.Add(disconnect);
            login.Click += async delegate {
                if (busy) return;
                poll.Stop(); ticket = null; create.Enabled = false;
                var result = await Send("outlook_login");
                if (IsDisposed || result == null || !result.ok) return;
                lastClientId = clientId.Text.Trim();
                poll.Start();
                try { Process.Start(new ProcessStartInfo("https://microsoft.com/devicelogin") { UseShellExecute = true }); }
                catch { status.AppendText("\r\n请在浏览器打开 https://microsoft.com/devicelogin"); }
            };
            poll.Tick += async delegate {
                if (busy) return;
                var result = await Send("outlook_poll");
                if (IsDisposed) return;
                if (result == null || !result.ok || result.state != "pending") poll.Stop();
            };
            check.Click += async delegate {
                if (busy) return;
                ticket = null; create.Enabled = false;
                var result = await Send("outlook_check");
                if (IsDisposed || result == null || !result.ok) return;
                ticket = (result.state == "clear" || result.state == "conflict") ? result.check_token : null;
                create.Enabled = !String.IsNullOrEmpty(ticket);
            };
            create.Click += async delegate {
                if (busy || String.IsNullOrEmpty(ticket)) return;
                if (MessageBox.Show(this, status.Text + "\r\n\r\n" + draft["title"] + "\r\n" +
                    draft["start"] + " 至 " + draft["end"] + " (UTC" + draft["utc_offset"] + ")\r\n确认写入上面显示的微软默认日历？",
                    "确认创建日程", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var result = await Send("outlook_create");
                if (IsDisposed) return;
                if (result != null && result.ok && !String.IsNullOrEmpty(result.event_id)) {
                    ticket = null; create.Enabled = false;
                    eventUrl = result.web_url;
                    open.Enabled = SafeEventUrl(eventUrl);
                }
            };
            disconnect.Click += async delegate {
                if (busy) return;
                poll.Stop(); ticket = null; create.Enabled = false; open.Enabled = false;
                await Send("outlook_disconnect");
            };
            open.Click += delegate {
                if (!SafeEventUrl(eventUrl)) return;
                try { Process.Start(new ProcessStartInfo(eventUrl) { UseShellExecute = true }); }
                catch { status.AppendText("\r\n浏览器未能打开，请在 Outlook 日历中查看。"); }
            };
            FormClosing += delegate(object source, FormClosingEventArgs args) { if (busy) args.Cancel = true; };
            FormClosed += delegate { poll.Stop(); poll.Dispose(); };
        }

        private static bool SafeEventUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
                (uri.Host == "outlook.live.com" || uri.Host == "outlook.office.com" || uri.Host == "outlook.office365.com");
        }

        private async Task<CalendarResponse> Send(string operation)
        {
            busy = true;
            login.Enabled = check.Enabled = disconnect.Enabled = clientId.Enabled = create.Enabled = false;
            var payload = new Dictionary<string, object>(draft);
            payload["operation"] = operation;
            payload["client_id"] = clientId.Text.Trim();
            payload["check_token"] = ticket;
            payload["confirmed"] = operation == "outlook_create";
            try {
                var result = await Task.Factory.StartNew(() => request(payload));
                if (IsDisposed) return null;
                // Keep the user code visible while device login is pending.
                if (result == null) status.Text = "未收到结果，请重试。";
                else if (!result.ok) status.Text = result.error;
                else if (operation != "outlook_poll" || result.state != "pending") {
                    status.Text = result.message;
                    if (result.conflicts != null && result.conflicts.Count > 0)
                        status.AppendText("\r\n冲突：" + String.Join("、", result.conflicts.ToArray()));
                }
                return result;
            }
            catch { if (!IsDisposed) status.Text = "日历请求未完成；如正在创建，请重试同一份日程以确认结果。"; return null; }
            finally {
                busy = false;
                if (!IsDisposed) {
                    login.Enabled = check.Enabled = disconnect.Enabled = clientId.Enabled = true;
                    create.Enabled = !String.IsNullOrEmpty(ticket);
                }
            }
        }
    }

    internal sealed class CalendarForm : Form
    {
        private readonly TextBox title = new TextBox();
        private readonly TextBox start = new TextBox();
        private readonly TextBox end = new TextBox();
        private readonly TextBox offset = new TextBox();
        private readonly Label status = new Label();
        private readonly Button export = new Button();
        private readonly Button check = new Button();
        private readonly Button import = new Button();
        private string calendarIcs;
        private bool checkedDraft;
        private bool busy;

        public CalendarForm(HighlightItem item, Func<Dictionary<string, object>, CalendarResponse> exporter,
            Func<LocalReminderRequest, LocalReminderResult> reminderCreator, Action showReminders,
            Action<LocalReminderResult> reminderCreated)
        {
            Text = "确认并设置提醒";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Microsoft YaHei UI", 10f);
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.AutoSize = true;
            layout.ColumnCount = 1;
            layout.Padding = new Padding(20);
            layout.Dock = DockStyle.Fill;
            Controls.Add(layout);
            Label original = new Label();
            original.AutoSize = true;
            original.MaximumSize = new Size(480, 0);
            original.Text = "发现一个安排：" + (item.time_text ?? item.term) +
                "\r\n核对下面的信息后，即可创建本地提醒。不会自动写入日历。";
            layout.Controls.Add(original);
            TextBox supplement = new TextBox();
            supplement.Width = 470;
            supplement.MaxLength = 1000;
            AddField(layout, "补充说明（例：2026年，持续1小时，北京时间）", supplement);
            Button applySupplement = new Button { Text = "整理补充信息", AutoSize = true };
            layout.Controls.Add(applySupplement);
            applySupplement.Click += async delegate
            {
                if (busy) return;
                busy = true;
                checkedDraft = false;
                export.Enabled = false;
                applySupplement.Enabled = check.Enabled = import.Enabled = false;
                foreach (TextBox field in new[] { title, start, end, offset, supplement }) field.Enabled = false;
                try
                {
                    var payload = new Dictionary<string, object> {
                        { "operation", "clarify" }, { "time_text", item.time_text ?? item.term ?? "" },
                        { "supplement", supplement.Text.Trim() }
                    };
                    CalendarResponse response = await Task.Factory.StartNew(() => exporter(payload));
                    if (response == null || !response.ok)
                    {
                        status.Text = response == null ? "整理失败，请手动填写或重试。" : response.error;
                        return;
                    }
                    start.Text = response.start ?? "";
                    end.Text = response.end ?? "";
                    offset.Text = response.utc_offset ?? "";
                    status.ForeColor = response.missing != null && response.missing.Count > 0
                        ? Color.FromArgb(180, 90, 25) : Color.FromArgb(48, 90, 150);
                    status.Text = response.message;
                }
                catch (Exception) { status.Text = "整理失败，请手动填写或检查服务连接。"; }
                finally
                {
                    busy = false;
                    applySupplement.Enabled = check.Enabled = import.Enabled = true;
                    foreach (TextBox field in new[] { title, start, end, offset, supplement }) field.Enabled = true;
                }
            };
            title.Text = item.title ?? "";
            start.Text = item.start_iso ?? "";
            end.Text = item.end_iso ?? "";
            offset.Text = item.utc_offset ?? "";
            title.AccessibleName = "Reminder title";
            start.AccessibleName = "Reminder start";
            end.AccessibleName = "Reminder end";
            offset.AccessibleName = "Reminder UTC offset";
            title.MaxLength = 120;
            AddField(layout, "事项", title);
            AddField(layout, "开始（例：2026-09-20T14:00）", start);
            AddField(layout, "结束（例：2026-09-20T15:00）", end);
            AddField(layout, "事件当日 UTC 时差（中国填 +08:00）", offset);
            ComboBox reminderLead = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            reminderLead.Items.AddRange(new object[] { "准时提醒", "提前 5 分钟", "提前 10 分钟", "提前 30 分钟", "提前 60 分钟" });
            reminderLead.SelectedIndex = 2;
            AddField(layout, "本地通知时间", reminderLead);
            FlowLayoutPanel primaryActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            Button reminderButton = new Button { Text = "确认创建本地提醒", AutoSize = true };
            Button viewReminders = new Button { Text = "查看全部提醒", AutoSize = true, Visible = false };
            Button done = new Button { Text = "完成", AutoSize = true, Visible = false };
            reminderButton.AccessibleName = "Create local reminder";
            viewReminders.AccessibleName = "View local reminders";
            done.AccessibleName = "Close reminder confirmation";
            primaryActions.Controls.Add(reminderButton);
            primaryActions.Controls.Add(viewReminders);
            primaryActions.Controls.Add(done);
            layout.Controls.Add(primaryActions);
            viewReminders.Click += delegate { if (showReminders != null) showReminders(); };
            done.Click += delegate { Close(); };
            reminderButton.Click += delegate {
                if (busy || !PromptForMissingFields()) return;
                int[] leads = { 0, 5, 10, 30, 60 };
                LocalReminderResult result = reminderCreator(new LocalReminderRequest {
                    title = title.Text.Trim(), start = start.Text.Trim(), end = end.Text.Trim(),
                    utc_offset = offset.Text.Trim(), lead_minutes = leads[Math.Max(0, reminderLead.SelectedIndex)]
                });
                if (result == null || !result.ok)
                {
                    status.ForeColor = Color.FromArgb(180, 45, 45);
                    status.Text = result == null ? "本地提醒创建失败。" : result.error;
                    return;
                }
                status.ForeColor = Color.FromArgb(25, 120, 70);
                status.Text = "✓ 提醒已创建\r\n" + title.Text.Trim() + "\r\n" +
                    start.Text.Trim().Replace('T', ' ') + "（" + reminderLead.SelectedItem + "）\r\n" +
                    "实时字典在托盘运行时会准时通知；重启后记录仍会保留。";
                reminderButton.Enabled = false;
                applySupplement.Enabled = false;
                reminderLead.Enabled = false;
                foreach (TextBox field in new[] { title, start, end, offset, supplement }) field.Enabled = false;
                viewReminders.Visible = true;
                done.Visible = true;
                if (reminderCreated != null) reminderCreated(result);
            };
            status.AutoSize = true;
            status.MaximumSize = new Size(480, 0);
            layout.Controls.Add(status);
            Button moreButton = new Button { Text = "其他方式：Outlook / 日历文件 ▾", AutoSize = true,
                FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(105, 112, 124),
                Margin = new Padding(3, 16, 3, 3) };
            TableLayoutPanel optionalLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1,
                Visible = false, Padding = new Padding(12, 2, 0, 0) };
            layout.Controls.Add(moreButton);
            layout.Controls.Add(optionalLayout);
            moreButton.Click += delegate {
                optionalLayout.Visible = !optionalLayout.Visible;
                moreButton.Text = optionalLayout.Visible
                    ? "其他方式：Outlook / 日历文件 ▴"
                    : "其他方式：Outlook / 日历文件 ▾";
            };
            Button outlookButton = new Button { Text = "可选：写入 Outlook / Microsoft 365 日历…", AutoSize = true };
            optionalLayout.Controls.Add(outlookButton);
            outlookButton.Click += delegate {
                if (busy || !PromptForMissingFields()) return;
                var payload = new Dictionary<string, object> {
                    { "title", title.Text.Trim() }, { "start", start.Text.Trim() },
                    { "end", end.Text.Trim() }, { "utc_offset", offset.Text.Trim() }
                };
                using (var dialog = new OutlookCalendarForm(payload, exporter)) dialog.ShowDialog(this);
            };
            Label info = new Label();
            info.Text = "需要日历文件时，可导入已有日历快照检查冲突，再确认导出。\r\n目前支持 UTC 固定事件；重复日程或其他时间格式需人工核对。";
            info.AutoSize = true;
            optionalLayout.Controls.Add(info);
            import.Text = "导入已有日历 (.ics)…";
            import.AutoSize = true;
            optionalLayout.Controls.Add(import);
            import.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Filter = "日历文件 (*.ics)|*.ics";
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    checkedDraft = false;
                    calendarIcs = null;
                    export.Enabled = false;
                    try
                    {
                        if (new FileInfo(dialog.FileName).Length > 1024 * 1024)
                            throw new InvalidOperationException("日历文件不能超过 1 MiB。");
                        calendarIcs = File.ReadAllText(dialog.FileName, new UTF8Encoding(false, true));
                        checkedDraft = false;
                        export.Enabled = false;
                        status.Text = "日历已加载到内存，请补齐时间后点击检查。";
                    }
                    catch (Exception error) { status.Text = "导入失败：" + error.Message; }
                }
            };
            check.Text = "检查信息和时间冲突";
            check.AutoSize = true;
            optionalLayout.Controls.Add(check);
            check.Click += async delegate
            {
                if (busy) return;
                if (!PromptForMissingFields()) return;
                checkedDraft = false;
                export.Enabled = false;
                busy = true;
                check.Enabled = import.Enabled = false;
                foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = false;
                status.Text = "正在检查导入的日历…";
                try
                {
                    var payload = new Dictionary<string, object> {
                        { "operation", "check" }, { "title", title.Text.Trim() },
                        { "start", start.Text.Trim() }, { "end", end.Text.Trim() },
                        { "utc_offset", offset.Text.Trim() }, { "calendar_ics", calendarIcs }
                    };
                    CalendarResponse result = await Task.Factory.StartNew(() => exporter(payload));
                    if (result == null || !result.ok)
                    {
                        status.Text = result == null ? "检查失败，请重试。" : result.error;
                        return;
                    }
                    status.Text = result.message;
                    if (result.conflicts != null && result.conflicts.Count > 0)
                        status.Text += "\r\n冲突：" + String.Join("、", result.conflicts.ToArray());
                    checkedDraft = result.state == "clear" || result.state == "conflict";
                    export.Enabled = checkedDraft;
                }
                catch (Exception) { status.Text = "检查失败，请确认本地服务正在运行并重试。"; }
                finally
                {
                    busy = false;
                    check.Enabled = import.Enabled = true;
                    foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = true;
                }
            };
            export.Text = "确认并导出日历文件…";
            export.AutoSize = true;
            export.Enabled = false;
            optionalLayout.Controls.Add(export);
            foreach (TextBox field in new[] { title, start, end, offset })
                field.TextChanged += delegate { if (!busy) { checkedDraft = false; export.Enabled = false; ShowMissingFields(); } };
            Shown += delegate { ShowMissingFields(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (busy) args.Cancel = true;
            };
            export.Click += async delegate
            {
                if (busy) return;
                if (!checkedDraft) return;
                if (MessageBox.Show(this, status.Text + "\r\n\r\n" + title.Text + "\r\n" +
                        start.Text + " 至 " + end.Text + " (UTC" + offset.Text + ")\r\n确认导出此日程？",
                        "确认日程", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                busy = true;
                check.Enabled = import.Enabled = false;
                export.Enabled = false;
                status.Text = "正在校验日程…";
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["title"] = title.Text.Trim();
                payload["start"] = start.Text.Trim();
                payload["end"] = end.Text.Trim();
                payload["utc_offset"] = offset.Text.Trim();
                payload["confirmed"] = true;
                foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = false;
                bool saved = false;
                try
                {
                    CalendarResponse response = await Task.Factory.StartNew(() => exporter(payload));
                    if (response == null || !response.ok)
                    {
                        status.Text = response == null ? "服务没有返回结果，请重试。" : response.error;
                        return;
                    }
                    using (SaveFileDialog dialog = new SaveFileDialog())
                    {
                        dialog.Filter = "日历文件 (*.ics)|*.ics";
                        dialog.FileName = response.filename;
                        dialog.DefaultExt = "ics";
                        dialog.OverwritePrompt = true;
                        if (dialog.ShowDialog(this) != DialogResult.OK)
                        {
                            status.Text = "已取消保存，未生成文件。";
                            return;
                        }
                        File.WriteAllText(dialog.FileName, response.ics, new UTF8Encoding(false));
                        saved = true;
                        status.Text = "文件已保存，请在日历软件中导入。\r\n重复导入的处理取决于日历软件。";
                    }
                }
                catch (Exception)
                {
                    status.Text = "导出失败，请检查实时字典是否启动及保存位置是否可写。";
                }
                finally
                {
                    busy = false;
                    check.Enabled = import.Enabled = true;
                    export.Enabled = !saved;
                    foreach (TextBox field in new[] { title, start, end, offset }) field.Enabled = true;
                }
            };
        }

        private bool PromptForMissingFields()
        {
            return UpdateMissingFields(true);
        }

        private bool UpdateMissingFields(bool focusFirst)
        {
            var fields = new[] { title, start, end, offset };
            var names = new[] { "事项", "完整开始时间", "完整结束时间", "UTC 时差" };
            var missing = new List<string>();
            TextBox first = null;
            for (int index = 0; index < fields.Length; index++)
            {
                if (String.IsNullOrWhiteSpace(fields[index].Text))
                {
                    missing.Add(names[index]);
                    if (first == null) first = fields[index];
                }
            }
            if (missing.Count > 0)
            {
                status.ForeColor = Color.FromArgb(180, 90, 25);
                status.Text = "还缺少：" + String.Join("、", missing.ToArray()) +
                    "。\r\n可直接填写，或在“补充说明”中补充后点“整理补充信息”。";
                if (focusFirst) first.Focus();
                return false;
            }
            return true;
        }

        private void ShowMissingFields()
        {
            if (UpdateMissingFields(false))
            {
                status.ForeColor = Color.FromArgb(48, 90, 150);
                status.Text = "信息已完整。点击“确认创建本地提醒”后才会保存。";
            }
        }

        private static void AddField(TableLayoutPanel layout, string label, Control field)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
            field.Width = 470;
            layout.Controls.Add(field);
        }
    }

    internal sealed class DefinitionForm : Form
    {
        private readonly Label titleLabel;
        private readonly LinkLabel bodyLabel;
        private readonly Button closeButton;
        private readonly Button backButton;
        private readonly Button retryButton;
        private readonly Button copyButton;
        private readonly Button editTaskButton;
        private HighlightItem taskCandidate;
        public event Action<HighlightItem> EditTaskRequested;
        private readonly LinkLabel sourceLink;
        private string currentExplanation;
        public event Action Dismissed;
        public event Action<string> TermClicked;
        public event Action BackRequested;
        public event Action RetryRequested;

        public DefinitionForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.White;
            ClientSize = new Size(390, 150);
            DoubleBuffered = true;

            titleLabel = new Label();
            titleLabel.Location = new Point(18, 14);
            titleLabel.Size = new Size(322, 28);
            titleLabel.ForeColor = Color.FromArgb(32, 78, 180);
            titleLabel.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11.0f, FontStyle.Bold);
            titleLabel.AutoEllipsis = true;
            Controls.Add(titleLabel);

            backButton = new Button();
            backButton.Text = "‹";
            backButton.AccessibleName = "Back to previous definition";
            backButton.FlatStyle = FlatStyle.Flat;
            backButton.FlatAppearance.BorderSize = 0;
            backButton.BackColor = Color.White;
            backButton.ForeColor = Color.FromArgb(105, 112, 124);
            backButton.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 14.0f, FontStyle.Regular);
            backButton.Location = new Point(302, 7);
            backButton.Size = new Size(38, 32);
            backButton.TabStop = false;
            backButton.Visible = false;
            backButton.Click += delegate
            {
                if (BackRequested != null)
                    BackRequested();
            };
            Controls.Add(backButton);

            closeButton = new Button();
            closeButton.Text = "×";
            closeButton.AccessibleName = "Close definition";
            closeButton.FlatStyle = FlatStyle.Flat;
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.BackColor = Color.White;
            closeButton.ForeColor = Color.FromArgb(105, 112, 124);
            closeButton.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12.0f, FontStyle.Regular);
            closeButton.Location = new Point(348, 7);
            closeButton.Size = new Size(34, 32);
            closeButton.TabStop = false;
            closeButton.Click += delegate
            {
                if (Dismissed != null)
                    Dismissed();
            };
            Controls.Add(closeButton);

            bodyLabel = new LinkLabel();
            bodyLabel.Location = new Point(18, 52);
            bodyLabel.Size = new Size(354, 80);
            bodyLabel.ForeColor = Color.FromArgb(48, 52, 60);
            bodyLabel.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10.0f, FontStyle.Regular);
            bodyLabel.LinkColor = Color.FromArgb(42, 107, 218);
            bodyLabel.ActiveLinkColor = Color.FromArgb(20, 77, 173);
            bodyLabel.VisitedLinkColor = Color.FromArgb(42, 107, 218);
            bodyLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            bodyLabel.AutoSize = false;
            bodyLabel.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                if (TermClicked != null)
                {
                    string linkedText = args.Link.LinkData as string;
                    if (string.IsNullOrEmpty(linkedText) && args.Link.Start >= 0 &&
                        args.Link.Start + args.Link.Length <= bodyLabel.Text.Length)
                        linkedText = bodyLabel.Text.Substring(args.Link.Start, args.Link.Length);
                    TermClicked(linkedText ?? string.Empty);
                }
            };
            Controls.Add(bodyLabel);

            sourceLink = new LinkLabel();
            sourceLink.Text = "查看来源";
            sourceLink.AutoSize = true;
            sourceLink.LinkColor = Color.FromArgb(42, 107, 218);
            sourceLink.Visible = false;
            sourceLink.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                string url = args.Link.LinkData as string;
                Uri parsed;
                if (Uri.TryCreate(url, UriKind.Absolute, out parsed) &&
                    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    ProcessStartInfo info = new ProcessStartInfo();
                    info.FileName = parsed.AbsoluteUri;
                    info.UseShellExecute = true;
                    Process.Start(info);
                }
            };
            Controls.Add(sourceLink);

            retryButton = new Button();
            retryButton.Text = "换个解释";
            retryButton.FlatStyle = FlatStyle.Flat;
            retryButton.FlatAppearance.BorderColor = Color.FromArgb(218, 222, 230);
            retryButton.BackColor = Color.White;
            retryButton.Size = new Size(104, 30);
            retryButton.Visible = false;
            retryButton.Click += delegate
            {
                retryButton.Enabled = false;
                retryButton.Text = "正在重写…";
                if (RetryRequested != null)
                    RetryRequested();
            };
            Controls.Add(retryButton);

            copyButton = new Button();
            copyButton.Text = "复制解释";
            copyButton.FlatStyle = FlatStyle.Flat;
            copyButton.FlatAppearance.BorderColor = Color.FromArgb(218, 222, 230);
            copyButton.BackColor = Color.White;
            copyButton.Size = new Size(108, 30);
            copyButton.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(currentExplanation))
                    return;
                try
                {
                    Clipboard.SetText(currentExplanation);
                    copyButton.Text = "已复制";
                }
                catch
                {
                    copyButton.Text = "复制失败";
                }
            };
            Controls.Add(copyButton);
            editTaskButton = new Button();
            editTaskButton.Text = "补全并设置提醒…";
            editTaskButton.AutoSize = true;
            editTaskButton.Visible = false;
            editTaskButton.Click += delegate
            {
                if (taskCandidate != null && EditTaskRequested != null)
                    EditTaskRequested(taskCandidate);
            };
            Controls.Add(editTaskButton);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
                parameters.ClassStyle |= NativeMethods.CsDropShadow;
                return parameters;
            }
        }

        public void ShowLoading(string term, Rectangle anchor, bool canGoBack, bool refresh)
        {
            SetContent(
                term,
                refresh ? "正在换一种解释…" : "正在查询…",
                null,
                null,
                anchor,
                canGoBack,
                false);
        }

        public void ShowDefinition(
            string term,
            string explanation,
            List<AnalysisEntity> entities,
            List<string> sources,
            Rectangle anchor,
            bool canGoBack,
            bool canRefresh)
        {
            SetContent(term, explanation, entities, sources, anchor, canGoBack, canRefresh);
        }

        public void ShowTaskCandidate(HighlightItem item, Rectangle anchor)
        {
            string timeText = String.IsNullOrWhiteSpace(item.time_text) ? item.term : item.time_text;
            while (timeText.Contains(": ") || timeText.Contains("： "))
                timeText = timeText.Replace(": ", ":").Replace("： ", "：");
            string title = String.IsNullOrWhiteSpace(item.title) ? "待确认事项" : item.title;
            string explanation =
                "时间：" + timeText + "\r\n" +
                "事项：" + title + "\r\n" +
                "状态：等待确认，尚未写入任何日历。";
            SetContent("发现一个安排", explanation, null, null, anchor, false, false);
            taskCandidate = item;
            editTaskButton.Location = new Point(18, copyButton.Top);
            editTaskButton.Visible = true;
            copyButton.Text = "复制日程";
        }

        private void SetContent(
            string term,
            string explanation,
            List<AnalysisEntity> entities,
            List<string> sources,
            Rectangle anchor,
            bool canGoBack,
            bool canRefresh)
        {
            titleLabel.Text = term ?? string.Empty;
            taskCandidate = null;
            editTaskButton.Visible = false;
            bodyLabel.Text = explanation ?? string.Empty;
            currentExplanation = bodyLabel.Text;
            copyButton.Text = "复制解释";
            retryButton.Text = "换个解释";
            retryButton.Enabled = true;
            retryButton.Visible = canRefresh;
            bodyLabel.Links.Clear();
            if (entities != null)
            {
                int occupiedUntil = -1;
                foreach (AnalysisEntity entity in entities)
                {
                    if (entity == null || string.IsNullOrEmpty(entity.text) ||
                        entity.start < 0 || entity.end <= entity.start ||
                        entity.end > bodyLabel.Text.Length || entity.start < occupiedUntil ||
                        !string.Equals(
                            bodyLabel.Text.Substring(entity.start, entity.end - entity.start),
                            entity.text,
                            StringComparison.Ordinal))
                        continue;
                    bodyLabel.Links.Add(entity.start, entity.end - entity.start, entity.text);
                    occupiedUntil = entity.end;
                }
            }
            backButton.Visible = canGoBack;
            Size measured = TextRenderer.MeasureText(
                bodyLabel.Text,
                bodyLabel.Font,
                new Size(bodyLabel.Width, 230),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int bodyHeight = Math.Max(48, Math.Min(230, measured.Height + 6));
            bodyLabel.Height = bodyHeight;
            string sourceUrl = null;
            if (sources != null)
            {
                foreach (string candidate in sources)
                {
                    Uri parsed;
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out parsed) &&
                        (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                    {
                        sourceUrl = parsed.AbsoluteUri;
                        break;
                    }
                }
            }
            int footerTop = bodyLabel.Top + bodyHeight + 8;
            sourceLink.Location = new Point(18, footerTop + 5);
            sourceLink.Visible = sourceUrl != null;
            sourceLink.Links.Clear();
            if (sourceUrl != null)
                sourceLink.Links.Add(0, sourceLink.Text.Length, sourceUrl);
            retryButton.Location = new Point(150, footerTop);
            copyButton.Location = new Point(264, footerTop);
            ClientSize = new Size(390, footerTop + copyButton.Height + 12);
            PositionFor(anchor);
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopMost,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            Invalidate();
        }

        public void PositionFor(Rectangle anchor)
        {
            Rectangle working = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left;
            int y = anchor.Bottom + 9;
            if (x + Width > working.Right - 8)
                x = working.Right - Width - 8;
            if (x < working.Left + 8)
                x = working.Left + 8;
            if (y + Height > working.Bottom - 8)
                y = anchor.Top - Height - 9;
            if (y < working.Top + 8)
                y = working.Top + 8;
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(Color.FromArgb(218, 222, 230), 1))
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class ApiKeyForm : Form
    {
        private readonly ComboBox providerBox;
        private readonly TextBox baseUrlBox;
        private readonly TextBox modelBox;
        private readonly TextBox keyBox;
        private readonly Button testButton;
        private readonly Button saveButton;
        private readonly Label statusLabel;
        private readonly Func<string, string, string, KeyValidationResult> validator;
        private string validatedSignature;

        public string ApiKey
        {
            get { return keyBox.Text; }
        }

        public string BaseUrl
        {
            get { return baseUrlBox.Text.Trim(); }
        }

        public string Model
        {
            get { return modelBox.Text.Trim(); }
        }

        public ApiKeyForm(Func<string, string, string, KeyValidationResult> validator)
        {
            this.validator = validator;
            Text = "配置模型服务";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(520, 360);
            Font = SystemFonts.MessageBoxFont;

            Label title = new Label();
            title.Text = "配置你自己的模型服务和 API Key";
            title.AutoSize = true;
            title.Font = new Font(Font, FontStyle.Bold);
            title.Location = new Point(18, 18);
            Controls.Add(title);

            Label note = new Label();
            note.Text = "密钥只保存在当前 Windows 用户目录，不会写进安装包。";
            note.AutoSize = true;
            note.ForeColor = Color.FromArgb(90, 96, 106);
            note.Location = new Point(18, 48);
            Controls.Add(note);

            Label providerLabel = new Label();
            providerLabel.Text = "服务商";
            providerLabel.AutoSize = true;
            providerLabel.Location = new Point(21, 84);
            Controls.Add(providerLabel);

            providerBox = new ComboBox();
            providerBox.DropDownStyle = ComboBoxStyle.DropDownList;
            providerBox.Items.Add("硅基流动（推荐）");
            providerBox.Items.Add("DeepSeek 官方");
            providerBox.Items.Add("其他 OpenAI 兼容服务");
            providerBox.Location = new Point(92, 79);
            providerBox.Size = new Size(407, 27);
            Controls.Add(providerBox);

            Label baseUrlLabel = new Label();
            baseUrlLabel.Text = "接口地址";
            baseUrlLabel.AutoSize = true;
            baseUrlLabel.Location = new Point(21, 123);
            Controls.Add(baseUrlLabel);

            baseUrlBox = new TextBox();
            baseUrlBox.Location = new Point(92, 118);
            baseUrlBox.Size = new Size(407, 26);
            Controls.Add(baseUrlBox);

            Label modelLabel = new Label();
            modelLabel.Text = "模型名称";
            modelLabel.AutoSize = true;
            modelLabel.Location = new Point(21, 161);
            Controls.Add(modelLabel);

            modelBox = new TextBox();
            modelBox.Location = new Point(92, 156);
            modelBox.Size = new Size(407, 26);
            Controls.Add(modelBox);

            Label keyLabel = new Label();
            keyLabel.Text = "API Key";
            keyLabel.AutoSize = true;
            keyLabel.Location = new Point(21, 201);
            Controls.Add(keyLabel);

            keyBox = new TextBox();
            keyBox.Location = new Point(92, 196);
            keyBox.Size = new Size(407, 26);
            keyBox.UseSystemPasswordChar = true;
            Controls.Add(keyBox);

            statusLabel = new Label();
            statusLabel.Text = "请先测试连接";
            statusLabel.AutoEllipsis = true;
            statusLabel.Location = new Point(21, 236);
            statusLabel.Size = new Size(478, 42);
            statusLabel.ForeColor = Color.FromArgb(90, 96, 106);
            Controls.Add(statusLabel);

            testButton = new Button();
            testButton.Text = "测试连接";
            testButton.Location = new Point(243, 304);
            testButton.Size = new Size(88, 32);
            testButton.Click += TestConnection;
            Controls.Add(testButton);

            saveButton = new Button();
            saveButton.Text = "保存并应用";
            saveButton.DialogResult = DialogResult.OK;
            saveButton.Enabled = false;
            saveButton.Location = new Point(339, 304);
            saveButton.Size = new Size(88, 32);
            Controls.Add(saveButton);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(435, 304);
            cancel.Size = new Size(64, 32);
            Controls.Add(cancel);

            AcceptButton = testButton;
            CancelButton = cancel;
            providerBox.SelectedIndexChanged += delegate
            {
                if (providerBox.SelectedIndex == 0)
                    baseUrlBox.Text = "https://api.siliconflow.cn/v1";
                else if (providerBox.SelectedIndex == 1)
                    baseUrlBox.Text = "https://api.deepseek.com";
                baseUrlBox.ReadOnly = providerBox.SelectedIndex != 2;
                if (providerBox.SelectedIndex == 0)
                    modelBox.Text = "deepseek-ai/DeepSeek-V4-Flash";
                else if (providerBox.SelectedIndex == 1)
                    modelBox.Text = "deepseek-v4-flash";
                InvalidateValidation();
            };
            keyBox.TextChanged += delegate { InvalidateValidation(); };
            baseUrlBox.TextChanged += delegate { InvalidateValidation(); };
            modelBox.TextChanged += delegate { InvalidateValidation(); };
            providerBox.SelectedIndex = 0;
            Shown += delegate { keyBox.Focus(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (DialogResult == DialogResult.OK &&
                    !String.Equals(validatedSignature, CurrentSignature(), StringComparison.Ordinal))
                {
                    args.Cancel = true;
                    MessageBox.Show(
                        this,
                        "请先测试当前服务商、模型和 API Key。",
                        "实时字典",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    keyBox.Focus();
                }
            };
        }

        private string CurrentSignature()
        {
            return keyBox.Text.Trim() + "\n" + BaseUrl + "\n" + Model;
        }

        private void InvalidateValidation()
        {
            validatedSignature = null;
            if (saveButton != null)
                saveButton.Enabled = false;
            if (statusLabel != null)
            {
                statusLabel.Text = "请先测试连接";
                statusLabel.ForeColor = Color.FromArgb(90, 96, 106);
            }
        }

        private void TestConnection(object sender, EventArgs args)
        {
            string key = keyBox.Text.Trim();
            if (key.Length < 8)
            {
                statusLabel.Text = "请输入有效的 API Key";
                statusLabel.ForeColor = Color.Firebrick;
                return;
            }
            if (BaseUrl.Length == 0 || Model.Length == 0)
            {
                statusLabel.Text = "接口地址和模型名称不能为空";
                statusLabel.ForeColor = Color.Firebrick;
                return;
            }
            testButton.Enabled = false;
            saveButton.Enabled = false;
            providerBox.Enabled = false;
            baseUrlBox.Enabled = false;
            modelBox.Enabled = false;
            keyBox.Enabled = false;
            statusLabel.Text = "正在连接模型服务…";
            statusLabel.ForeColor = Color.FromArgb(90, 96, 106);
            string baseUrl = BaseUrl;
            string model = Model;
            Task.Factory.StartNew(delegate { return validator(key, baseUrl, model); })
                .ContinueWith(delegate(Task<KeyValidationResult> task)
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            testButton.Enabled = true;
                            providerBox.Enabled = true;
                            baseUrlBox.Enabled = true;
                            modelBox.Enabled = true;
                            keyBox.Enabled = true;
                            if (task.IsFaulted || task.Result == null || !task.Result.ok)
                            {
                                validatedSignature = null;
                                statusLabel.Text = task.IsFaulted
                                    ? "连接失败：" + task.Exception.GetBaseException().Message
                                    : task.Result.message;
                                statusLabel.ForeColor = Color.Firebrick;
                                keyBox.Focus();
                                return;
                            }
                            validatedSignature = key + "\n" + baseUrl + "\n" + model;
                            saveButton.Enabled = true;
                            AcceptButton = saveButton;
                            statusLabel.Text = task.Result.message;
                            statusLabel.ForeColor = Color.FromArgb(25, 125, 70);
                        }));
                    }
                    catch
                    {
                    }
                });
        }
    }

    internal sealed class ScanRegionForm : Form
    {
        public RectangleF SelectedRegion { get; private set; }
        internal static NativeRect MapRegion(RectangleF region, NativeRect window)
        {
            return new NativeRect {
                Left = window.Left + (int)Math.Round(region.Left * window.Width),
                Top = window.Top + (int)Math.Round(region.Top * window.Height),
                Right = window.Left + (int)Math.Round(region.Right * window.Width),
                Bottom = window.Top + (int)Math.Round(region.Bottom * window.Height)
            };
        }
        public ScanRegionForm(Bitmap screenshot)
        {
            Text = "框选聊天内容 · 鼠标拖动选区";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            double scale = Math.Min(1.0, Math.Min(Math.Min(900, area.Width - 100) / (double)screenshot.Width,
                Math.Min(560, area.Height - 180) / (double)screenshot.Height));
            int width = Math.Max(200, (int)(screenshot.Width * scale));
            int height = Math.Max(120, (int)(screenshot.Height * scale));
            ClientSize = new Size(width, height + 85);
            var picture = new PictureBox { Image = screenshot, SizeMode = PictureBoxSizeMode.StretchImage,
                Location = Point.Empty, Size = new Size(width, height), Cursor = Cursors.Cross };
            var notice = new Label { Text = "拖出只包含消息正文的区域。选区仅本次运行有效；排版变化后可重新框选。",
                Location = new Point(8, height + 4), Size = new Size(width - 16, 32) };
            var confirm = new Button { Text = "使用选区", Enabled = false,
                Location = new Point(width - 190, height + 44), Size = new Size(90, 30) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel,
                Location = new Point(width - 94, height + 44), Size = new Size(86, 30) };
            Controls.Add(picture); Controls.Add(notice); Controls.Add(confirm); Controls.Add(cancel);
            CancelButton = cancel;
            bool dragging = false;
            Point origin = Point.Empty;
            Rectangle selection = Rectangle.Empty;
            picture.MouseDown += delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                origin = e.Location; dragging = true; picture.Capture = true;
                confirm.Enabled = false; selection = Rectangle.Empty;
            };
            picture.MouseMove += delegate(object sender, MouseEventArgs e) {
                if (!dragging) return;
                int x = Math.Max(0, Math.Min(width, e.X)), y = Math.Max(0, Math.Min(height, e.Y));
                selection = Rectangle.FromLTRB(Math.Min(origin.X, x), Math.Min(origin.Y, y),
                    Math.Max(origin.X, x), Math.Max(origin.Y, y));
                picture.Invalidate();
            };
            picture.MouseUp += delegate {
                if (!dragging) return;
                dragging = false; picture.Capture = false;
                confirm.Enabled = selection.Width / scale >= 100 && selection.Height / scale >= 50;
            };
            picture.Paint += delegate(object sender, PaintEventArgs e) {
                if (selection.Width < 1 || selection.Height < 1) return;
                using (var brush = new SolidBrush(Color.FromArgb(40, 50, 110, 240))) e.Graphics.FillRectangle(brush, selection);
                using (var pen = new Pen(Color.RoyalBlue, 2)) e.Graphics.DrawRectangle(pen, selection);
            };
            confirm.Click += delegate {
                SelectedRegion = new RectangleF(selection.X / (float)width, selection.Y / (float)height,
                    selection.Width / (float)width, selection.Height / (float)height);
                DialogResult = DialogResult.OK; Close();
            };
        }
    }

    internal sealed class StatusForm : Form
    {
        private readonly Label label;
        private readonly System.Windows.Forms.Timer dismissTimer;

        public StatusForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 248, 218);
            ClientSize = new Size(104, 32);
            DoubleBuffered = true;

            label = new Label();
            label.Text = "正在读取文字…";
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Dock = DockStyle.Fill;
            label.ForeColor = Color.FromArgb(92, 67, 10);
            label.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.0f, FontStyle.Regular);
            Controls.Add(label);

            dismissTimer = new System.Windows.Forms.Timer();
            dismissTimer.Tick += delegate
            {
                dismissTimer.Stop();
                Hide();
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WsExTransparent |
                                      NativeMethods.WsExToolWindow |
                                      NativeMethods.WsExNoActivate;
                return parameters;
            }
        }

        public void PositionFor(NativeRect target)
        {
            Rectangle targetBounds = new Rectangle(
                target.Left, target.Top, target.Width, target.Height);
            Rectangle working = Screen.FromRectangle(targetBounds).WorkingArea;
            int desiredX = target.Right - Width - 16;
            int desiredY = target.Top + 16;
            Location = new Point(
                Math.Max(working.Left + 8, Math.Min(desiredX, working.Right - Width - 8)),
                Math.Max(working.Top + 8, Math.Min(desiredY, working.Bottom - Height - 8)));
        }

        public void ShowScanning(NativeRect target)
        {
            dismissTimer.Stop();
            label.Text = "正在读取文字…";
            PositionFor(target);
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopMost,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        public void ShowMessage(string message, NativeRect target, int milliseconds)
        {
            label.Text = message ?? string.Empty;
            PositionFor(target);
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopMost,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            dismissTimer.Interval = Math.Max(250, milliseconds);
            dismissTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(Color.FromArgb(210, 170, 58), 1))
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                dismissTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class SelectionActionForm : Form
    {
        internal event Action ExplainRequested;
        internal string SelectedText { get; private set; }
        internal Rectangle SelectedRegion { get; private set; }
        internal DateTime ExpiresUtc { get; private set; }
        internal SelectionActionForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            // Form.TopMost activates during CreateHandle on .NET Framework.
            // Apply topmost only through SetWindowPos with SWP_NOACTIVATE.
            StartPosition = FormStartPosition.Manual;
            Size = new Size(126, 36); BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10); Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.SetWindowDisplayAffinity(Handle, 0x11);
        }
        protected override CreateParams CreateParams
        {
            get {
                var value = base.CreateParams;
                value.ExStyle |= NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
                value.ClassStyle |= NativeMethods.CsDropShadow;
                return value;
            }
        }
        internal static Rectangle DragRegion(Point start, Point end, Rectangle window)
        {
            // Drag endpoints are only an approximation; OCR text must be reviewed.
            if (Math.Abs(start.Y - end.Y) > 80 || Math.Abs(start.X - end.X) < 8 ||
                Math.Abs(start.X - end.X) > 1000) return Rectangle.Empty;
            Rectangle region = Rectangle.FromLTRB(Math.Min(start.X, end.X) - 3,
                Math.Min(start.Y, end.Y) - 18, Math.Max(start.X, end.X) + 3,
                Math.Max(start.Y, end.Y) + 18);
            region.Intersect(window);
            return region.Width < 8 || region.Height < 10 ? Rectangle.Empty : region;
        }
        internal void Present(string text, Point endpoint, Rectangle region)
        {
            SelectedText = text; SelectedRegion = region;
            ExpiresUtc = DateTime.UtcNow.AddSeconds(8);
            Rectangle area = Screen.FromPoint(endpoint).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(endpoint.X, area.Right - Width)),
                endpoint.Y + 22 + Height <= area.Bottom ? endpoint.Y + 22 : Math.Max(area.Top, endpoint.Y - Height - 22));
            NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost, Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawRectangle(Pens.LightGray, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, String.IsNullOrWhiteSpace(SelectedText) ? "识别查词" : "AI 解释",
                Font, new Rectangle(8, 0, 91, Height), Color.FromArgb(30, 60, 140),
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, "×", Font, new Rectangle(100, 0, 25, Height), Color.Gray,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= 100) Hide();
            else if (ExplainRequested != null) ExplainRequested();
        }
    }

    internal sealed class ManualLookupForm : Form
    {
        private readonly ServiceManager services;
        private readonly TextBox query = new TextBox { MaxLength = 200, Dock = DockStyle.Top };
        private readonly TextBox body = new TextBox { Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Button lookup = new Button { Text = "解释", Dock = DockStyle.Bottom, Height = 34 };
        private readonly Label notice = new Label { Text = "选中文字后按 Ctrl+Alt+D；也可以在这里输入或粘贴词语。",
            Dock = DockStyle.Top, Height = 48 };
        private int generation;

        // Show the waiting state immediately without stealing focus from the
        // source application. UI Automation still needs that application's
        // focused element in order to read the current selection.
        protected override bool ShowWithoutActivation { get { return true; } }

        internal ManualLookupForm(ServiceManager service)
        {
            services = service;
            Text = "主动查词"; Size = new Size(440, 310); MinimumSize = Size;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true;
            Controls.Add(body); Controls.Add(query); Controls.Add(notice); Controls.Add(lookup);
            AcceptButton = lookup;
            lookup.Click += async delegate
            {
                string term = query.Text.Trim();
                if (term.Length == 0) { notice.Text = "请输入或粘贴需要解释的词语。"; return; }
                int request = ++generation;
                body.Text = "正在查询…"; lookup.Enabled = false;
                services.Log("Manual lookup submitted (" + term.Length + " chars)");
                try
                {
                    LookupResponse response = await Task.Factory.StartNew(delegate {
                        LookupResponse local;
                        if (ShortcutLookup.TryExplain(term, String.Empty, out local)) return local;
                        services.EnsureRunning();
                        return services.Lookup(term, String.Empty, false, null);
                    });
                    if (IsDisposed || request != generation) return;
                    body.Text = response == null || String.IsNullOrWhiteSpace(response.explanation)
                        ? "暂时没有可靠解释，请稍后重试。" : response.explanation;
                    services.NoteTermClicked(term);
                    services.Log("Manual lookup completed");
                }
                catch (Exception error)
                {
                    services.Log("Manual lookup failed: " + error.GetType().Name);
                    if (!IsDisposed && request == generation) body.Text = "查询失败，请稍后重试。";
                }
                finally { if (!IsDisposed && request == generation) lookup.Enabled = true; }
            };
        }

        internal async void Open(bool readSelection)
        {
            int request = ++generation;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            lookup.Enabled = true;
            query.Text = String.Empty;
            body.Text = String.Empty;

            if (!readSelection)
            {
                notice.Text = "请输入或粘贴需要解释的词语。";
                if (!Visible) Show();
                ActivateForInput();
                services.Log("Manual lookup form opened for typed input");
                return;
            }

            // Give immediate feedback, but do not activate until accessibility has
            // read the source selection. Never touch the clipboard.
            IntPtr target = NativeMethods.GetForegroundWindow();
            notice.Text = "正在读取选中文字…";
            if (!Visible) Show();
            else Invalidate();
            services.Log("Manual lookup form shown; reading selection");

            Task<string> selection = Task.Factory.StartNew<string>(ReadSelection);
            Task completed = await Task.WhenAny(selection, Task.Delay(900));
            if (IsDisposed || request != generation) return;
            string text = completed == selection && !selection.IsFaulted ? selection.Result : String.Empty;
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != target && foreground != Handle) text = String.Empty;
            query.Text = text;
            if (String.IsNullOrWhiteSpace(text))
            {
                notice.Text = "当前软件未提供可读取的选中文字，请在下方输入或粘贴。";
                services.Log(completed == selection
                    ? "Manual lookup selection unavailable; editable fallback shown"
                    : "Manual lookup selection timed out; editable fallback shown");
            }
            else
            {
                notice.Text = "已读取所选文字，正在为你解释。";
                services.Log("Manual lookup selection read (" + text.Length + " chars)");
            }
            ActivateForInput();
            if (!String.IsNullOrWhiteSpace(text)) lookup.PerformClick();
            else query.Focus();
        }

        private void ActivateForInput()
        {
            if (!Visible) Show();
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            BringToFront();
            query.Focus();
        }

        internal void OpenText(string text, bool exactSelection)
        {
            query.Text = (text ?? String.Empty).Length > 200 ? text.Substring(0, 200) : text;
            notice.Text = exactSelection ? "已读取所选文字，正在为你解释。" :
                "这是所选区域的 OCR 结果，请核对或修改后点击解释。";
            Show(); Activate();
            if (exactSelection && !String.IsNullOrWhiteSpace(query.Text)) lookup.PerformClick();
            else query.Focus();
        }

        internal static string ReadSelection()
        {
            try
            {
                var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(
                    RuntimeEnvironment.GetRuntimeDirectory(), "WPF", "UIAutomationClient.dll"));
                Type elementType = assembly.GetType("System.Windows.Automation.AutomationElement");
                Type patternType = assembly.GetType("System.Windows.Automation.TextPattern");
                object element = elementType.GetProperty("FocusedElement").GetValue(null, null);
                if (element == null) return String.Empty;
                object current = elementType.GetProperty("Current").GetValue(element, null);
                if ((bool)current.GetType().GetProperty("IsPassword").GetValue(current, null))
                    return String.Empty;
                object id = patternType.GetField("Pattern").GetValue(null);
                object pattern = elementType.GetMethod("GetCurrentPattern").Invoke(element, new[] { id });
                Array ranges = (Array)patternType.GetMethod("GetSelection").Invoke(pattern, null);
                if (ranges == null || ranges.Length != 1) return String.Empty;
                object range = ranges.GetValue(0);
                string text = (string)range.GetType().GetMethod("GetText").Invoke(range, new object[] { 201 });
                return text != null && text.Trim().Length <= 200 ? text.Trim() : String.Empty;
            }
            catch { return String.Empty; }
        }
    }

    internal sealed class FamiliarityRecord
    {
        public int misses { get; set; }
        public long updated_utc_ticks { get; set; }
    }

    internal sealed class TermFamiliarityStore
    {
        internal const int SuppressionThreshold = 4;
        internal static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromSeconds(3);
        private const int MaximumRecords = 512;

        private readonly string path;
        private bool dirty;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly Dictionary<string, FamiliarityRecord> records =
            new Dictionary<string, FamiliarityRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> clicked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> currentlyVisible =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TimeSpan> visibleDurations =
            new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        private DateTime lastVisibilityUtc;
        private bool sessionActive;

        internal TermFamiliarityStore(string storagePath)
        {
            path = storagePath;
            Load();
        }

        internal int SuppressedCount
        {
            get
            {
                int count = 0;
                foreach (FamiliarityRecord record in records.Values)
                    if (record != null && record.misses >= SuppressionThreshold) count++;
                return count;
            }
        }

        internal static string Normalize(string term)
        {
            string value = (term ?? String.Empty).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            StringBuilder normalized = new StringBuilder(value.Length);
            foreach (char character in value)
                if (!Char.IsWhiteSpace(character)) normalized.Append(character);
            return normalized.ToString();
        }

        internal bool ShouldSuppress(string term)
        {
            FamiliarityRecord record;
            string key = Normalize(term);
            return key.Length > 0 && records.TryGetValue(key, out record) &&
                record != null && record.misses >= SuppressionThreshold;
        }

        internal void BeginSession()
        {
            BeginSession(DateTime.UtcNow);
        }

        internal void BeginSession(DateTime now)
        {
            clicked.Clear();
            currentlyVisible.Clear();
            visibleDurations.Clear();
            lastVisibilityUtc = now;
            sessionActive = true;
        }

        internal void UpdateVisibleTerms(IEnumerable<string> terms)
        {
            UpdateVisibleTerms(terms, DateTime.UtcNow);
        }

        internal void UpdateVisibleTerms(IEnumerable<string> terms, DateTime now)
        {
            if (!sessionActive) return;
            AccumulateVisibleTime(now);
            currentlyVisible.Clear();
            if (terms == null) return;
            foreach (string term in terms)
            {
                string key = Normalize(term);
                if (key.Length > 0) currentlyVisible.Add(key);
            }
        }

        internal void NoteClicked(string term)
        {
            NoteClicked(term, DateTime.UtcNow);
        }

        internal void NoteClicked(string term, DateTime now)
        {
            string key = Normalize(term);
            if (key.Length == 0) return;
            if (sessionActive) AccumulateVisibleTime(now);
            clicked.Add(key);
            if (records.Remove(key)) dirty = true;
        }

        internal void EndSession(bool applyLearning)
        {
            EndSession(applyLearning, DateTime.UtcNow);
        }

        internal void EndSession(bool applyLearning, DateTime now)
        {
            if (!sessionActive) { if (dirty) Save(); return; }
            AccumulateVisibleTime(now);
            bool changed = false;
            if (applyLearning)
            {
                foreach (KeyValuePair<string, TimeSpan> exposure in visibleDurations)
                {
                    if (exposure.Value < MinimumVisibleDuration || clicked.Contains(exposure.Key))
                        continue;
                    FamiliarityRecord record;
                    if (!records.TryGetValue(exposure.Key, out record) || record == null)
                    {
                        record = new FamiliarityRecord();
                        records[exposure.Key] = record;
                    }
                    if (record.misses < SuppressionThreshold) record.misses++;
                    record.updated_utc_ticks = now.Ticks;
                    changed = true;
                }
                if (TrimRecords()) changed = true;
                if (changed || dirty) Save();
            }
            if (dirty) Save();
            clicked.Clear();
            currentlyVisible.Clear();
            visibleDurations.Clear();
            sessionActive = false;
        }

        internal void CancelSession()
        {
            clicked.Clear();
            currentlyVisible.Clear();
            visibleDurations.Clear();
            sessionActive = false;
        }

        internal void Clear()
        {
            records.Clear();
            Save();
        }

        private void AccumulateVisibleTime(DateTime now)
        {
            if (now < lastVisibilityUtc) now = lastVisibilityUtc;
            TimeSpan elapsed = now - lastVisibilityUtc;
            if (elapsed > TimeSpan.Zero)
            {
                foreach (string key in currentlyVisible)
                {
                    TimeSpan duration;
                    visibleDurations.TryGetValue(key, out duration);
                    visibleDurations[key] = duration + elapsed;
                }
            }
            lastVisibilityUtc = now;
        }

        private bool TrimRecords()
        {
            bool changed = false;
            while (records.Count > MaximumRecords)
            {
                string oldestKey = null;
                long oldestTicks = Int64.MaxValue;
                foreach (KeyValuePair<string, FamiliarityRecord> item in records)
                {
                    long ticks = item.Value == null ? 0 : item.Value.updated_utc_ticks;
                    if (ticks < oldestTicks) { oldestTicks = ticks; oldestKey = item.Key; }
                }
                if (oldestKey == null) break;
                records.Remove(oldestKey);
                changed = true;
            }
            return changed;
        }

        private void Load()
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                Dictionary<string, FamiliarityRecord> saved =
                    serializer.Deserialize<Dictionary<string, FamiliarityRecord>>(
                        File.ReadAllText(path, Encoding.UTF8));
                if (saved == null) return;
                foreach (KeyValuePair<string, FamiliarityRecord> item in saved)
                {
                    string key = Normalize(item.Key);
                    if (key.Length > 0 && item.Value != null && item.Value.misses > 0)
                        records[key] = item.Value;
                }
                TrimRecords();
            }
            catch { records.Clear(); }
        }

        private void Save()
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, serializer.Serialize(records), new UTF8Encoding(false));
                File.Copy(temporary, path, true);
                File.Delete(temporary);
                dirty = false;
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }
    }

    internal sealed class ServiceManager : IDisposable
    {
        internal static string WorkModeOverrideForDiagnostics { get; set; }
        internal static bool DisableFamiliarityPersistenceForDiagnostics { get; set; }

        private readonly string projectRoot;
        private readonly string logPath;
        private readonly List<Process> ownedProcesses = new List<Process>();
        private readonly object serviceLock = new object();
        private readonly object windowsOcrLock = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly object tokenLock = new object();
        private readonly Dictionary<string, string> preferences =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> ignoredTerms =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly TermFamiliarityStore familiarity;
        private string serviceToken;
        private Process windowsOcrWorker;
        private Task<string> windowsOcrErrorTask;

        public string Difficulty { get; private set; }
        public string AnalysisContextId { get; private set; }
        public string ScanScope { get; private set; }
        public string WorkMode { get; private set; }
        public string CaptionAudioScope { get; private set; }
        public bool AutoLearnFamiliarTerms { get; private set; }
        public bool SelectionToolbarEnabled { get; private set; }
        public int IgnoredTermCount { get { return ignoredTerms.Count; } }
        public int AutoSuppressedTermCount { get { return familiarity.SuppressedCount; } }

        public void OpenBrowserExtensionDirectory()
        {
            string directory = Path.Combine(projectRoot, "browser-extension");
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "explorer.exe";
            info.Arguments = "\"" + directory + "\"";
            info.UseShellExecute = true;
            Process.Start(info);
        }

        public ServiceManager()
        {
            projectRoot = FindProjectRoot();
            logPath = Path.Combine(projectRoot, "_native_host.log");
            LoadPreferences();
            string value;
            Difficulty = preferences.TryGetValue("difficulty", out value) &&
                (value == "concise" || value == "standard" || value == "detailed")
                ? value : "standard";
            ScanScope = preferences.TryGetValue("scan_scope", out value) && value == "full"
                ? "full" : "auto";
            if (WorkModeOverrideForDiagnostics == "caption" ||
                WorkModeOverrideForDiagnostics == "conversation")
                WorkMode = WorkModeOverrideForDiagnostics;
            else
                WorkMode = preferences.TryGetValue("work_mode", out value) && value == "caption"
                    ? "caption" : "conversation";
            CaptionAudioScope = preferences.TryGetValue("caption_audio_scope", out value) && value == "system"
                ? "system" : "process";
            AutoLearnFamiliarTerms = !preferences.TryGetValue("auto_learn_familiar_terms", out value) ||
                !String.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            LoadIgnoredTerms();
            SelectionToolbarEnabled = !preferences.TryGetValue("selection_toolbar", out value) || value != "false";
            familiarity = new TermFamiliarityStore(
                DisableFamiliarityPersistenceForDiagnostics ? null : FamiliarityPath());
            BeginAnalysisContext();
            Task.Factory.StartNew(delegate
            {
                try
                {
                    lock (windowsOcrLock)
                        EnsureWindowsOcrWorker();
                }
                catch (Exception error)
                {
                    Log("Windows OCR worker warmup failed: " + error.Message);
                }
            });
        }

        public void BeginAnalysisContext()
        {
            familiarity.EndSession(AutoLearnFamiliarTerms);
            AnalysisContextId = Guid.NewGuid().ToString("N");
            familiarity.BeginSession();
        }

        public void EndAnalysisContext()
        {
            familiarity.EndSession(AutoLearnFamiliarTerms);
        }

        public void UpdateVisibleTerms(IEnumerable<string> terms)
        {
            familiarity.UpdateVisibleTerms(terms);
        }

        public void NoteTermClicked(string term)
        {
            familiarity.NoteClicked(term);
        }

        public bool ShouldSuppressTerm(string term)
        {
            return AutoLearnFamiliarTerms && familiarity.ShouldSuppress(term);
        }

        public void SetAutoLearnFamiliarTerms(bool enabled)
        {
            if (AutoLearnFamiliarTerms == enabled) return;
            if (!enabled) familiarity.EndSession(false);
            AutoLearnFamiliarTerms = enabled;
            preferences["auto_learn_familiar_terms"] = enabled ? "true" : "false";
            SavePreferences();
            if (enabled) familiarity.BeginSession();
            Log("Local familiarity learning " + (enabled ? "enabled" : "disabled"));
        }

        public void ClearFamiliarTerms()
        {
            familiarity.Clear();
            Log("Cleared local familiarity counters");
        }

        private void LoadPreferences()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RealtimeDictionary",
                    "preferences.json");
                if (!File.Exists(path))
                    return;
                Dictionary<string, string> values =
                    serializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8));
                if (values != null)
                {
                    foreach (KeyValuePair<string, string> item in values)
                        preferences[item.Key] = item.Value;
                }
            }
            catch (Exception error)
            {
                Log("Load preferences failed: " + error.Message);
            }
        }

        public void SetDifficulty(string value)
        {
            if (value != "concise" && value != "standard" && value != "detailed")
                value = "standard";
            Difficulty = value;
            preferences["difficulty"] = value;
            SavePreferences();
            Log("Highlight difficulty changed to " + value);
        }

        public void SetSelectionToolbarEnabled(bool enabled)
        {
            SelectionToolbarEnabled = enabled;
            preferences["selection_toolbar"] = enabled ? "true" : "false";
            SavePreferences();
        }

        public void SetScanScope(string value)
        {
            ScanScope = value == "full" ? "full" : "auto";
            preferences["scan_scope"] = ScanScope;
            SavePreferences();
            Log("Scan scope changed to " + ScanScope);
        }

        public void SetWorkMode(string value)
        {
            WorkMode = value == "caption" ? "caption" : "conversation";
            preferences["work_mode"] = WorkMode;
            SavePreferences();
            Log("Work mode changed to " + WorkMode);
        }

        public void SetCaptionAudioScope(string value)
        {
            CaptionAudioScope = value == "system" ? "system" : "process";
            preferences["caption_audio_scope"] = CaptionAudioScope;
            SavePreferences();
            Log("Caption audio scope changed to " + CaptionAudioScope);
        }

        private void SavePreferences()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "preferences.json"),
                serializer.Serialize(preferences),
                new UTF8Encoding(false));
        }

        private string IgnoredTermsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary",
                "ignored-terms.txt");
        }

        private string FamiliarityPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary",
                "term-familiarity.json");
        }

        private void LoadIgnoredTerms()
        {
            try
            {
                string path = IgnoredTermsPath();
                if (!File.Exists(path))
                    return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string term = line.Trim();
                    if (term.Length > 0)
                        ignoredTerms.Add(term);
                }
            }
            catch (Exception error)
            {
                Log("Load ignored terms failed: " + error.Message);
            }
        }

        public bool IsIgnoredTerm(string term)
        {
            return !String.IsNullOrWhiteSpace(term) && ignoredTerms.Contains(term.Trim());
        }

        public void IgnoreTerm(string term)
        {
            string value = (term ?? String.Empty).Trim();
            if (value.Length == 0 || !ignoredTerms.Add(value))
                return;
            string path = IgnoredTermsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, ignoredTerms, new UTF8Encoding(false));
        }

        public void ClearIgnoredTerms()
        {
            ignoredTerms.Clear();
            string path = IgnoredTermsPath();
            if (File.Exists(path))
                File.WriteAllText(path, String.Empty, new UTF8Encoding(false));
        }

        public void EnsureRunning()
        {
            lock (serviceLock)
            {
                if (!IsHealthy("http://127.0.0.1:8877/health", 350))
                    StartPython("server.py");
                WaitForHealth("http://127.0.0.1:8877/health", 10000);
            }
        }

        public string Capture(NativeRect rect)
        {
            string directory = Path.Combine(Path.GetTempPath(), "RealtimeDictionary");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "scan-" + Guid.NewGuid().ToString("N") + ".png");
            using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    rect.Left,
                    rect.Top,
                    0,
                    0,
                    new Size(rect.Width, rect.Height),
                    CopyPixelOperation.SourceCopy);
                bitmap.Save(path, ImageFormat.Png);
            }
            return path;
        }

        public byte[] ComputeVisualFingerprint(string path)
        {
            using (Bitmap bitmap = new Bitmap(path))
                return ComputeBitmapFingerprint(bitmap);
        }

        public byte[] ComputeScreenFingerprint(NativeRect rect)
        {
            using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                    new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
                return ComputeBitmapFingerprint(bitmap);
            }
        }

        private byte[] ComputeBitmapFingerprint(Bitmap bitmap)
        {
            const int columns = 48;
            const int rows = 27;
            byte[] fingerprint = new byte[columns * rows];
            {
                for (int row = 0; row < rows; row++)
                {
                    int y = Math.Min(bitmap.Height - 1, (row * 2 + 1) * bitmap.Height / (rows * 2));
                    for (int column = 0; column < columns; column++)
                    {
                        int x = Math.Min(bitmap.Width - 1, (column * 2 + 1) * bitmap.Width / (columns * 2));
                        Color color = bitmap.GetPixel(x, y);
                        fingerprint[row * columns + column] = (byte)(
                            (color.R * 30 + color.G * 59 + color.B * 11) / 100);
                    }
                }
            }
            return fingerprint;
        }

        public bool AreVisualFingerprintsEquivalent(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return false;
            int materiallyChanged = 0;
            int allowedChanges = Math.Max(5, first.Length / 100);
            for (int index = 0; index < first.Length; index++)
            {
                if (Math.Abs(first[index] - second[index]) <= 12)
                    continue;
                materiallyChanged++;
                if (materiallyChanged > allowedChanges)
                    return false;
            }
            return true;
        }

        public ScanResponse Scan(NativeRect rect, string capturePath)
        {
            try
            {
                return ScanWithWindowsOcr(capturePath);
            }
            catch (AnalysisRequestException error)
            {
                Log("Term analysis failed after Windows OCR; not retrying OCR: " + error);
                return new ScanResponse { ok = false, error = error.Message };
            }
            catch (Exception error)
            {
                Log("Windows OCR failed; using PaddleOCR fallback: " + error);
                EnsureLegacyOcr();
                return ScanWithLegacyOcr(rect);
            }
        }

        public ScanResponse RefineWords(List<OcrWord> words)
        {
            return AnalyzeWords(words ?? new List<OcrWord>(), "model");
        }

        public void SaveApiKey(string apiKey, string baseUrl, string model)
        {
            string key = (apiKey ?? string.Empty).Trim();
            if (key.Length < 8)
                throw new ArgumentException("API Key 不能为空。", "apiKey");
            string endpoint = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            string modelId = (model ?? string.Empty).Trim();
            Uri endpointUri;
            bool validUri = Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri);
            bool localHttp = validUri && endpointUri.Scheme == Uri.UriSchemeHttp &&
                (endpointUri.Host == "127.0.0.1" || endpointUri.Host == "localhost");
            if (!validUri || (endpointUri.Scheme != Uri.UriSchemeHttps && !localHttp))
                throw new ArgumentException("模型服务地址必须是 HTTPS，或本机地址。", "baseUrl");
            if (modelId.Length == 0 || modelId.Length > 160)
                throw new ArgumentException("模型名称不能为空。", "model");
            string configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary");
            Directory.CreateDirectory(configDir);
            Dictionary<string, string> config = new Dictionary<string, string>();
            config["base_url"] = endpoint;
            config["model"] = modelId;
            config["api_key"] = key;
            File.WriteAllText(
                Path.Combine(configDir, "config.json"),
                serializer.Serialize(config),
                new UTF8Encoding(false));
        }

        public bool TakeFirstRunNotice()
        {
            try
            {
                string stateDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RealtimeDictionary");
                string marker = Path.Combine(stateDir, "onboarding-v1.done");
                if (File.Exists(marker))
                    return false;
                Directory.CreateDirectory(stateDir);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"), new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public KeyValidationResult ValidateApiKey(string apiKey, string baseUrl, string model)
        {
            Dictionary<string, string> payload = new Dictionary<string, string>();
            payload["api_key"] = (apiKey ?? string.Empty).Trim();
            payload["base_url"] = (baseUrl ?? string.Empty).Trim();
            payload["model"] = (model ?? string.Empty).Trim();
            return PostJson<KeyValidationResult>(
                "http://127.0.0.1:8877/validate-key", payload, 12000);
        }

        public KeyValidationResult ValidateCurrentConfiguration()
        {
            return PostJson<KeyValidationResult>(
                "http://127.0.0.1:8877/validate-current",
                new Dictionary<string, string>(),
                12000);
        }

        public ServiceHealth GetServiceStatus()
        {
            return GetJson<ServiceHealth>("http://127.0.0.1:8877/health", false);
        }

        public KeyValidationResult RestartAnalysisService()
        {
            try
            {
                PostJson<OperationResponse>(
                    "http://127.0.0.1:8877/shutdown",
                    new Dictionary<string, string>(),
                    3000);
            }
            catch (Exception error)
            {
                Log("Graceful server shutdown request failed: " + error.Message);
            }
            ResetServiceToken();
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 4000 &&
                   IsHealthy("http://127.0.0.1:8877/health", 250))
                Thread.Sleep(100);
            lock (serviceLock)
            {
                if (IsHealthy("http://127.0.0.1:8877/health", 250))
                    throw new InvalidOperationException("旧分析服务没有按时退出。");
                StartPython("server.py");
                WaitForHealth("http://127.0.0.1:8877/health", 10000);
            }
            ResetServiceToken();
            ServiceHealth health = GetServiceStatus();
            return new KeyValidationResult {
                ok = health != null && health.ok && health.has_key,
                configured = health != null && health.has_key,
                model = health == null ? null : health.model,
                model_available = health != null && health.ok && health.has_key,
                message = health != null && health.ok && health.has_key
                    ? "配置已应用" : "配置未能加载"
            };
        }

        public LookupResponse Lookup(
            string term,
            string context,
            bool refresh,
            string previousExplanation)
        {
            Dictionary<string, string> payload = new Dictionary<string, string>();
            payload["term"] = term;
            payload["context"] = context ?? String.Empty;
            payload["refresh"] = refresh ? "true" : "false";
            payload["previous_explanation"] = refresh ? (previousExplanation ?? String.Empty) : String.Empty;
            byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8877/lookup");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 22000;
                request.ReadWriteTimeout = 22000;
                SetToken(request);
                request.ContentLength = body.Length;
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(body, 0, body.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        LookupResponse result = serializer.Deserialize<LookupResponse>(reader.ReadToEnd());
                        if (result != null) UnicodeSpans.Convert(result.explanation, result.entities);
                        return result;
                    }
                }
                catch (WebException error)
                {
                    if (attempt == 0 && IsForbidden(error))
                    {
                        ResetServiceToken();
                        continue;
                    }
                    throw;
                }
            }
        }

        public bool TryTriggerBrowser()
        {
            BrowserCommand command = PostJson<BrowserCommand>(
                "http://127.0.0.1:8877/browser/trigger", new Dictionary<string, string>());
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1200)
            {
                BrowserAck ack = GetJson<BrowserAck>(
                    "http://127.0.0.1:8877/browser/ack-status?generation=" + command.generation);
                if (ack != null && ack.acked && ack.focused)
                    return true;
                Thread.Sleep(100);
            }
            return false;
        }

        public void ClearBrowser()
        {
            try
            {
                PostJson<BrowserCommand>(
                    "http://127.0.0.1:8877/browser/clear", new Dictionary<string, string>());
            }
            catch (Exception error)
            {
                Log("Browser clear failed: " + error.Message);
            }
        }

        public AnalyzeResponse AnalyzeHistoricalCaption(string text)
        {
            EnsureRunning();
            AnalyzeResponse result = PostJson<AnalyzeResponse>("http://127.0.0.1:8877/analyze",
                new Dictionary<string, string> { { "text", text }, { "mode", "model" },
                    { "difficulty", Difficulty } }, 22000);
            if (result != null)
            {
                UnicodeSpans.Convert(text, result.entities);
                UnicodeSpans.Convert(text, result.actions);
            }
            return result;
        }

        public AudioTranscriptionResponse TranscribeAudio(byte[] wav)
        {
            EnsureRunning();
            Dictionary<string, string> payload = new Dictionary<string, string>();
            payload["audio_base64"] = Convert.ToBase64String(wav);
            return PostJson<AudioTranscriptionResponse>(
                "http://127.0.0.1:8877/transcribe", payload, 30000);
        }

        public CalendarResponse ExportCalendar(Dictionary<string, object> payload)
        {
            EnsureRunning();
            object operation;
            bool checking = payload.TryGetValue("operation", out operation) && (string)operation == "check";
            if (operation is string && ((string)operation).StartsWith("outlook_", StringComparison.Ordinal))
                return PostJson<CalendarResponse>("http://127.0.0.1:8877/calendar/outlook", payload, 90000);
            if (operation as string == "clarify")
                return PostJson<CalendarResponse>("http://127.0.0.1:8877/calendar/clarify", payload);
            return PostJson<CalendarResponse>(checking ? "http://127.0.0.1:8877/calendar/check" :
                "http://127.0.0.1:8877/calendar/export", payload);
        }

        private T PostJson<T>(string url, object payload)
        {
            return PostJson<T>(url, payload, 3000);
        }

        private T PostJson<T>(string url, object payload, int timeout)
        {
            byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = timeout;
                request.ReadWriteTimeout = timeout;
                SetToken(request);
                request.ContentLength = body.Length;
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(body, 0, body.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        return serializer.Deserialize<T>(reader.ReadToEnd());
                }
                catch (WebException error)
                {
                    if (attempt == 0 && IsForbidden(error))
                    {
                        ResetServiceToken();
                        continue;
                    }
                    throw;
                }
            }
        }

        private T GetJson<T>(string url)
        {
            return GetJson<T>(url, true);
        }

        private T GetJson<T>(string url, bool withToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 1000;
                if (withToken)
                    SetToken(request);
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        return serializer.Deserialize<T>(reader.ReadToEnd());
                }
                catch (WebException error)
                {
                    if (attempt == 0 && withToken && IsForbidden(error))
                    {
                        ResetServiceToken();
                        continue;
                    }
                    throw;
                }
            }
        }

        private string GetServiceToken()
        {
            lock (tokenLock)
            {
                if (String.IsNullOrEmpty(serviceToken))
                {
                    try
                    {
                        SessionInfo session = GetJson<SessionInfo>("http://127.0.0.1:8877/session", false);
                        if (session != null && !String.IsNullOrEmpty(session.token))
                            serviceToken = session.token;
                    }
                    catch (Exception error)
                    {
                        Log("Fetch service token failed: " + error.Message);
                    }
                }
                return serviceToken;
            }
        }

        private void ResetServiceToken()
        {
            lock (tokenLock)
            {
                serviceToken = null;
            }
        }

        private void SetToken(HttpWebRequest request)
        {
            string token = GetServiceToken();
            if (!String.IsNullOrEmpty(token))
                request.Headers["X-RealtimeDictionary-Token"] = token;
        }

        private static bool IsForbidden(WebException error)
        {
            HttpWebResponse response = error.Response as HttpWebResponse;
            return response != null && response.StatusCode == HttpStatusCode.Forbidden;
        }

        private ScanResponse ScanWithWindowsOcr(string capturePath)
        {
            Stopwatch totalWatch = Stopwatch.StartNew();
            Stopwatch ocrWatch = Stopwatch.StartNew();
            List<OcrWord> words = ReadWindowsOcrWords(capturePath);
            int ocrMs = (int)ocrWatch.ElapsedMilliseconds;
            Stopwatch analysisWatch = Stopwatch.StartNew();
            ScanResponse result = AnalyzeWords(words, "instant");
            int localAnalysisMs = (int)analysisWatch.ElapsedMilliseconds;
            result.words = words;
            result.ocr_ms = ocrMs;
            result.local_analysis_ms = localAnalysisMs;
            result.duration_ms = (int)totalWatch.ElapsedMilliseconds;
            Log("Windows OCR scan: " + words.Count + " words, " + result.highlights.Count +
                " highlights, OCR " + result.ocr_ms + "ms, local analysis " +
                result.local_analysis_ms + "ms (server " + result.analysis_duration_ms +
                "ms), total " + result.duration_ms + "ms");
            return result;
        }

        public string ReadSelectionRegion(Rectangle region)
        {
            string path = Capture(new NativeRect { Left = region.Left, Top = region.Top,
                Right = region.Right, Bottom = region.Bottom });
            try
            {
                List<OcrWord> words = ReadWindowsOcrWords(path);
                StringBuilder text = new StringBuilder();
                OcrWord previous = null;
                foreach (OcrWord word in words)
                {
                    if (previous != null) text.Append(SeparatorBetweenWords(previous, word));
                    text.Append(word.text); previous = word;
                }
                return text.ToString();
            }
            finally { try { File.Delete(path); } catch { } }
        }

        private List<OcrWord> ReadWindowsOcrWords(string capturePath)
        {
            Exception workerError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    lock (windowsOcrLock)
                    {
                        EnsureWindowsOcrWorker();
                        string requestId = Guid.NewGuid().ToString("N");
                        string request = serializer.Serialize(new Dictionary<string, string> {
                            { "request_id", requestId },
                            { "image_path_base64", Convert.ToBase64String(
                                Encoding.UTF8.GetBytes(Path.GetFullPath(capturePath))) }
                        });
                        windowsOcrWorker.StandardInput.WriteLine(request);
                        windowsOcrWorker.StandardInput.Flush();
                        Task<string> responseTask = Task.Factory.StartNew(
                            delegate { return windowsOcrWorker.StandardOutput.ReadLine(); });
                        if (!responseTask.Wait(15000))
                            throw new TimeoutException("Windows OCR worker timed out.");
                        string output = responseTask.Result;
                        if (String.IsNullOrWhiteSpace(output))
                            throw new InvalidOperationException("Windows OCR worker closed its output stream.");
                        WindowsOcrResponse ocr = serializer.Deserialize<WindowsOcrResponse>(output);
                        if (ocr == null || !ocr.ok || !String.Equals(ocr.request_id, requestId,
                            StringComparison.Ordinal))
                            throw new InvalidOperationException("Windows OCR worker returned an invalid response: " +
                                (ocr == null ? "empty response" : ocr.error));
                        Log("Windows OCR worker: decode " + ocr.decode_ms + "ms, recognize " +
                            ocr.recognize_ms + "ms, worker " + ocr.worker_ms + "ms");
                        return ocr.words ?? new List<OcrWord>();
                    }
                }
                catch (Exception error)
                {
                    workerError = error;
                    lock (windowsOcrLock)
                        StopWindowsOcrWorker();
                }
            }

            Log("Windows OCR worker unavailable; using one-shot bridge: " + workerError.Message);
            return ReadWindowsOcrWordsOneShot(capturePath);
        }

        private void EnsureWindowsOcrWorker()
        {
            if (windowsOcrWorker != null && !windowsOcrWorker.HasExited)
                return;

            StopWindowsOcrWorker();
            string script = Path.Combine(projectRoot, "native-host", "windows_ocr_worker.ps1");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "powershell.exe";
            info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"";
            info.WorkingDirectory = projectRoot;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            Process worker = Process.Start(info);
            windowsOcrWorker = worker;
            if (worker == null)
                throw new InvalidOperationException("Windows OCR worker did not start.");
            windowsOcrErrorTask = Task.Factory.StartNew(
                delegate { return worker.StandardError.ReadToEnd(); });
            Task<string> readyTask = Task.Factory.StartNew(
                delegate { return worker.StandardOutput.ReadLine(); });
            if (!readyTask.Wait(10000))
                throw new TimeoutException("Windows OCR worker warmup timed out.");
            string readyJson = readyTask.Result;
            WindowsOcrResponse ready = String.IsNullOrWhiteSpace(readyJson) ? null :
                serializer.Deserialize<WindowsOcrResponse>(readyJson);
            if (ready == null || !ready.ok || !ready.ready)
                throw new InvalidOperationException("Windows OCR worker failed to initialize.");
            Log("Windows OCR worker ready as PID " + windowsOcrWorker.Id);
        }

        private void StopWindowsOcrWorker()
        {
            Process worker = windowsOcrWorker;
            Task<string> errorTask = windowsOcrErrorTask;
            windowsOcrWorker = null;
            windowsOcrErrorTask = null;
            if (worker == null)
                return;
            try
            {
                if (!worker.HasExited)
                {
                    try { worker.StandardInput.Close(); } catch { }
                    if (!worker.WaitForExit(750))
                        worker.Kill();
                }
                if (errorTask != null && errorTask.IsCompleted)
                {
                    string error = errorTask.Result.Trim();
                    if (!String.IsNullOrEmpty(error))
                        Log("Windows OCR worker stderr: " + error);
                }
            }
            catch { }
            finally { worker.Dispose(); }
        }

        private List<OcrWord> ReadWindowsOcrWordsOneShot(string capturePath)
        {
            string script = Path.Combine(projectRoot, "native-host", "windows_ocr.ps1");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "powershell.exe";
            info.Arguments =
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script +
                "\" -ImagePath \"" + capturePath + "\"";
            info.WorkingDirectory = projectRoot;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            Process process = Process.Start(info);
            if (process == null)
                throw new InvalidOperationException("Windows OCR bridge did not start.");

            Task<string> outputTask = Task.Factory.StartNew(
                delegate { return process.StandardOutput.ReadToEnd(); });
            Task<string> errorTask = Task.Factory.StartNew(
                delegate { return process.StandardError.ReadToEnd(); });
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Windows OCR timed out.");
            }
            string output = outputTask.Result.Trim();
            string error = errorTask.Result.Trim();
            int exitCode = process.ExitCode;
            process.Dispose();
            if (exitCode != 0 || string.IsNullOrEmpty(output))
                throw new InvalidOperationException(
                    "Windows OCR bridge failed: " + (string.IsNullOrEmpty(error) ? output : error));

            WindowsOcrResponse ocr = serializer.Deserialize<WindowsOcrResponse>(output);
            if (ocr == null || !ocr.ok)
                throw new InvalidOperationException("Windows OCR returned an invalid response.");
            return ocr.words ?? new List<OcrWord>();
        }

        private ScanResponse AnalyzeWords(List<OcrWord> words, string mode)
        {
            StringBuilder fullText = new StringBuilder();
            List<int> starts = new List<int>();
            OcrWord previous = null;
            foreach (OcrWord word in words)
            {
                if (previous != null)
                    fullText.Append(SeparatorBetweenWords(previous, word));
                starts.Add(fullText.Length);
                fullText.Append(word.text ?? string.Empty);
                previous = word;
            }

            string sourceText = fullText.ToString();
            if (String.IsNullOrWhiteSpace(sourceText))
            {
                Log("OCR frame contained no readable text; skipped term analysis");
                return new ScanResponse {
                    ok = true,
                    highlights = new List<HighlightItem>(),
                    words = words,
                    analysis_mode = "local"
                };
            }
            Dictionary<string, string> analyzePayload = new Dictionary<string, string>();
            analyzePayload["text"] = sourceText;
            analyzePayload["mode"] = mode;
            analyzePayload["difficulty"] = Difficulty;
            analyzePayload["context_id"] = AnalysisContextId;
            byte[] analyzeBody = Encoding.UTF8.GetBytes(serializer.Serialize(analyzePayload));
            AnalyzeResponse analysis = null;
            Exception analyzeError = null;
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8877/analyze");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                bool localOnly = mode == "local" || mode == "instant";
                request.Timeout = localOnly ? 5000 : 22000;
                request.ReadWriteTimeout = localOnly ? 5000 : 22000;
                SetToken(request);
                request.ContentLength = analyzeBody.Length;
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(analyzeBody, 0, analyzeBody.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        analysis = serializer.Deserialize<AnalyzeResponse>(reader.ReadToEnd());
                    analyzeError = null;
                    break;
                }
                catch (WebException error)
                {
                    if (attempt == 0 && IsForbidden(error))
                    {
                        ResetServiceToken();
                        continue;
                    }
                    analyzeError = error;
                    break;
                }
                catch (Exception error)
                {
                    analyzeError = error;
                    break;
                }
            }
            if (analyzeError != null)
            {
                throw new AnalysisRequestException("Term analysis timed out or returned an invalid response.", analyzeError);
            }

            List<HighlightItem> highlights = new List<HighlightItem>();
            HashSet<string> seen = new HashSet<string>();
            List<AnalysisEntity> renderEntities = new List<AnalysisEntity>();
            if (analysis != null)
            {
                UnicodeSpans.Convert(sourceText, analysis.entities);
                UnicodeSpans.Convert(sourceText, analysis.actions);
                if (analysis.entities != null)
                    renderEntities.AddRange(analysis.entities);
                if (analysis.actions != null)
                    renderEntities.AddRange(analysis.actions);
            }
            if (renderEntities.Count > 0)
            {
                foreach (AnalysisEntity entity in renderEntities)
                {
                    bool isTask = String.Equals(
                        entity.type, "calendar_event", StringComparison.OrdinalIgnoreCase);
                    if (!isTask && IsIgnoredTerm(entity.text))
                        continue;
                    if (entity.start < 0 || entity.end <= entity.start)
                        continue;
                    int contextStart = Math.Max(0, entity.start - 90);
                    int contextEnd = Math.Min(sourceText.Length, entity.end + 90);
                    string entityContext = sourceText.Substring(
                        contextStart, Math.Max(0, contextEnd - contextStart));
                    List<HighlightItem> fragments = new List<HighlightItem>();
                    for (int index = 0; index < words.Count; index++)
                    {
                        OcrWord word = words[index];
                        string text = word.text ?? string.Empty;
                        int spanStart = starts[index];
                        int spanEnd = spanStart + text.Length;
                        int overlapStart = Math.Max(entity.start, spanStart);
                        int overlapEnd = Math.Min(entity.end, spanEnd);
                        if (overlapStart >= overlapEnd || text.Length == 0)
                            continue;

                        double left = (overlapStart - spanStart) / (double)text.Length;
                        double right = (overlapEnd - spanStart) / (double)text.Length;
                        HighlightItem item = new HighlightItem();
                        item.term = entity.text;
                        item.context = entityContext;
                        item.kind = isTask ? "task" : "concept";
                        item.title = entity.title;
                        item.time_text = entity.time_text;
                        item.start_iso = entity.start_iso;
                        item.end_iso = entity.end_iso;
                        item.utc_offset = entity.utc_offset;
                        item.needs_confirmation = isTask || entity.needs_confirmation;
                        item.x = (int)Math.Round(word.x + word.w * left) - 1;
                        item.y = (int)Math.Round(word.y) - 1;
                        item.w = Math.Max(3, (int)Math.Round(word.w * (right - left)) + 2);
                        item.h = Math.Max(3, (int)Math.Round(word.h) + 2);
                        string signature =
                            item.kind + "|" + item.term + "|" + item.x + "|" + item.y + "|" + item.w + "|" + item.h;
                        if (seen.Add(signature))
                            fragments.Add(item);
                    }

                    HighlightItem merged = null;
                    foreach (HighlightItem fragment in fragments)
                    {
                        if (merged != null &&
                            Math.Abs(fragment.y - merged.y) <= Math.Max(fragment.h, merged.h) / 2 &&
                            fragment.x <= merged.x + merged.w + 8)
                        {
                            int right = Math.Max(merged.x + merged.w, fragment.x + fragment.w);
                            int bottom = Math.Max(merged.y + merged.h, fragment.y + fragment.h);
                            merged.x = Math.Min(merged.x, fragment.x);
                            merged.y = Math.Min(merged.y, fragment.y);
                            merged.w = right - merged.x;
                            merged.h = bottom - merged.y;
                        }
                        else
                        {
                            merged = fragment;
                            highlights.Add(merged);
                        }
                    }
                }
            }
            return new ScanResponse
            {
                ok = true,
                highlights = highlights,
                analysis_mode = analysis == null ? null : analysis.analysis_mode,
                analysis_duration_ms = analysis == null ? 0 : analysis.analysis_duration_ms
            };
        }

        private static string SeparatorBetweenWords(OcrWord previous, OcrWord current)
        {
            string left = previous.text ?? string.Empty;
            string right = current.text ?? string.Empty;
            if (left.Length == 0 || right.Length == 0)
                return String.Empty;
            double previousBottom = previous.y + previous.h;
            double currentBottom = current.y + current.h;
            double verticalGap = Math.Max(previous.y, current.y) -
                                 Math.Min(previousBottom, currentBottom);
            double lineHeight = Math.Max(1.0, Math.Min(previous.h, current.h));
            if (verticalGap > lineHeight * 0.55)
                return "\n";
            bool leftWordLike = ContainsLatinOrDigit(left);
            bool rightWordLike = ContainsLatinOrDigit(right);
            return leftWordLike && rightWordLike ? " " : String.Empty;
        }

        private static bool ContainsLatinOrDigit(string value)
        {
            foreach (char character in value)
            {
                if ((character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9'))
                    return true;
            }
            return false;
        }

        private void EnsureLegacyOcr()
        {
            lock (serviceLock)
            {
                if (!IsHealthy("http://127.0.0.1:8878/health", 350))
                    StartPython("ocr_service.py");
                WaitForHealth("http://127.0.0.1:8878/health", 30000);
            }
        }

        private ScanResponse ScanWithLegacyOcr(NativeRect rect)
        {
            Dictionary<string, int> payload = new Dictionary<string, int>();
            payload["x"] = rect.Left;
            payload["y"] = rect.Top;
            payload["w"] = rect.Width;
            payload["h"] = rect.Height;
            string json = serializer.Serialize(payload);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8878/scan");
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Timeout = 150000;
            byte[] body = Encoding.UTF8.GetBytes(json);
            request.ContentLength = body.Length;
            using (Stream requestStream = request.GetRequestStream())
                requestStream.Write(body, 0, body.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return serializer.Deserialize<ScanResponse>(reader.ReadToEnd());
        }

        public void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            try { File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

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
                Process probe = Process.Start(new ProcessStartInfo
                {
                    FileName = onPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (probe != null)
                {
                    probe.WaitForExit(1500);
                    if (probe.ExitCode == 0)
                        return onPath;
                    probe.Dispose();
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

    internal sealed class AnalysisRequestException : Exception
    {
        public AnalysisRequestException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class ScanResponse
    {
        public bool ok { get; set; }
        public string error { get; set; }
        public int duration_ms { get; set; }
        public int ocr_ms { get; set; }
        public int local_analysis_ms { get; set; }
        public int analysis_duration_ms { get; set; }
        public List<HighlightItem> highlights { get; set; }
        public List<OcrWord> words { get; set; }
        public string analysis_mode { get; set; }
    }

    internal sealed class ScanTiming
    {
        public int Generation { get; set; }
        public long CaptureMs { get; set; }
        public long FingerprintMs { get; set; }
        public long OcrMs { get; set; }
        public long LocalAnalysisMs { get; set; }
        public long RefinementMs { get; set; }
        public long RenderMs { get; set; }
        public long FirstVisibleMs { get; set; }
        public Stopwatch TotalWatch { get; private set; }

        public ScanTiming()
        {
            TotalWatch = new Stopwatch();
        }
    }

    internal sealed class LookupResponse
    {
        public string term { get; set; }
        public string explanation { get; set; }
        public List<AnalysisEntity> entities { get; set; }
        public List<string> sources { get; set; }
        public string lookup_mode { get; set; }
        public bool can_refresh { get; set; }
        public string error { get; set; }
    }

    internal sealed class LookupView
    {
        public string term { get; set; }
        public string context { get; set; }
        public string explanation { get; set; }
        public List<AnalysisEntity> entities { get; set; }
        public List<string> sources { get; set; }
        public bool can_refresh { get; set; }
        public Rectangle anchor { get; set; }
    }

    internal sealed class SessionInfo
    {
        public string token { get; set; }
        public bool has_key { get; set; }
    }

    internal sealed class KeyValidationResult
    {
        public bool ok { get; set; }
        public bool configured { get; set; }
        public string model { get; set; }
        public bool model_available { get; set; }
        public string message { get; set; }
    }

    internal sealed class ServiceHealth
    {
        public bool ok { get; set; }
        public bool has_key { get; set; }
        public string model { get; set; }
        public string analysis_mode { get; set; }
        public int model_analysis_calls_last_hour { get; set; }
        public int model_analysis_limit_per_hour { get; set; }
        public int analysis_cache_entries { get; set; }
    }

    internal sealed class OperationResponse
    {
        public bool ok { get; set; }
    }

    internal sealed class AudioTranscriptionResponse
    {
        public bool ok { get; set; }
        public string text { get; set; }
        public string model { get; set; }
        public string error { get; set; }
        public bool retryable { get; set; }
    }

    internal sealed class BrowserCommand
    {
        public int generation { get; set; }
        public string kind { get; set; }
        public long timestamp { get; set; }
    }

    internal sealed class BrowserAck
    {
        public string generation { get; set; }
        public bool acked { get; set; }
        public bool focused { get; set; }
    }

    internal sealed class WindowsOcrResponse
    {
        public bool ok { get; set; }
        public bool ready { get; set; }
        public string request_id { get; set; }
        public string error { get; set; }
        public int decode_ms { get; set; }
        public int recognize_ms { get; set; }
        public int worker_ms { get; set; }
        public List<OcrWord> words { get; set; }
    }

    internal sealed class OcrWord
    {
        public string text { get; set; }
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
    }

    internal sealed class AnalyzeResponse
    {
        public List<AnalysisEntity> entities { get; set; }
        public List<AnalysisEntity> actions { get; set; }
        public string analysis_mode { get; set; }
        public int analysis_duration_ms { get; set; }
    }

    internal static class ShortcutLookup
    {
        internal static bool TryNormalize(string term, out string canonical)
        {
            canonical = null;
            string compact = System.Text.RegularExpressions.Regex.Replace(
                term ?? String.Empty, @"\s+", String.Empty)
                .Replace('＋', '+').Replace('－', '-').Replace('–', '-').Replace('—', '-');
            string[] parts = System.Text.RegularExpressions.Regex.Split(compact, @"[+\-]");
            if (parts.Length < 2) return false;
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "ctrl", "Ctrl" }, { "ctr1", "Ctrl" }, { "control", "Ctrl" },
                { "alt", "Alt" }, { "a1t", "Alt" }, { "shift", "Shift" }, { "win", "Win" }
            };
            List<string> keys = new List<string>();
            for (int index = 0; index < parts.Length - 1; index++)
            {
                string modifier;
                if (!aliases.TryGetValue(parts[index], out modifier)) return false;
                keys.Add(modifier);
            }
            string key = parts[parts.Length - 1];
            string functionKey = key.ToUpperInvariant().Replace('O', '0');
            int functionNumber;
            if (functionKey.StartsWith("F") && Int32.TryParse(functionKey.Substring(1), out functionNumber) &&
                functionNumber >= 1 && functionNumber <= 12)
                key = "F" + functionNumber;
            else
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z0-9][Oo0]$"))
                    key = key.Substring(0, 1);
                if (!System.Text.RegularExpressions.Regex.IsMatch(key,
                    @"^(?:[A-Za-z0-9]|Enter|Tab|Esc|Delete|Space)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
                key = key.Length == 1 ? key.ToUpperInvariant() :
                    Char.ToUpperInvariant(key[0]) + key.Substring(1).ToLowerInvariant();
            }
            keys.Add(key);
            canonical = String.Join("+", keys.ToArray());
            return true;
        }

        internal static bool TryExplain(string term, string context, out LookupResponse result)
        {
            result = null;
            string canonical;
            if (!TryNormalize(term, out canonical)) return false;
            string normalized = canonical.ToLowerInvariant();
            string body = canonical + " 是键盘组合快捷键：按住前面的修饰键，再按最后一个键。具体功能取决于当前软件的设置。";
            if (normalized == "ctrl+alt+g") body = canonical + " 在实时字典中用于清除高亮并结束当前会话，程序仍在托盘运行。";
            if (normalized == "ctrl+alt+k") body = canonical + " 在实时字典中用于启用或刷新当前窗口高亮。";
            if (normalized == "ctrl+alt+d") body = canonical + " 在实时字典中用于查询选中文字；读取不到时可以输入或粘贴。";
            result = new LookupResponse { term = canonical, explanation = body + "\n\n来源：本地快捷键规则。",
                lookup_mode = "local_shortcut", can_refresh = false };
            return true;
        }
    }

    internal static class UnicodeSpans
    {
        internal static void Convert(string text, List<AnalysisEntity> entities)
        {
            if (entities == null) return;
            text = text ?? String.Empty;
            List<int> offsets = new List<int>();
            for (int index = 0; index < text.Length; index++)
            {
                offsets.Add(index);
                if (Char.IsHighSurrogate(text[index]) && index + 1 < text.Length &&
                    Char.IsLowSurrogate(text[index + 1])) index++;
            }
            offsets.Add(text.Length);
            entities.RemoveAll(delegate(AnalysisEntity entity)
            {
                if (entity == null || entity.start < 0 || entity.end <= entity.start ||
                    entity.end >= offsets.Count) return true;
                int start = offsets[entity.start], end = offsets[entity.end];
                if (text.Substring(start, end - start) != entity.text) return true;
                entity.start = start; entity.end = end;
                return false;
            });
        }
    }

    internal sealed class AnalysisEntity
    {
        public string text { get; set; }
        public string type { get; set; }
        public int start { get; set; }
        public int end { get; set; }
        public string title { get; set; }
        public string time_text { get; set; }
        public string start_iso { get; set; }
        public string end_iso { get; set; }
        public string utc_offset { get; set; }
        public bool needs_confirmation { get; set; }
    }

    internal sealed class HighlightItem
    {
        public string term { get; set; }
        public string context { get; set; }
        public string kind { get; set; }
        public string title { get; set; }
        public string time_text { get; set; }
        public string start_iso { get; set; }
        public string end_iso { get; set; }
        public string utc_offset { get; set; }
        public bool needs_confirmation { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public int w { get; set; }
        public int h { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Contains(int x, int y)
        {
            return x >= Left && x < Right && y >= Top && y < Bottom;
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        public const int WmHotkey = 0x0312;
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();
        public const int WmMouseWheel = 0x020A;
        public const int WmLButtonUp = 0x0202;
        public const int WhMouseLl = 14;
        public const int WsExTransparent = 0x00000020;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExNoActivate = 0x08000000;
        public const int CsDropShadow = 0x00020000;
        public const int SwShowNoActivate = 4;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpShowWindow = 0x0040;
        public static readonly IntPtr HwndTopMost = new IntPtr(-1);

        public const uint EventObjectShow = 0x8002;
        public const uint EventObjectValueChange = 0x800E;
        public const uint EventObjectLocationChange = 0x800B;
        public const uint EventObjectNameChange = 0x800C;
        public const int ObjIdWindow = 0;
        public const uint WinEventOutOfContext = 0x0000;
        public const uint WinEventSkipOwnProcess = 0x0002;

        public delegate void WinEventDelegate(
            IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId,
            uint eventThread, uint eventTime);
        public delegate IntPtr MouseHookDelegate(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseHookData
        {
            public NativePoint point;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(
            IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        public static extern IntPtr BeginDeferWindowPos(int numberOfWindows);
        [DllImport("user32.dll")]
        public static extern IntPtr DeferWindowPos(
            IntPtr positionInfo, IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        public static extern bool EndDeferWindowPos(IntPtr positionInfo);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
            uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowsHookEx(
            int hookId, MouseHookDelegate callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(
            IntPtr hook, int code, IntPtr message, IntPtr data);
    }
}
