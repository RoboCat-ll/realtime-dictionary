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
        private void ConfigureApiKey()
        {
            ConfigureApiKey(true);
        }

        private void ConfigureApiKey(bool textProvider)
        {
            using (ApiKeyForm form = new ApiKeyForm(services.ValidateApiKey))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return;
                try
                {
                    if (textProvider) services.SaveTextApiKey(form.ApiKey, form.BaseUrl, form.Model);
                    else services.SaveApiKey(form.ApiKey, form.BaseUrl, form.Model);
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
                    services.Log("API Key save failed: " + error.GetType().Name);
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
                            if (task.IsFaulted)
                            {
                                string detail = task.Exception.GetBaseException().Message;
                                services.Log("Backend startup rejected: " + detail);
                                ShowNotice(detail, ToolTipIcon.Error);
                            }
                            return;
                        }
                        string mode = services.WorkMode == "caption" ? "会议字幕" : "对话窗口";
                        if (task.Result.ok && task.Result.has_explanation_key)
                            trayStatusItem.Text = "状态：" + mode + "模式，智能服务已配置";
                        else if (!task.Result.has_explanation_key)
                        {
                            trayStatusItem.Text = "状态：" + mode + "模式，仅本地识别";
                            if (services.TakeFirstRunNotice())
                                ShowNotice("已启动：按 Ctrl+Alt+K，再在 10 秒内单击一条微信或 QQ 消息。", ToolTipIcon.Info);
                        }
                        else
                            trayStatusItem.Text = "状态：" + mode + "模式，模型异常";
                        UpdateUsageStatus(task.Result);
                    }));
                }
                catch (Exception error)
                {
                    services.Log("Model status UI update failed: " + error.GetType().Name);
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
                        services.Log("Usage status UI update failed: " + error.GetType().Name);
                    }
                });
        }

        private void UpdateUsageStatus(ServiceHealth health)
        {
            if (health == null || !health.ok)
            {
                trayUsageItem.Text = "引擎：服务不可用";
                return;
            }
            string analysis = String.Equals(health.analysis_provider, "local", StringComparison.OrdinalIgnoreCase)
                    ? "本地规则"
                    : "解释模型 / " + health.analysis_model;
            string explanation = health.has_explanation_key
                ? (health.explanation_model ?? health.model ?? "已配置")
                : "本地/公共词典";
            trayUsageItem.ToolTipText = String.Format(
                "高亮：{0} · 解释：{1} · 调用 {2}/{3} · 缓存 {4}",
                analysis,
                explanation,
                health.model_analysis_calls_last_hour,
                health.model_analysis_limit_per_hour,
                health.analysis_cache_entries);
            trayUsageItem.Text = String.Format("解释模型：{0} · 本小时分析 {1}/{2}",
                health.has_explanation_key ? "已配置" : "未配置",
                health.model_analysis_calls_last_hour, health.model_analysis_limit_per_hour);
        }

        private void ShowHelp()
        {
            MessageBox.Show(
                "微信 / QQ 聊天（当前验证范围）\r\n" +
                "1. 切换到微信或 QQ。\r\n" +
                "2. 按 Ctrl + Alt + K，进入 10 秒待选状态。\r\n" +
                "3. 单击一条需要理解的消息一次；点击后待选状态自动结束。\r\n" +
                "4. 先阅读整段解释，再按需点击原文中的必要术语。\r\n\r\n" +
                "程序优先读取完整消息控件；读不到时会定位整个消息气泡并 OCR。原文始终显示在面板中，可直接修改后重试。每次只处理当前消息，最多 1000 个字符。\r\n" +
                "拖选后出现的“解释这段”仅作为单击识别失败时的兜底。\r\n" +
                "主动查词：选中文字后按 Ctrl + Alt + D；读取不到时会自动采用明确复制的文字，仍可手动修改。\r\n" +
                "未高亮的词也能在原句中选取，再点附近的“解释”；需要背景和例子时点“展开解释”。\r\n" +
                "连续查词模式（托盘开关，默认关闭）：开启后双击一条消息即解释，可在“触发方式”里改为 Alt＋单击。单击和正常聊天不会触发；双击与客户端原生行为的兼容性仍在验收中。\r\n" +
                "托盘“设置”中可查看用量、隐私和本地体验数据，调整字幕保存与词语熟悉度。\r\n\r\n" +
                "实验功能（默认关闭）\r\n" +
                "整窗 OCR 高亮、会议字幕和提醒只保留兼容测试。\r\n" +
                "需要整窗扫描时，先启用实验功能，再点击“扫描当前窗口（兼容）”。\r\n" +
                "会议语音字幕：\r\n" +
                "1. 从托盘“实验功能 → 工作模式”切换为“会议语音字幕”。\r\n" +
                "2. “会议音源”默认优先当前进程；遇到无声可切换全系统兼容模式。\r\n" +
                "3. 切回会议窗口，按 Ctrl + Alt + K；首次确认时可选以后不再询问。\r\n" +
                "4. 窗口旁的小框显示启动、收音或转写状态；托盘显示实际音源。麦克风默认不采集。\r\n\r\n" +
                "会议字幕仍使用原文词语高亮；解释打开时字幕刷新会暂停。\r\n" +
                "右键高亮词可选择以后不再标注。\r\n" +
                "蓝色时间可编辑为本地提醒；提醒保存在本机，到点弹窗，可延后10分钟。\r\n" +
                "实时字典必须在托盘运行才能准时提醒；“实验功能 → 本地提醒”可查看和删除。\r\n" +
                "按 Ctrl + Alt + G 结束当前高亮或字幕会话。\r\n\r\n" +
                "会议模式不读取屏幕文字。静音在本地丢弃；检测到的语音片段发送到硅基流动生成字幕。临时网络错误会冷却后继续，密钥或余额错误才会停止。",
                "实时字典使用帮助",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

    }
}
