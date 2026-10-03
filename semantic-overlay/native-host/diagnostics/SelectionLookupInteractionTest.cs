using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class SelectionLookupInteractionTest
    {
        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(target);
        }

        private static object Invoke(object target, string name, params object[] args)
        {
            return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, args);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Application.DoEvents();
                if (clock.ElapsedMilliseconds > 4000) throw new TimeoutException("UI continuation timeout");
                Thread.Sleep(10);
            }
            Application.DoEvents();
        }

        private static void Snapshot(Form form, string directory, string name)
        {
            if (String.IsNullOrWhiteSpace(directory)) return;
            form.Update();
            Application.DoEvents();
            Thread.Sleep(200);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                // RichTextBox omits its native surface from DrawToBitmap.
                // Capture only this foreground synthetic test form instead.
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(form.Location, Point.Empty, bitmap.Size);
                bitmap.Save(System.IO.Path.Combine(directory, name));
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            int calls = 0;
            string lastDetail = null, lastContext = null;
            bool failExpansion = false;
            ManualResetEvent block = null;
            const string passage = "这次项目采用 RAG 检索资料，最后解释一遍结果。";
            const string brief = "这里的 RAG 指先检索资料，再让模型据此作答的方法。";
            Func<string, string, string, LookupResponse> lookup = delegate(string term, string context, string detail) {
                lastDetail = detail; lastContext = context;
                Interlocked.Increment(ref calls);
                ManualResetEvent pending = block;
                if (pending != null) pending.WaitOne();
                if (failExpansion && detail == "expanded")
                    return new LookupResponse { lookup_mode = "local_fallback", explanation = "模拟请求失败。" };
                return new LookupResponse { lookup_mode = "model", explanation = detail == "brief" ? brief :
                    "RAG 是检索增强生成。它先从知识库中寻找相关资料，再让模型结合资料回答。\r\n" +
                    "例如查询项目规范时，可以先找对应文档，再依据文档给出解释。" };
            };
            try
            {
                using (var form = new SelectionAnalysisForm(null, null, lookup))
                {
                    form.StartPosition = FormStartPosition.CenterScreen;
                    form.Show(); form.Activate();
                    // v2 浮框改为默认不抢焦点；后台诊断进程需要前台窗口验证选词交互，
                    // 用旧版偶发激活行为（TopMost 属性赋值副作用）显式获得前台（仅测试环境需要）。
                    form.TopMost = true;
                    RichTextBox sentence = Field<RichTextBox>(form, "sentence");
                    TextBox termBody = Field<TextBox>(form, "termBody");
                    Button nearby = Field<Button>(form, "nearbyExplain");
                    Button expand = Field<Button>(form, "expandTerm");
                    form.GetType().GetField("passageText", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(form, passage);
                    sentence.Text = passage;
                    Field<TextBox>(form, "body").Text = "这句话说，项目先检索资料，再解释结果。";
                    Field<Label>(form, "status").Text = "本地界面验证 · 查询结果为模拟数据";
                    sentence.Focus(); sentence.Select(passage.IndexOf("RAG"), 3);
                    Application.DoEvents();
                    Invoke(form, "ShowNearbySelectionAction");
                    Require(nearby.Visible && calls == 0, "Selection must offer an action without querying");
                    Require(form.ClientRectangle.Contains(nearby.Bounds), "Action must fit the panel");
                    string previews = args.Length > 0 ? args[0] : null;
                    Snapshot(form, previews, "selection-lookup-nearby.png");
                    nearby.PerformClick();
                    Wait(delegate { return expand.Enabled; });
                    Require(calls == 1 && lastDetail == "brief" && lastContext == passage,
                        "Explicit nearby lookup must send one brief query with this passage");
                    Require(sentence.SelectedText == "RAG" && termBody.Text == brief,
                        "Selection and brief explanation must survive the button click");
                    Snapshot(form, previews, "selection-lookup-brief.png");
                    expand.PerformClick();
                    Wait(delegate { return expand.Enabled; });
                    Require(calls == 2 && lastDetail == "expanded" && expand.Text == "收起解释",
                        "Only explicit expansion may request details");
                    Snapshot(form, previews, "selection-lookup-expanded.png");
                    expand.PerformClick();
                    Require(termBody.Text == brief && calls == 2, "Collapse must restore the brief locally");
                    expand.PerformClick();
                    Require(calls == 2 && expand.Text == "收起解释", "Reopening must reuse current details");

                    var next = new SelectionTerm { text = "bootcamp", explanation = "这里指集训营。" };
                    Task reuse = (Task)Invoke(form, "ShowTerm", next);
                    Wait(delegate { return reuse.IsCompleted; });
                    Require(calls == 2 && termBody.Text == next.explanation,
                        "Passage-provided annotations must not trigger another query");
                    failExpansion = true;
                    expand.PerformClick(); Wait(delegate { return expand.Enabled; });
                    Require(termBody.Text.StartsWith(next.explanation) && expand.Text == "重试展开",
                        "Failed details must retain the brief and offer explicit retry");
                    failExpansion = false;

                    using (var release = new ManualResetEvent(false))
                    {
                        block = release;
                        Task stale = (Task)Invoke(form, "ToggleTermDetails");
                        Wait(delegate { return calls == 4; });
                        Task newer = (Task)Invoke(form, "ShowTerm", new SelectionTerm {
                            text = "新词", explanation = "这是另一个词的释义。" });
                        Wait(delegate { return newer.IsCompleted; });
                        release.Set(); Wait(delegate { return stale.IsCompleted; }); block = null;
                        Require(termBody.Text == "这是另一个词的释义。" && expand.Text == "展开解释",
                            "Late expansion must not replace another term");
                    }
                    using (var release = new ManualResetEvent(false))
                    {
                        block = release;
                        Task stale = (Task)Invoke(form, "ToggleTermDetails");
                        Wait(delegate { return calls == 5; });
                        Field<TextBox>(form, "source").Text = "用户修改了原句。";
                        release.Set(); Wait(delegate { return stale.IsCompleted; }); block = null;
                        Require(!expand.Enabled && termBody.Text == "重新解释后可查看词语注释。",
                            "Editing must invalidate pending details");
                    }
                    sentence.Text = new string('长', 201); sentence.Focus(); sentence.SelectAll();
                    Invoke(form, "ShowNearbySelectionAction");
                    Require(!nearby.Visible && calls == 5, "Oversized selection must not offer a query");
                    form.Close();
                }
                Console.WriteLine("nearby-explicit-brief-expand-reuse-failure-and-stale-result-ok");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine(error.ToString());
                return 1;
            }
        }
    }
}
