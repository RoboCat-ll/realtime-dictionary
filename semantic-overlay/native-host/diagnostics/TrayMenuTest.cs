using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    // Build the production menu without hotkeys, saved profiles, helper processes
    // or a backend. Only the experimental dropdown is opened by this fixture.
    internal static class TrayMenuTest
    {
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string field, object value)
        {
            target.GetType().GetField(field, Private).SetValue(target, value);
        }
        private static void Preference(ServiceManager service, string name, object value)
        {
            service.GetType().GetField("<" + name + ">k__BackingField", Private).SetValue(service, value);
        }
        private static ToolStripMenuItem Item(ToolStripItemCollection items, string text)
        {
            return items.OfType<ToolStripMenuItem>().Single(item => item.Text == text);
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void OpenDropdown(ToolStripMenuItem item)
        {
            typeof(ToolStripMenuItem).GetMethod("OnDropDownShow", Private)
                .Invoke(item, new object[] { EventArgs.Empty });
        }
        [STAThread]
        private static int Main(string[] args)
        {
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            var context = (OverlayContext)FormatterServices.GetUninitializedObject(typeof(OverlayContext));
            var service = (ServiceManager)FormatterServices.GetUninitializedObject(typeof(ServiceManager));
            var reminders = (LocalReminderManager)FormatterServices.GetUninitializedObject(typeof(LocalReminderManager));
                Set(reminders, "store", new LocalReminderStore(null));
                Set(service, "familiarity", new TermFamiliarityStore(null));
                // 连续查词开关会真实写入 PreferenceStore；用临时文件初始化，
                // 避免触碰真实偏好（.NET GetFolderPath 不读 APPDATA 环境变量）。
                Set(service, "preferences", new PreferenceStore(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "rtd-traymenu-test-" + Guid.NewGuid().ToString("N") + ".json"), null));
            Preference(service, "WorkMode", "conversation");
            Preference(service, "PresentationMode", "assistant");
            Preference(service, "Difficulty", "standard");
            Preference(service, "ScanScope", "auto");
            Preference(service, "CaptionAudioScope", "process");
            Preference(service, "CaptionArchiveEnabled", true);
            Preference(service, "CaptionPromptEnabled", true);
            Preference(service, "SelectionToolbarEnabled", true);
            Preference(service, "AutoLearnFamiliarTerms", true);
            using (var tray = new NotifyIcon())
            using (var history = new CaptionHistoryForm())
            using (var action = new SelectionActionForm())
            {
                Set(context, "services", service);
                Set(context, "reminders", reminders);
                Set(context, "tray", tray);
                Set(context, "captionHistoryWindow", history);
                Set(context, "selectionAction", action);
                Set(context, "trayStatusItem", new ToolStripMenuItem("状态：正在启动…"));
                Set(context, "trayUsageItem", new ToolStripMenuItem("模型分析：读取中…"));
                Set(context, "reminderMenuItem", new ToolStripMenuItem());
                try
                {
                    // 触发方式的默认勾选取决于真实偏好，先固定为双击以保证断言确定性
                    // （只改内存中的支持字段，不写偏好文件）。
                    Preference(service, "ContinuousLookupTrigger", "double_click");
                    typeof(OverlayContext).GetMethod("InitializeTrayMenu", Private).Invoke(context, null);
                    using (ContextMenuStrip menu = tray.ContextMenuStrip)
                    {
                        Require(!menu.Items.OfType<ToolStripMenuItem>().Any(item =>
                            item.Text.Contains("浏览器") || item.DropDownItems.OfType<ToolStripMenuItem>()
                                .Any(child => child.Text.Contains("浏览器"))),
                            "Retired browser capability must have no tray entry");
                        string[] expected = { "状态：正在启动…", "解释一条消息（Ctrl+Alt+K）",
                            "主动查词（选中文字后 Ctrl+Alt+D）…", "连续查词模式", "字幕记录…", "模型设置…",
                            "设置", "实验功能", "使用帮助…", "退出" };
                        Require(menu.Items.OfType<ToolStripMenuItem>().Select(item => item.Text)
                            .SequenceEqual(expected), "Primary navigation is cluttered or missing an entry");
                        ToolStripMenuItem settings = Item(menu.Items, "设置");
                        ToolStripMenuItem experiments = Item(menu.Items, "实验功能");
                        Require(Item(settings.DropDownItems, "将字幕按日期保存在本机").Checked &&
                            Item(settings.DropDownItems, "启用点击消息解释（Ctrl+Alt+K）").Checked,
                            "Moving settings lost existing checked preferences");
                        Require(Item(Item(settings.DropDownItems, "词语熟悉度").DropDownItems,
                            "自动减少多次未点击的词").Checked, "Familiarity setting disappeared");
                        Require(Item(experiments.DropDownItems, "标注密度").DropDownItems
                            .OfType<ToolStripMenuItem>().Single(item => item.Checked).Tag as string == "standard",
                            "Density preference was changed during regrouping");
                        Item(experiments.DropDownItems, "显示方式");
                        Item(experiments.DropDownItems, "识别范围");
                        Require(!experiments.DropDownItems.OfType<ToolStripMenuItem>()
                            .Any(item => item.Text.Contains("Jev")), "Retired Jev entry remains visible");
                        OpenDropdown(experiments);
                        Require(experiments.DropDownItems.OfType<ToolStripMenuItem>()
                            .Where(item => item.Text != "启用实验功能").All(item => !item.Enabled),
                            "Disabled experiments expose active scan/configuration actions");
                        Require(Item(menu.Items, "字幕记录…").Enabled && Item(menu.Items, "模型设置…").Enabled,
                            "Archived captions or core settings became gated by experiments");
                        ToolStripMenuItem continuous = Item(menu.Items, "连续查词模式");
                        Require(continuous.DropDownItems.OfType<ToolStripMenuItem>()
                                .Select(item => item.Text).SequenceEqual(new[] { "触发方式" }),
                            "Continuous lookup trigger submenu missing");
                        ToolStripMenuItem triggerMenu = Item(continuous.DropDownItems, "触发方式");
                        Require(triggerMenu.DropDownItems.OfType<ToolStripMenuItem>()
                                .Select(item => item.Text).SequenceEqual(new[] { "双击消息（默认）", "Alt＋单击消息" }) &&
                            triggerMenu.DropDownItems.OfType<ToolStripMenuItem>()
                                .Single(item => item.Checked).Text == "双击消息（默认）",
                            "Continuous lookup trigger choices wrong");
                        bool initial = continuous.Checked;
                        continuous.PerformClick();
                        Require(continuous.Checked != initial, "Continuous lookup toggle did not flip");
                        continuous.PerformClick();
                        Require(continuous.Checked == initial, "Continuous lookup toggle did not restore");
                        Preference(service, "ExperimentalFeaturesEnabled", true);
                        OpenDropdown(experiments);
                        Require(experiments.DropDownItems.OfType<ToolStripMenuItem>().All(item => item.Enabled),
                            "Re-enabling experiments did not restore nested controls");
                        Require(Item(experiments.DropDownItems, "启用实验功能").Checked,
                            "Experiment checked state is stale");
                        if (args.Length > 0)
                        {
                            // Optional visual review of this synthetic menu. Its
                            // Opening handler can request only local /health.
                            menu.Show(new Point(80, 80));
                            Application.DoEvents();
                            menu.Update(); Thread.Sleep(200);
                            using (var bitmap = new Bitmap(menu.Width, menu.Height))
                            {
                                using (var graphics = Graphics.FromImage(bitmap))
                                    graphics.CopyFromScreen(menu.Location, Point.Empty, bitmap.Size);
                                bitmap.Save(args[0]);
                            }
                            menu.Close();
                        }
                    }
                    Console.WriteLine("tray-navigation-preferences-and-experiment-gating-ok");
                    return 0;
                }
                catch (Exception error)
                {
                    Console.WriteLine(error.ToString());
                    return 1;
                }
                finally { tray.Visible = false; }
            }
        }
    }
}
