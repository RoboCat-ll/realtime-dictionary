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
    internal sealed partial class OverlayContext
    {
        private void InitializeTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip { ShowItemToolTips = true };
            ToolStripMenuItem settingsMenu = new ToolStripMenuItem("设置");
            ToolStripMenuItem familiarityMenu = new ToolStripMenuItem("词语熟悉度");
            trayStatusItem.Enabled = false;
            menu.Items.Add(trayStatusItem);
            trayUsageItem.Enabled = false;
            settingsMenu.DropDownItems.Add(trayUsageItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("解释一条消息（Ctrl+Alt+K）", null, delegate { ArmMessageClick(); });
            menu.Items.Add("主动查词（选中文字后 Ctrl+Alt+D）…", null,
                delegate { OpenManualLookup(false); });
            ToolStripMenuItem continuousItem = new ToolStripMenuItem("连续查词模式") {
                Checked = services.ContinuousLookupEnabled,
                ToolTipText = "开启后，双击 QQ／微信消息即解释（可改为 Alt＋单击），会同时启用点击消息解释。双击与客户端原生行为的兼容性尚在验收中。"
            };
            continuousItem.Click += delegate {
                services.SetContinuousLookupEnabled(!services.ContinuousLookupEnabled);
                continuousItem.Checked = services.ContinuousLookupEnabled;
                ShowNotice(services.ContinuousLookupEnabled
                    ? "连续查词已开启：" + (services.ContinuousLookupTrigger == "alt_click"
                        ? "Alt＋单击一条消息即解释。" : "双击一条消息即解释。")
                    : "连续查词已关闭；仍可用 Ctrl+Alt+K 单次解释。", ToolTipIcon.Info);
            };
            ToolStripMenuItem continuousTriggerMenu = new ToolStripMenuItem("触发方式");
            ToolStripMenuItem doubleClickItem = new ToolStripMenuItem("双击消息（默认）") { Tag = "double_click" };
            ToolStripMenuItem altClickItem = new ToolStripMenuItem("Alt＋单击消息") { Tag = "alt_click" };
            ToolStripMenuItem[] triggerItems = { doubleClickItem, altClickItem };
            Action refreshTriggerChecks = delegate {
                foreach (ToolStripMenuItem item in triggerItems)
                    item.Checked = String.Equals(services.ContinuousLookupTrigger,
                        item.Tag as string, StringComparison.Ordinal);
            };
            foreach (ToolStripMenuItem item in triggerItems)
            {
                item.Click += delegate(object sender, EventArgs args)
                {
                    ToolStripMenuItem selected = sender as ToolStripMenuItem;
                    if (selected == null) return;
                    services.SetContinuousLookupTrigger(selected.Tag as string);
                    refreshTriggerChecks();
                };
                continuousTriggerMenu.DropDownItems.Add(item);
            }
            continuousTriggerMenu.DropDownOpening += delegate { refreshTriggerChecks(); };
            refreshTriggerChecks();
            settingsMenu.DropDownItems.Add(continuousTriggerMenu);
            continuousTriggerMenu.Text = "连续查词触发方式";
            menu.Items.Add(continuousItem);
            menu.Opening += delegate { continuousItem.Checked = services.ContinuousLookupEnabled; };
            menu.Items.Add("字幕记录…", null,
                delegate { QueueShowCaptionHistory(); });
            settingsMenu.DropDownItems.Add("今日模型用量…", null, async delegate {
                try {
                    string summary = await Task.Factory.StartNew(() => services.GetProviderUsage());
                    Form usage = new Form { Text = "今日模型用量", Size = new Size(680, 420),
                        StartPosition = FormStartPosition.CenterScreen, TopMost = true };
                    usage.Controls.Add(new TextBox { Multiline = true, ReadOnly = true,
                        ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = summary });
                    usage.Show();
                } catch { ShowNotice("读取用量记录失败，请检查本地服务。", ToolTipIcon.Warning); }
            });
            settingsMenu.DropDownItems.Add("隐私与密钥状态…", null, async delegate {
                try {
                    ServiceHealth health = await Task.Factory.StartNew(() => services.GetServiceStatus());
                    string warning = health.configuration_warning == "legacy_plaintext_credentials"
                        ? "现有配置仍包含旧版明文密钥，尚未迁移。新保存的凭证会加密；旧凭证需另行确认转换。"
                        : String.IsNullOrEmpty(health.configuration_warning) ? "配置检查未发现明文凭证警告。"
                        : "配置存在异常，请重新检查模型设置。";
                    MessageBox.Show("新凭证绑定当前 Windows 用户和对应服务地址。\r\n" + warning +
                        "\r\n查询词和原句不写日志；历史日志不会自动清除。\r\n字幕默认保存本机，可关闭保存或按日期删除。",
                        "隐私与密钥状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
                } catch { ShowNotice("读取配置状态失败，请检查本地服务。", ToolTipIcon.Warning); }
            });
            ToolStripMenuItem saveCaptions = new ToolStripMenuItem("将字幕按日期保存在本机") {
                Checked = services.CaptionArchiveEnabled };
            saveCaptions.Click += delegate {
                services.SetCaptionArchiveEnabled(!services.CaptionArchiveEnabled);
                saveCaptions.Checked = services.CaptionArchiveEnabled;
                captionHistoryWindow.ArchiveEnabled = services.CaptionArchiveEnabled;
                ShowNotice(services.CaptionArchiveEnabled ? "后续字幕将保存在本机；历史窗口可按日期删除。"
                    : "后续字幕仅保留在本次运行内；以前的历史不会自动删除。", ToolTipIcon.Info);
            };
            captionHistoryWindow.ArchiveEnabled = services.CaptionArchiveEnabled;
            settingsMenu.DropDownItems.Add(saveCaptions);
            ToolStripMenuItem selectionMenu = new ToolStripMenuItem("启用点击消息解释（Ctrl+Alt+K）") {
                Checked = services.SelectionToolbarEnabled };
            selectionMenu.Click += delegate {
                services.SetSelectionToolbarEnabled(!services.SelectionToolbarEnabled);
                selectionMenu.Checked = services.SelectionToolbarEnabled;
                DismissSelectionAction();
                DisarmMessageClick("feature toggled");
            };
            settingsMenu.DropDownItems.Add(selectionMenu);
            selectionAction.ExplainRequested += ExplainSelection;
            ToolStripMenuItem experimentalMenu = new ToolStripMenuItem(
                "实验功能");
            ToolStripMenuItem experimentalToggle = new ToolStripMenuItem("启用实验功能") {
                Checked = services.ExperimentalFeaturesEnabled
            };
            experimentalToggle.Click += delegate
            {
                services.SetExperimentalFeaturesEnabled(!services.ExperimentalFeaturesEnabled);
                experimentalToggle.Checked = services.ExperimentalFeaturesEnabled;
                foreach (ToolStripItem child in experimentalMenu.DropDownItems)
                    if (child != experimentalToggle && !(child is ToolStripSeparator))
                        child.Enabled = services.ExperimentalFeaturesEnabled;
                if (!services.ExperimentalFeaturesEnabled && services.WorkMode == "caption")
                {
                    if (active) DisableSession();
                    services.SetWorkMode("conversation");
                }
                ShowNotice(services.ExperimentalFeaturesEnabled
                    ? "会议和提醒实验功能已启用。"
                    : "实验功能已关闭；微信与 QQ 查词不受影响。", ToolTipIcon.Info);
            };
            experimentalMenu.DropDownItems.Add(experimentalToggle);
            experimentalMenu.DropDownItems.Add("配置语音 / 兼容分析服务…", null, delegate { ConfigureApiKey(false); });
            experimentalMenu.DropDownItems.Add(new ToolStripSeparator());
            experimentalMenu.DropDownItems.Add("扫描当前窗口（兼容）", null,
                delegate { BeginSession(); });
            experimentalMenu.DropDownItems.Add("清除整窗扫描结果", null,
                delegate { DisableSession(); });
            experimentalMenu.DropDownItems.Add(new ToolStripSeparator());
            experimentalMenu.DropDownOpening += delegate
            {
                experimentalToggle.Checked = services.ExperimentalFeaturesEnabled;
                foreach (ToolStripItem child in experimentalMenu.DropDownItems)
                    if (child != experimentalToggle && !(child is ToolStripSeparator))
                        child.Enabled = services.ExperimentalFeaturesEnabled;
            };
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

                        HideHighlights();
                        HideDefinition();

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
            experimentalMenu.DropDownItems.Add(workModeMenu);
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
            experimentalMenu.DropDownItems.Add(audioSourceMenu);
            ToolStripMenuItem captionPromptItem = new ToolStripMenuItem(
                "启动字幕前询问是否发送语音") { Checked = services.CaptionPromptEnabled };
            captionPromptItem.Click += delegate
            {
                services.SetCaptionPromptEnabled(!services.CaptionPromptEnabled);
                captionPromptItem.Checked = services.CaptionPromptEnabled;
            };
            experimentalMenu.DropDownItems.Add(captionPromptItem);
            reminderMenuItem.Click += delegate { reminders.ShowList(); };
            reminders.CountChanged += UpdateReminderMenu;
            UpdateReminderMenu();
            experimentalMenu.DropDownItems.Add(reminderMenuItem);
            ToolStripMenuItem presentationMenu = new ToolStripMenuItem("显示方式");
            ToolStripMenuItem assistantPresentationItem = new ToolStripMenuItem("悬浮助手（默认）");
            ToolStripMenuItem attachedPresentationItem = new ToolStripMenuItem("原文高亮（兼容）");
            Action refreshPresentationChecks = delegate
            {
                assistantPresentationItem.Checked = services.PresentationMode == "assistant";
                attachedPresentationItem.Checked = services.PresentationMode == "attached";
            };
            assistantPresentationItem.Click += delegate
            {
                services.SetPresentationMode("assistant");
                refreshPresentationChecks();
                HideHighlights();
                if (active && highlightsCurrent) RenderHighlights();
            };
            attachedPresentationItem.Click += delegate
            {
                services.SetPresentationMode("attached");
                refreshPresentationChecks();
                HideHighlights();
                if (active && highlightsCurrent) RenderHighlights();
            };
            presentationMenu.DropDownItems.Add(assistantPresentationItem);
            presentationMenu.DropDownItems.Add(attachedPresentationItem);
            presentationMenu.DropDownOpening += delegate { refreshPresentationChecks(); };
            refreshPresentationChecks();
            experimentalMenu.DropDownItems.Add(presentationMenu);
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
            experimentalMenu.DropDownItems.Add(difficultyMenu);
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
            experimentalMenu.DropDownItems.Add(scopeMenu);
            familiarityMenu.DropDownItems.Add("恢复所有被忽略的词", null, delegate
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
            familiarityMenu.DropDownItems.Add(familiarityItem);
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
            familiarityMenu.DropDownItems.Add(resetFamiliarityItem);
            settingsMenu.DropDownItems.Add("查看本地体验数据…", null, delegate
            {
                try { services.OpenUsageMetricsDirectory(); }
                catch (Exception error)
                {
                    services.Log("Open usage metrics directory failed: " + error.GetType().Name);
                    ShowNotice("无法打开本地体验数据目录。", ToolTipIcon.Warning);
                }
            });
            menu.Items.Add("模型设置…", null, delegate { ConfigureApiKey(); });
            settingsMenu.DropDownItems.Add(familiarityMenu);
            menu.Items.Add(settingsMenu);
            menu.Items.Add(experimentalMenu);
            menu.Items.Add("使用帮助…", null, delegate { ShowHelp(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
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
            tray.DoubleClick += delegate { ArmMessageClick(); };

        }
    }
}
