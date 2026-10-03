using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    // v2 浮框诊断：定位纯函数、视图切换、焦点/Esc 约定、以及合成数据截图。
    // 全部使用合成数据与本地桩，不发起任何网络或模型请求。
    internal static class FloatPlacementTest
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
            Thread.Sleep(150);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(form.Location, Point.Empty, bitmap.Size);
                bitmap.Save(System.IO.Path.Combine(directory, name));
            }
        }

        private static void CheckPlacement()
        {
            Rectangle area = new Rectangle(0, 0, 1280, 800);
            Rectangle window = new Rectangle(100, 50, 1000, 700);
            Size size = new Size(388, 240);
            int reserve = 120;

            // 右侧充足 → 气泡右侧
            Rectangle bubble = new Rectangle(300, 200, 220, 60);
            Point p = SelectionAnalysisForm.PlaceFloat(bubble, size, window, area, reserve);
            Require(p.X == bubble.Right + 12, "Float must prefer the bubble's right side: " + p);
            Require(p.Y <= bubble.Top && p.Y + size.Height <= window.Bottom - reserve,
                "Float must clear the input zone: y=" + p.Y);

            // 右侧不足 → 翻左侧
            Rectangle rightBubble = new Rectangle(1000, 200, 220, 60);
            p = SelectionAnalysisForm.PlaceFloat(rightBubble, size, window, area, reserve);
            Require(p.X + size.Width <= rightBubble.Left, "Float must flip left when right side lacks room: " + p);

            // 两侧都不足 → 不覆盖目标消息
            Rectangle wide = new Rectangle(500, 300, 300, 80);
            Size big = new Size(700, 260);
            p = SelectionAnalysisForm.PlaceFloat(wide, big, window, area, reserve);
            Rectangle placed = new Rectangle(p, big);
            Require(!placed.IntersectsWith(wide), "Float must never cover the target message: " + p);

            // 底部输入区避让：气泡靠近窗口底
            Rectangle low = new Rectangle(300, 640, 220, 50);
            p = SelectionAnalysisForm.PlaceFloat(low, size, window, area, reserve);
            Require(p.Y + size.Height <= window.Bottom - reserve || p.Y + size.Height <= area.Bottom - 8,
                "Float must respect the input-zone reserve: y=" + p.Y);

            // 多屏：右侧第二屏工作区
            Rectangle area2 = new Rectangle(1280, 0, 1280, 800);
            Rectangle bubble2 = new Rectangle(1300, 200, 200, 60);
            p = SelectionAnalysisForm.PlaceFloat(bubble2, size, Rectangle.Empty, area2, reserve);
            Require(p.X == bubble2.Right + 12 && p.X + size.Width <= area2.Right - 14,
                "Placement must use the per-monitor working area: " + p);
            Console.WriteLine("placement-edge-cases-ok");
        }

        private static void CheckViewSwitchAndFocus(string previews)
        {
            using (var form = new SelectionAnalysisForm(null, null,
                delegate(string term, string context, string detail) {
                    return new LookupResponse { lookup_mode = "model", explanation = "释义：" + term };
                }))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(240, 120);
                form.Show();
                Application.DoEvents();

                // 焦点约定：默认不激活、无 WS_EX_NOACTIVATE（用户交互可获得焦点）
                Require((bool)form.GetType().GetProperty("ShowWithoutActivation",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form, null),
                    "Float must show without stealing focus");
                CreateParams cp = (CreateParams)form.GetType().GetProperty("CreateParams",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form, null);
                Require((cp.ExStyle & NativeMethods.WsExNoActivate) == 0,
                    "Float must NOT use WS_EX_NOACTIVATE; user interaction must be able to focus");

                RichTextBox sentence = Field<RichTextBox>(form, "sentence");
                TextBox body = Field<TextBox>(form, "body");
                TextBox termBody = Field<TextBox>(form, "termBody");
                Label heading = Field<Label>(form, "heading");
                Panel wordView = Field<Panel>(form, "wordView");
                TableLayoutPanel layout = Field<TableLayoutPanel>(form, "layout");

                sentence.Text = "我们可以用 RAG 回答知识库的问题。";
                body.Text = "这句话是在讨论检索增强生成。";
                Field<Label>(form, "status").Text = "界面验证 · 合成数据";
                Invoke(form, "RenderTerms", new List<SelectionTerm> {
                    new SelectionTerm { text = "RAG", explanation = "这里指检索增强生成。" } });
                Invoke(form, "RenderTasks", null, null);
                Application.DoEvents();
                Snapshot(form, previews, "float-sentence.png");

                // 复现 RefinementTest 的点词路径：MouseUp → ShowTerm
                int idxOf = sentence.Text.IndexOf("RAG");
                Point linkedPoint = sentence.GetPositionFromCharIndex(idxOf);
                int charIndex = sentence.GetCharIndexFromPosition(new Point(linkedPoint.X + 2, linkedPoint.Y + 2));
                object termAt = Invoke(form, "LinkedTermAt", charIndex);
                Console.WriteLine("debug: idxOf=" + idxOf + " linkedPoint=" + linkedPoint +
                    " clientSize=" + sentence.ClientSize + " row4=" +
                    Field<TableLayoutPanel>(form, "layout").RowStyles[4].Height +
                    " formH=" + form.Height + " dpi=" + form.DeviceDpi +
                    " selLen=" + sentence.SelectionLength +
                    " charIndex=" + charIndex + " linkedTerm=" + (termAt == null ? "null" : "hit"));
                Invoke(sentence, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1,
                    linkedPoint.X + 2, linkedPoint.Y + 2, 0));
                Application.DoEvents();
                Console.WriteLine("debug: termBody=" + termBody.Text +
                    " wordViewActive=" + Field<bool>(form, "wordViewActive"));
                Require(termBody.Text == "这里指检索增强生成。",
                    "Clicking a linked term must switch to the word view: " + termBody.Text);
                Require(wordView.Visible && !layout.Visible, "Word view must replace the sentence view in place");
                Require(termBody.Visible && heading.Text == "词语解释",
                    "Word view must host the pinned term controls");
                Snapshot(form, previews, "float-word.png");

                // 返回整句：原句、解释与阅读位置保留
                LinkLabel back = Field<LinkLabel>(form, "wordBack");
                Invoke(back, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(back.Links[0]));
                Application.DoEvents();
                Require(layout.Visible && !wordView.Visible &&
                    body.Text == "这句话是在讨论检索增强生成。" &&
                    sentence.Text.Contains("RAG"),
                    "Back must restore the sentence view with content intact");
                Require(heading.Text == "这句话的意思", "Back must restore the heading text");
                Snapshot(form, previews, "float-back.png");

                // 长内容展开：词语视图内增高并重新钳制
                Task show = (Task)Invoke(form, "ShowTerm", new SelectionTerm {
                    text = "RAG", explanation = "这里指检索增强生成。" });
                Wait(delegate { return show.IsCompleted; });
                Button expand = Field<Button>(form, "expandTerm");
                int before = form.Height;
                expand.PerformClick();
                Wait(delegate { return expand.Enabled; });
                Require(expand.Text == "收起解释" && Field<bool>(form, "termDetailsExpanded"),
                    "Expansion must grow inside the same float");
                Snapshot(form, previews, "float-word-expanded.png");
                expand.PerformClick();
                Application.DoEvents();
                Require(expand.Text == "展开解释", "Collapse must restore the brief view");

                // Esc：仅焦点内关闭（KeyPreview）
                Invoke(back, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(back.Links[0]));
                Application.DoEvents();
                Require(form.KeyPreview, "Esc handling must be focus-scoped (KeyPreview)");
                form.Close();
                Console.WriteLine("view-switch-focus-ok");

                // 复现 RefinementTest 的 OCR 路径 + 两行文本点词流程
                using (var form2 = new SelectionAnalysisForm(null, null))
                {
                    form2.StartPosition = FormStartPosition.Manual;
                    form2.Location = new Point(240, 120);
                    form2.Show();
                    Application.DoEvents();
                    form2.OpenText("OCR 识别的 bootcamp 段落", false, "ocr", "qq");
                    Application.DoEvents();
                    RichTextBox s2 = Field<RichTextBox>(form2, "sentence");
                    form2.GetType().GetField("passageText", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(form2, "先使用 oneAPI 和 bootcamp，随后再次使用 oneAPI");
                    s2.Text = "先使用 oneAPI 和 bootcamp，随后再次使用 oneAPI";
                    Invoke(form2, "RenderTerms", new List<SelectionTerm> {
                        new SelectionTerm { text = "oneAPI", explanation = "统一编程体系。" },
                        new SelectionTerm { text = "bootcamp", explanation = "集中训练。" },
                        new SelectionTerm { text = "oneAPI", explanation = "重复项。" } });
                    Application.DoEvents();
                    int idx2 = s2.Text.IndexOf("bootcamp");
                    Console.WriteLine("debug2: y0-after-render=" +
                        s2.GetPositionFromCharIndex(0).Y + " selStart=" + s2.SelectionStart);
                    s2.Select(0, 0);
                    s2.ScrollToCaret();
                    Application.DoEvents();
                    Console.WriteLine("debug2: y0-after-manual-scroll=" +
                        s2.GetPositionFromCharIndex(0).Y);
                    Point lp2 = s2.GetPositionFromCharIndex(idx2);
                    int ci2 = s2.GetCharIndexFromPosition(new Point(lp2.X + 2, lp2.Y + 2));
                    Console.WriteLine("debug2: lp=" + lp2 + " ci=" + ci2 +
                        " clientSize=" + s2.ClientSize + " vscrollVisible=" +
                        (s2.ScrollBars == RichTextBoxScrollBars.Vertical));
                    Invoke(s2, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1,
                        lp2.X + 2, lp2.Y + 2, 0));
                    Application.DoEvents();
                    Console.WriteLine("debug2: termBody=" + Field<TextBox>(form2, "termBody").Text);
                    form2.Close();
                }
            }
        }

        private static void CheckSizing(string previews)
        {
            Rectangle original = new Rectangle(200, 200, 480, 320);
            Rectangle desktop = new Rectangle(0, 0, 1600, 1000);
            Size minimum = new Size(320, 170);
            foreach (int direction in new int[] { 1, 2, 4, 8 })
            {
                Rectangle resized = SelectionAnalysisForm.ResizeFloatBounds(original,
                    new Point(40, 50), direction, minimum, desktop);
                Require(direction < 4 ? resized.Height == original.Height : resized.Width == original.Width,
                    "Border resize must affect only its own axis");
                Require(direction == 1 ? resized.Right == original.Right :
                    direction == 4 ? resized.Bottom == original.Bottom : resized.Location == original.Location,
                    "Border resize must anchor the opposite edge");
            }
            Rectangle bounded = SelectionAnalysisForm.ResizeFloatBounds(original,
                new Point(-5000, -5000), 10, minimum, desktop);
            Require(bounded.Size == minimum, "Corner resize must honor the minimum size");
            Console.WriteLine("independent-four-edge-resize-ok");
            using (var form = new SelectionAnalysisForm(null, null))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(240, 120);
                form.Show();
                Application.DoEvents();

                int dpi = Math.Max(96, form.DeviceDpi);
                Require(form.Width == 480 * dpi / 96,
                    "Default float width must be 480 logical pixels: " + form.Width);

                TextBox body = Field<TextBox>(form, "body");
                RichTextBox sentence = Field<RichTextBox>(form, "sentence");
                TextBox termBody = Field<TextBox>(form, "termBody");
                sentence.Text = "我们可以用 RAG 回答知识库的问题。";
                body.Text = "这句话是在讨论检索增强生成。";
                Field<Label>(form, "status").Text = "界面验证 · 合成数据";
                Invoke(form, "RenderTerms", new List<SelectionTerm> {
                    new SelectionTerm { text = "RAG", explanation = "这里指检索增强生成。" } });
                Invoke(form, "RenderTasks", null, null);
                Invoke(form, "UpdateCardLayout");
                Application.DoEvents();

                // 短正文：完整显示且无常驻滚动条
                Require(form.Height >= 320 * dpi / 96 && body.Height >= 70 * dpi / 96,
                    "Sentence view must retain a readable height even for short content");
                Require(body.ScrollBars == ScrollBars.None,
                    "Short meaning must not show a permanent scrollbar");
                Snapshot(form, previews, "float-size-short.png");

                // 长正文：超过约 8 行才出现垂直滚动条
                body.Text = "这句话是在讨论检索增强生成：先检索，再生成。" +
                    "它把问题拿去检索资料库，找出相关内容，再把内容和问题一起交给模型。" +
                    "模型基于这些资料生成回答，而不是凭空发挥。这样做的好处是答案有据可查，" +
                    "而且可以覆盖企业内部文档、会议纪要、代码规范等模型训练时没见过的内容。" +
                    "在这条消息里，说话人建议直接采用这个方案来解决知识库问答的问题。";
                body.Text += body.Text; // Wider reading area still needs a genuinely overflowing fixture.
                Invoke(form, "UpdateCardLayout");
                Application.DoEvents();
                Require(body.ScrollBars == ScrollBars.Vertical,
                    "Long meaning must gain a vertical scrollbar only beyond the cap");
                Snapshot(form, previews, "float-size-long.png");

                // 用户调整尺寸后：返回整句、切换词语、收到结果都不再强制恢复
                Panel grip = Field<Panel>(form, "resizeGrip");
                Require(grip != null && grip.Visible, "Resize grip must exist");
                Panel[] edges = Field<Panel[]>(form, "resizeEdges");
                Require(edges.Length == 4 && Array.TrueForAll(edges, delegate(Panel edge) { return edge.Visible; }),
                    "All four resize edges must be available");
                Point cursorBefore = Cursor.Position;
                Size beforeDrag = form.Size;
                try
                {
                    Cursor.Position = new Point(form.Right - 2, form.Top + 80);
                    Invoke(edges[1], "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 2, 80, 0));
                    Require(Field<bool>(form, "userSized") && edges[1].Capture,
                        "Resize must lock user size and capture the mouse at press time");
                    Cursor.Position = new Point(Cursor.Position.X + 60, Cursor.Position.Y + 30);
                    Invoke(edges[1], "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 62, 110, 0));
                    Require(form.Width == beforeDrag.Width + 60 && form.Height == beforeDrag.Height,
                        "Real right-edge handlers must change width independently");
                    Invoke(edges[1], "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 62, 110, 0));
                    Require(!edges[1].Capture && !Field<bool>(form, "gripResizing"),
                        "Resize must release capture on completion");
                }
                finally { Cursor.Position = cursorBefore; }
                form.GetType().GetField("userSized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, false);
                form.Size = beforeDrag;
                Task brief = (Task)Invoke(form, "ShowTerm", new SelectionTerm {
                    text = "RAG", explanation = "这里指检索增强生成。" });
                Wait(delegate { return brief.IsCompleted; });
                Require(form.Height >= 220 * dpi / 96 && termBody.Height >= 90 * dpi / 96,
                    "Brief word view must retain a readable height");
                Snapshot(form, previews, "float-size-word.png");
                Invoke(form, "ShowSentenceView");
                Size custom = new Size(460, 420);
                form.Size = custom;
                form.GetType().GetField("userSized", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(form, true);
                Task show = (Task)Invoke(form, "ShowTerm", new SelectionTerm {
                    text = "RAG", explanation = "这里指检索增强生成。" });
                Wait(delegate { return show.IsCompleted; });
                Require(form.Size == custom, "User size must survive switching to the word view: " + form.Size);
                LinkLabel back = Field<LinkLabel>(form, "wordBack");
                Invoke(back, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(back.Links[0]));
                Application.DoEvents();
                Require(form.Size == custom, "User size must survive returning to the sentence view");
                Invoke(form, "RenderTerms", new List<SelectionTerm> {
                    new SelectionTerm { text = "RAG", explanation = "这里指检索增强生成。" } });
                Application.DoEvents();
                Require(form.Size == custom, "User size must survive incoming results");
                Snapshot(form, previews, "float-size-custom.png");

                // 拖出工作区后重新钳制回来
                form.Location = new Point(form.Location.X,
                    Screen.FromControl(form).WorkingArea.Bottom - 20);
                Invoke(form, "ClampIntoWorkingArea");
                Require(form.Bottom <= Screen.FromControl(form).WorkingArea.Bottom - 8,
                    "Float must be clamped back into the working area");
                form.Width = Screen.FromControl(form).WorkingArea.Width + 120;
                Invoke(form, "ClampIntoWorkingArea");
                Require(form.Width <= Screen.FromControl(form).WorkingArea.Width - 16,
                    "Restored wide float must fit the current monitor's work area");
                form.Close();
                Console.WriteLine("float-sizing-ok");
            }
        }

        private static void CheckStaleAndRetry(string previews)
        {
            int calls = 0;
            bool failNext = false;
            var form = new SelectionAnalysisForm(null, null, null,
                delegate(string text, bool correction, bool refresh) {
                    calls++;
                    if (failNext) throw new TimeoutException("simulated");
                    return new SelectionAnalysisResponse {
                        display_text = text, analysis_mode = "model",
                        explanation = "这句话是在讨论检索增强生成。",
                        terms = new List<SelectionTerm> {
                            new SelectionTerm { text = "RAG", explanation = "这里指检索增强生成。" } },
                        can_retry = true };
                });
            try
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(680, 120);
                form.Show();
                Application.DoEvents();
                form.OpenText("我们可以用 RAG 回答知识库的问题。", true, "accessibility", "wechat");
                Wait(delegate { return calls == 1; });
                Application.DoEvents();
                Snapshot(form, previews, "float-analyzed.png");

                // 修改原文 → 旧结果置灰失效，不静默清空
                TextBox body = Field<TextBox>(form, "body");
                Invoke(form, "SetSourceEditorVisible", true);
                TextBox source = Field<TextBox>(form, "source");
                source.Text = "我们可以用 RAG 回答内部知识库的问题。";
                Application.DoEvents();
                Application.DoEvents();
                Application.DoEvents();
                Require(body.Text.Length > 0 && body.ForeColor == Color.FromArgb(150, 153, 160),
                    "Editing must dim the stale result instead of silently clearing it");
                Require(Field<Label>(form, "status").Text.Contains("已失效"),
                    "Stale state must be explicit and late results must not overwrite the edit");
                Snapshot(form, previews, "float-stale.png");

                // 失败 → 显式重试按钮
                failNext = true;
                Button analyze = Field<Button>(form, "analyze");
                Task failed = (Task)Invoke(form, "RunAnalysis", true);
                Wait(delegate { return failed.IsCompleted; });
                Require(analyze.Text == "重试" && analyze.Enabled &&
                    Field<Label>(form, "status").Text == "解释失败",
                    "Failure must offer an explicit retry action");
                Snapshot(form, previews, "float-error-retry.png");
                form.Close();
                Console.WriteLine("stale-retry-ok");
            }
            catch (Exception first)
            {
                Console.WriteLine("FIRST-EXCEPTION: " + first);
                throw;
            }
            finally
            {
                try { form.Dispose(); }
                catch (Exception disposeError)
                {
                    Console.WriteLine("DISPOSE-ERR: " + disposeError.Message);
                }
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            try
            {
                CheckPlacement();
                string previews = args.Length > 0 ? args[0] : null;
                CheckViewSwitchAndFocus(previews);
                CheckSizing(previews);
                CheckStaleAndRetry(previews);
                Console.WriteLine("float-placement-view-focus-stale-retry-ok");
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
