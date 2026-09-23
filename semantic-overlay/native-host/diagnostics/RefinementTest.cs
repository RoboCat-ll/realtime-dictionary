using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    // Exercise production asynchronous callbacks with real window handles and an
    // injected, bounded local analyzer. Never start a backend or call a provider.
    internal static class RefinementTest
    {
        static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static void Set(object o, string n, object v) { o.GetType().GetField(n, Flags).SetValue(o, v); }
        static T Get<T>(object o, string n) { return (T)o.GetType().GetField(n, Flags).GetValue(o); }
        static void Call(object o, string n, params object[] a) { o.GetType().GetMethod(n, Flags).Invoke(o, a); }
        static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
        static void Pump(Func<bool> done)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(done(), "UI callback did not complete within six seconds");
        }
        static ScanResponse Result(string term)
        {
            return new ScanResponse { ok = true, analysis_mode = "llm",
                highlights = new List<HighlightItem> { new HighlightItem {
                    term = term, kind = "concept", x = 30, y = 70, w = 80, h = 24 } } };
        }
        [STAThread]
        public static int Main()
        {
            try { return Run(); }
            catch (Exception error) { Console.WriteLine(error.GetType().Name + ": " + error.Message);
                if (error.InnerException != null) Console.WriteLine(error.InnerException.GetType().Name + ": " + error.InnerException.Message + "\n" + error.InnerException.StackTrace);
                return 1; }
        }
        private static int Run()
        {
            ServiceManager.DisableUsageMetricsForDiagnostics = true;
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            string unicodeText = "😀😀 Ctrl+Alt+G";
            var spans = new List<AnalysisEntity> { new AnalysisEntity {
                text = "Ctrl+Alt+G", start = 3, end = 13 } };
            UnicodeSpans.Convert(unicodeText, spans);
            Assert(spans.Count == 1 && spans[0].start == 5 && spans[0].end == 15,
                "Emoji before shortcut truncated its UTF-16 span");
            Assert(unicodeText.Substring(spans[0].start, spans[0].end - spans[0].start) == "Ctrl+Alt+G",
                "Converted range does not cover full shortcut");
            Console.WriteLine("unicode-shortcut-exact-span-ok");
            DateTime armNow = DateTime.UtcNow;
            Assert(OverlayContext.IsMessageClickArmed(armNow, armNow.AddSeconds(10)) &&
                !OverlayContext.IsMessageClickArmed(armNow, armNow.AddMilliseconds(-1)) &&
                !OverlayContext.IsMessageClickArmed(armNow, DateTime.MinValue),
                "One-shot message click arming did not honor its deadline");
            Console.WriteLine("message-click-one-shot-deadline-ok");
            Assert(OverlayContext.IsModelAnalysisMode("jev") &&
                OverlayContext.IsModelAnalysisMode("llm") &&
                OverlayContext.IsModelAnalysisMode("local_strong") &&
                !OverlayContext.IsModelAnalysisMode("local_fallback"),
                "Jev or fallback analysis mode was classified incorrectly");
            Console.WriteLine("jev-model-mode-classification-ok");
            Assert(ServiceManager.IsCompatibleHealth(new ServiceHealth {
                    ok = true,
                    product_id = ServiceManager.ExpectedProductId,
                    protocol_version = ServiceManager.SupportedProtocolVersion }) &&
                !ServiceManager.IsCompatibleHealth(new ServiceHealth {
                    ok = true,
                    product_id = ServiceManager.ExpectedProductId,
                    protocol_version = ServiceManager.SupportedProtocolVersion - 1 }) &&
                !ServiceManager.IsCompatibleHealth(new ServiceHealth {
                    ok = true,
                    product_id = "another-local-service",
                    protocol_version = ServiceManager.SupportedProtocolVersion }),
                "Backend identity/protocol handshake accepted an incompatible service");
            Console.WriteLine("backend-identity-protocol-handshake-ok");
            Assert(OverlayContext.ClassifyChatProcessName("WeChat") == "wechat" &&
                OverlayContext.ClassifyChatProcessName("Weixin") == "wechat" &&
                OverlayContext.ClassifyChatProcessName("QQ") == "qq" &&
                OverlayContext.ClassifyChatProcessName("QQNT") == "qq" &&
                OverlayContext.ClassifyChatProcessName("chrome") == "other",
                "WeChat and QQ process classification was not bounded to the supported apps");
            Console.WriteLine("wechat-qq-metrics-classification-ok");
            using (var image = new Bitmap(500, 240))
            using (Graphics graphics = Graphics.FromImage(image))
            using (Brush bubbleBrush = new SolidBrush(Color.FromArgb(190, 226, 248)))
            using (Brush selectionBrush = new SolidBrush(Color.FromArgb(84, 174, 232)))
            {
                graphics.Clear(Color.FromArgb(244, 244, 244));
                graphics.FillRectangle(bubbleBrush, new Rectangle(80, 60, 340, 100));
                graphics.FillRectangle(selectionBrush, new Rectangle(105, 76, 255, 25));
                graphics.FillRectangle(selectionBrush, new Rectangle(96, 108, 282, 25));
                graphics.FillRectangle(Brushes.Black, new Rectangle(150, 86, 140, 8));
                graphics.FillRectangle(Brushes.Black, new Rectangle(115, 118, 220, 8));
                Rectangle bubble;
                Assert(MessageBubbleDetector.FindInBitmap(image, new Point(175, 89), out bubble) &&
                    bubble.Left <= 82 && bubble.Right >= 418 &&
                    bubble.Top <= 62 && bubble.Bottom >= 158,
                    "One-click fallback mistook selected-text color for the whole message bubble");
            }
            Assert(MessageTextReader.IsCandidate("完整消息文字", "ControlType.Text",
                    new Rectangle(100, 100, 300, 80), new Rectangle(0, 0, 800, 600),
                    new Point(150, 120), 1000) &&
                !MessageTextReader.IsCandidate("输入框草稿", "ControlType.Edit",
                    new Rectangle(100, 400, 500, 120), new Rectangle(0, 0, 800, 600),
                    new Point(150, 420), 1000),
                "One-click accessibility filter accepted an editor or rejected message text");
            Console.WriteLine("one-click-message-accessibility-and-whole-bubble-fallback-ok");
            Assert(OverlayContext.PopupBlocksScheduledScan("conversation", true) &&
                OverlayContext.PopupBlocksScheduledScan("caption", true) &&
                !OverlayContext.PopupBlocksScheduledScan("conversation", false),
                "A definition popup did not block background OCR in every work mode");
            Console.WriteLine("definition-popup-blocks-scheduled-scan-ok");
            var stableProgressive = new List<HighlightItem> { new HighlightItem {
                term = "oneAPI", kind = "concept", context = "old", x = 10, y = 20, w = 60, h = 18 } };
            var stableReference = stableProgressive[0];
            var refinedProgressive = new List<HighlightItem> {
                new HighlightItem { term = "oneAPI", kind = "concept", context = "new",
                    x = 10, y = 20, w = 60, h = 18 },
                new HighlightItem { term = "bootcamp", kind = "concept", context = "new",
                    x = 100, y = 20, w = 75, h = 18 }
            };
            int progressiveAdded = OverlayContext.MergeProgressiveHighlights(
                stableProgressive, refinedProgressive);
            Assert(progressiveAdded == 1 && stableProgressive.Count == 2 &&
                Object.ReferenceEquals(stableReference, stableProgressive[0]) &&
                stableProgressive[0].x == 10 && stableProgressive[0].context == "new" &&
                stableProgressive[1].term == "bootcamp",
                "Progressive refinement removed, reordered or repositioned a stable highlight");
            Assert(OverlayContext.MergeProgressiveHighlights(stableProgressive, refinedProgressive) == 0 &&
                stableProgressive.Count == 2,
                "Repeated progressive refinement duplicated highlights");
            Console.WriteLine("progressive-refinement-additive-stable-idempotent-ok");
            LookupResponse shortcut;
            Assert(ShortcutLookup.TryExplain("Ctrl+Alt+G", "实时字典", out shortcut) &&
                shortcut.explanation.Contains("清除高亮"), "Shortcut did not resolve without a backend");
            foreach (string source in new[] { "Ctrl-Alt-D", "Ctrl－Alt－DO", "Ctr1–A1t–D0" })
            {
                Assert(ShortcutLookup.TryExplain(source, "实时字典", out shortcut) &&
                    shortcut.term == "Ctrl+Alt+D" && shortcut.explanation.Contains("查询选中文字"),
                    "OCR shortcut was not canonicalized: " + source);
            }
            Console.WriteLine("shortcut-hyphen-and-trailing-o-canonicalization-ok");
            Assert(ManualLookupForm.PreferCopiedSelection("", "GitHub") == "GitHub" &&
                ManualLookupForm.PreferCopiedSelection("“itHub0", "GitHub") == "GitHub" &&
                ManualLookupForm.PreferCopiedSelection("realtime-dictionar", "realtime dictionary") ==
                    "realtime dictionary" &&
                ManualLookupForm.PreferCopiedSelection("model", "GitHub") == "model",
                "Clipboard selection did not prefer only closely matching copied text");
            Console.WriteLine("clipboard-selection-bounded-ocr-repair-ok");
            string metricsPath = Path.Combine(Path.GetTempPath(),
                "realtime-dictionary-metrics-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var metrics = new UsageMetricsStore(metricsPath);
            metrics.RecordLookup("active_lookup", "wechat", "clipboard", "model",
                321, false, false, true);
            metrics.RecordSelection("qq", "ocr", "model", 456, true, true, 2);
            metrics.RecordFeedback("active_lookup", "wechat", "useful");
            metrics.RecordHighlights("qq", 9, "jev");
            string metricsText = File.ReadAllText(metricsPath);
            Assert(metricsText.Contains("\"text_source\":\"clipboard\"") &&
                metricsText.Contains("\"source_app\":\"wechat\"") &&
                metricsText.Contains("\"trigger_mode\":\"selection_passage\"") &&
                metricsText.Contains("\"manual_correction\":true") &&
                metricsText.Contains("\"feedback\":\"useful\"") &&
                metricsText.Contains("\"count\":5") &&
                !metricsText.Contains("GitHub") && !metricsText.Contains("explanation"),
                "Privacy-safe metrics schema leaked content or failed to bound values");
            File.Delete(metricsPath);
            Console.WriteLine("content-free-local-usage-metrics-ok");
            using (var service = new ServiceManager())
            using (var lookupForm = new ManualLookupForm(service))
            {
                lookupForm.Open(false);
                Application.DoEvents();
                Assert(lookupForm.Visible, "Manual lookup typed fallback did not open");
                TextBox lookupQuery = Get<TextBox>(lookupForm, "query");
                lookupQuery.Text = "stale term";
                lookupForm.Open(false);
                Application.DoEvents();
                Assert(lookupForm.Visible && lookupQuery.Text.Length == 0 && lookupQuery.Focused,
                    "Repeated manual lookup did not reset and focus the editable fallback");
                TextBox lookupBody = Get<TextBox>(lookupForm, "body");
                Button lookupButton = Get<Button>(lookupForm, "lookup");
                Button pasteButton = Get<Button>(lookupForm, "paste");
                Assert(pasteButton.Text == "粘贴并解释", "Explicit clipboard lookup action is missing");
                lookupQuery.Text = "Ctrl+Alt+D";
                lookupButton.PerformClick();
                Pump(() => lookupButton.Enabled);
                Assert(lookupBody.Text.Contains("查询选中文字"),
                    "Manual lookup submit did not render the local shortcut explanation");
            }
            Console.WriteLine("manual-lookup-editable-fallback-reopen-and-submit-ok");
            using (var assistantTarget = new Form { Text = "Assistant target", Size = new Size(720, 520) })
            using (var assistant = new AssistantPanelForm())
            {
                assistantTarget.Show();
                NativeMethods.SetForegroundWindow(assistantTarget.Handle);
                Application.DoEvents();
                HighlightItem clicked = null;
                assistant.ItemClicked += delegate(HighlightItem item, Rectangle anchor) { clicked = item; };
                assistant.SetItems(new[] {
                    new HighlightItem { term = "Conv2D", context = "神经网络中的 Conv2D" },
                    new HighlightItem { term = "Conv2D", context = "重复词" },
                    new HighlightItem { term = "BatchNorm", context = "归一化" }
                });
                assistant.StartForTarget(new NativeRect { Left = 100, Top = 100, Right = 820, Bottom = 620 });
                Call(assistant, "SetExpanded", true);
                assistant.ShowInactive();
                Application.DoEvents();
                Assert(NativeMethods.GetForegroundWindow() != assistant.Handle,
                    "Showing the floating assistant activated it over the chat target");
                FlowLayoutPanel terms = Get<FlowLayoutPanel>(assistant, "termsPanel");
                Assert(terms.Controls.Count == 2,
                    "Floating assistant did not deduplicate the current term list");
                Button firstTerm = terms.Controls[0] as Button;
                Assert(firstTerm != null && firstTerm.Text == "Conv2D",
                    "Floating assistant did not expose the recognized term");
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(firstTerm, new object[] { EventArgs.Empty });
                Application.DoEvents();
                Assert(clicked != null && clicked.term == "Conv2D",
                    "Floating assistant term did not enter the explanation flow");
                Assert(NativeMethods.GetForegroundWindow() != assistant.Handle,
                    "Floating assistant term action stole foreground focus from the chat target");
            }
            Console.WriteLine("floating-assistant-nonactivating-term-panel-ok");
            using (var definition = new DefinitionForm())
            {
                definition.ShowDefinition("term", "中文解释", new List<AnalysisEntity>(),
                    new List<string>(), new Rectangle(20, 20, 40, 20), false, true);
                Application.DoEvents();
                LinkLabel feedbackLink = Get<LinkLabel>(definition, "feedbackLink");
                Assert(feedbackLink.Visible && feedbackLink.Links.Count == 3 &&
                    feedbackLink.Width >= 340,
                    "Definition feedback actions were not shown for a completed explanation");
            }
            Console.WriteLine("definition-outcome-feedback-visible-ok");
            using (var service = new ServiceManager())
            using (var selection = new SelectionAnalysisForm(service))
            {
                selection.OpenText("OCR 识别的 bootcamp 段落", false, "ocr", "qq");
                Application.DoEvents();
                TextBox source = Get<TextBox>(selection, "source");
                TextBox body = Get<TextBox>(selection, "body");
                Button analyze = Get<Button>(selection, "analyze");
                Label sourceNotice = Get<Label>(selection, "sourceNotice");
                Label heading = Get<Label>(selection, "heading");
                LinkLabel sentence = Get<LinkLabel>(selection, "sentence");
                Assert(source.Text.Contains("bootcamp") && body.Text.Length == 0 &&
                    analyze.Enabled && analyze.Text == "确认并解释" &&
                    sourceNotice.Text.Contains("先校对") && source.Visible &&
                    heading.Text == "这句话的意思",
                    "OCR selection was sent before explicit confirmation or was not editable");
                Set(selection, "passageText", "先使用 oneAPI 和 bootcamp，随后再次使用 oneAPI");
                sentence.Text = "先使用 oneAPI 和 bootcamp，随后再次使用 oneAPI";
                Call(selection, "RenderTerms", new List<SelectionTerm> {
                    new SelectionTerm { text = "oneAPI", explanation = "统一编程体系。" },
                    new SelectionTerm { text = "bootcamp", explanation = "集中训练。" },
                    new SelectionTerm { text = "oneAPI", explanation = "重复项。" },
                });
                FlowLayoutPanel termControls = Get<FlowLayoutPanel>(selection, "terms");
                Assert(sentence.Links.Count == 3 && termControls.Controls.Count == 3 &&
                    body.Text.Length == 0,
                    "Repeated clickable terms overlapped, duplicated buttons, or hid the whole-message explanation");
                Call(selection, "ShowTerm", new SelectionTerm {
                    text = "oneAPI", explanation = "统一编程体系。" });
                Assert(Get<TextBox>(selection, "termBody").Text == "统一编程体系。" &&
                    body.Text.Length == 0,
                    "Term annotation replaced the whole-message explanation");
            }
            using (var service = new ServiceManager())
            using (var oversized = new SelectionAnalysisForm(service))
            {
                oversized.OpenText(new string('a', 1001), false, "ocr", "qq");
                Application.DoEvents();
                Assert(Get<TextBox>(oversized, "source").Text.Length == 0 &&
                    !Get<Button>(oversized, "analyze").Enabled &&
                    Get<Label>(oversized, "sourceNotice").Text.Contains("1000"),
                    "Oversized selection was silently truncated or left submittable");
            }
            Console.WriteLine("selected-passage-ocr-confirmation-and-size-boundary-ok");
            bool skipLiveAccessibility = String.Equals(
                Environment.GetEnvironmentVariable("SKIP_LIVE_ACCESSIBILITY"), "1",
                StringComparison.Ordinal);
            if (skipLiveAccessibility)
            {
                Console.WriteLine("selected-unhighlighted-word-accessibility-skipped-explicitly");
                Console.WriteLine("selection-toolbar-focus-click-dismiss-skipped-explicitly");
            }
            else
            {
                using (var selectionTarget = new Form())
                using (var selectedText = new RichTextBox { Text = "hello bootcamp world", Dock = DockStyle.Fill })
                {
                    selectionTarget.Controls.Add(selectedText);
                    selectionTarget.Show();
                    NativeMethods.SetForegroundWindow(selectionTarget.Handle);
                    selectionTarget.Activate(); selectedText.Focus();
                    Pump(() => NativeMethods.GetForegroundWindow() == selectionTarget.Handle && selectedText.Focused);
                    selectedText.Select(6, 8); Application.DoEvents();
                    var selectionTask = System.Threading.Tasks.Task.Factory.StartNew(delegate {
                        return (string)typeof(ManualLookupForm).GetMethod("ReadSelection",
                            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                    });
                    Pump(() => selectionTask.IsCompleted);
                    Assert(selectionTask.Result == "bootcamp", "Accessibility selection could not read the selected word");
                    using (var toolbar = new SelectionActionForm())
                    {
                        IntPtr focusBefore = NativeMethods.GetForegroundWindow();
                        int clicks = 0;
                        toolbar.ExplainRequested += delegate { clicks++; };
                        Rectangle bounds = selectionTarget.Bounds;
                        Rectangle region = SelectionActionForm.DragRegion(
                            new Point(bounds.Left + 30, bounds.Top + 50),
                            new Point(bounds.Left + 130, bounds.Top + 50), bounds);
                        Assert(!region.IsEmpty && bounds.Contains(region), "Selection OCR crop escaped the target");
                        Rectangle multiLine = SelectionActionForm.DragRegion(
                            new Point(bounds.Left + 80, bounds.Top + 45),
                            new Point(bounds.Left + 84, bounds.Top + 180), bounds);
                        Assert(!multiLine.IsEmpty && multiLine.Width >= 80 && bounds.Contains(multiLine),
                            "Multi-line passage selection did not create a bounded editable OCR crop");
                        toolbar.Present(selectionTask.Result, new Point(bounds.Right - 50, bounds.Bottom - 50), region);
                        Application.DoEvents();
                        Assert(toolbar.Visible && NativeMethods.GetForegroundWindow() == focusBefore,
                            "Selection toolbar focus: visible=" + toolbar.Visible + " before=" + focusBefore +
                            " after=" + NativeMethods.GetForegroundWindow() + " toolbar=" + toolbar.Handle);
                        Assert(selectedText.SelectedText == "bootcamp", "Toolbar cleared selected text");
                        Assert(clicks == 0, "Showing toolbar invoked a query");
                        typeof(SelectionActionForm).GetMethod("OnMouseUp", Flags).Invoke(toolbar,
                            new object[] { new MouseEventArgs(MouseButtons.Left, 1, 30, 15, 0) });
                        Assert(clicks == 1, "Toolbar action did not invoke exactly once");
                        typeof(SelectionActionForm).GetMethod("OnMouseUp", Flags).Invoke(toolbar,
                            new object[] { new MouseEventArgs(MouseButtons.Left, 1, 115, 15, 0) });
                        Assert(!toolbar.Visible && clicks == 1, "Dismiss triggered a query");
                    }
                }
                Console.WriteLine("selected-unhighlighted-word-accessibility-ok");
                Console.WriteLine("selection-toolbar-focus-click-dismiss-no-auto-query-ok");
            }
            var familiarity = new TermFamiliarityStore(null);
            DateTime learningClock = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);
            for (int session = 0; session < TermFamiliarityStore.SuppressionThreshold; session++)
            {
                familiarity.BeginSession(learningClock);
                familiarity.UpdateVisibleTerms(new[] { session % 2 == 0 ? "WorkBuddy" : "work Buddy" }, learningClock);
                familiarity.EndSession(true, learningClock.AddSeconds(4));
                if (session + 1 < TermFamiliarityStore.SuppressionThreshold)
                    Assert(!familiarity.ShouldSuppress("workbuddy"), "Term suppressed before the confidence threshold");
                learningClock = learningClock.AddMinutes(1);
            }
            Assert(familiarity.ShouldSuppress("WORK BUDDY"), "Whitespace/case variants did not share familiarity");
            Assert(!familiarity.ShouldSuppress("WorkBuddy++"), "Punctuation-distinct term was suppressed");
            familiarity.BeginSession(learningClock);
            familiarity.UpdateVisibleTerms(new[] { "WorkBuddy" }, learningClock);
            familiarity.NoteClicked("work buddy", learningClock.AddSeconds(1));
            familiarity.EndSession(true, learningClock.AddSeconds(5));
            Assert(!familiarity.ShouldSuppress("WorkBuddy"), "Click did not restore a learned term");
            for (int session = 0; session < TermFamiliarityStore.SuppressionThreshold; session++)
            {
                learningClock = learningClock.AddMinutes(1);
                familiarity.BeginSession(learningClock);
                familiarity.UpdateVisibleTerms(new[] { "brief" }, learningClock);
                familiarity.EndSession(true, learningClock.AddSeconds(2));
            }
            Assert(!familiarity.ShouldSuppress("brief"), "Brief visibility counted as an ignored exposure");
            familiarity.BeginSession(learningClock.AddMinutes(1));
            familiarity.UpdateVisibleTerms(new[] { "disabled" }, learningClock.AddMinutes(1));
            familiarity.EndSession(false, learningClock.AddMinutes(1).AddSeconds(10));
            Assert(!familiarity.ShouldSuppress("disabled"), "Disabled learning changed familiarity");
            Console.WriteLine("familiarity-threshold-click-duration-normalization-ok");
            using (var screenshot = new Bitmap(1000, 700))
            using (var selector = new ScanRegionForm(screenshot))
            {
                selector.Show(); Application.DoEvents();
                var picture = (PictureBox)selector.Controls[0];
                var confirm = (Button)selector.Controls[2];
                Assert(!confirm.Enabled, "Empty region can be confirmed");
                typeof(Control).GetMethod("OnMouseDown", Flags).Invoke(picture,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, 100, 60, 0) });
                typeof(Control).GetMethod("OnMouseMove", Flags).Invoke(picture,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 0, 400, 300, 0) });
                typeof(Control).GetMethod("OnMouseUp", Flags).Invoke(picture,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, 400, 300, 0) });
                Assert(confirm.Enabled, "Dragged region not accepted"); confirm.PerformClick();
                NativeRect mapped = ScanRegionForm.MapRegion(selector.SelectedRegion,
                    new NativeRect { Left = -1000, Top = 100, Right = 0, Bottom = 800 });
                Assert(mapped.Left < 0 && mapped.Width > 100 && mapped.Height > 50, "Negative monitor mapping failed");
                NativeRect resized = ScanRegionForm.MapRegion(selector.SelectedRegion,
                    new NativeRect { Left = 0, Top = 0, Right = 2000, Bottom = 1400 });
                Assert(Math.Abs(resized.Width - mapped.Width * 2) <= 2, "Resized region scaling failed");
            }
            Console.WriteLine("region-drag-and-coordinate-mapping-ok");
            using (var pixels = new Bitmap(600, 450))
            using (var shifted = new Bitmap(600, 450))
            {
                using (Graphics g = Graphics.FromImage(pixels))
                {
                    g.Clear(Color.White);
                    Random r = new Random(61);
                    for (int i = 0; i < 1200; i++)
                        g.FillRectangle(Brushes.Black, r.Next(600), r.Next(450), r.Next(3, 18), r.Next(3, 9));
                }
                using (Graphics g = Graphics.FromImage(shifted))
                { g.Clear(Color.White); g.DrawImageUnscaled(pixels, 0, -57); }
                int delta;
                var frame = ScrollFrame.FromBitmap(pixels);
                Assert(frame.TryDisplacement(ScrollFrame.FromBitmap(shifted), out delta) && delta == -57,
                    "Measured scroll translation was incorrect");
                Assert(frame.TryDisplacement(frame, out delta) && delta == 0, "Static frame moved");
                using (Graphics g = Graphics.FromImage(shifted)) g.Clear(Color.White);
                Assert(!frame.TryDisplacement(ScrollFrame.FromBitmap(shifted), out delta), "Lost content guessed a position");
            }
            Console.WriteLine("scroll-pixels-static-shift-loss-ok");
            ServiceManager.WorkModeOverrideForDiagnostics = "conversation";
            ServiceManager.DisableFamiliarityPersistenceForDiagnostics = true;
            var ctx = (OverlayContext)FormatterServices.GetUninitializedObject(typeof(OverlayContext));
            using (var services = new ServiceManager())
            using (var target = new Form { Text = "Refinement regression", Bounds = new Rectangle(240, 180, 650, 350), TopMost = true })
            using (var dispatcher = new MessageForm())
            using (var status = new StatusForm())
            using (var definition = new DefinitionForm())
            using (var assistantPanel = new AssistantPanelForm())
            using (var assistantAnchor = new HighlightForm())
            using (var lyric = new CaptionLyricForm())
            using (var menu = new ToolStripMenuItem())
            {
                target.Show();
                Application.DoEvents();
                IntPtr dispatchHandle = dispatcher.Handle;
                NativeRect rect;
                NativeMethods.GetWindowRect(target.Handle, out rect);
                Set(ctx, "services", services); Set(ctx, "dispatcher", dispatcher);
                Set(ctx, "statusWindow", status); Set(ctx, "definitionWindow", definition);
                Set(ctx, "assistantPanel", assistantPanel);
                Set(ctx, "assistantLookupAnchor", assistantAnchor);
                Set(ctx, "captionLyricWindow", lyric);
                Set(ctx, "trayStatusItem", menu); Set(ctx, "active", true);
                Set(ctx, "targetWindow", target.Handle); Set(ctx, "targetRect", rect);
                Set(ctx, "refreshLock", new object());
                Set(ctx, "relativeHighlights", new List<HighlightItem>());
                services.GetType().GetProperty("PresentationMode").SetValue(services, "attached", null);
                var windows = new List<HighlightForm>(); Set(ctx, "windows", windows);
                try
                {
                    var progressiveStarted = new ManualResetEvent(false);
                    var progressiveRelease = new ManualResetEvent(false);
                    var stableItem = new HighlightItem {
                        term = "oneAPI", kind = "concept", context = "local",
                        x = 30, y = 70, w = 80, h = 24 };
                    Get<List<HighlightItem>>(ctx, "relativeHighlights").Add(stableItem);
                    Set(ctx, "scanGeneration", 10);
                    Set(ctx, "progressiveHighlightGeneration", 10);
                    Set(ctx, "refineWords", new Func<List<OcrWord>, ScanResponse>(words => {
                        progressiveStarted.Set(); progressiveRelease.WaitOne(3000);
                        return new ScanResponse { ok = true, analysis_mode = "jev",
                            highlights = new List<HighlightItem> {
                                new HighlightItem { term = "oneAPI", kind = "concept", context = "model",
                                    x = 30, y = 70, w = 80, h = 24 },
                                new HighlightItem { term = "bootcamp", kind = "concept", context = "model",
                                    x = 130, y = 70, w = 90, h = 24 }
                            } };
                    }));
                    Call(ctx, "QueueConversationRefinement", 10, target.Handle, rect,
                        new List<OcrWord> { new OcrWord { text = "oneAPI bootcamp" } },
                        0, 0, Result("fallback"));
                    Pump(() => progressiveStarted.WaitOne(0));
                    Assert(Get<List<HighlightItem>>(ctx, "relativeHighlights").Count == 1 &&
                        Object.ReferenceEquals(Get<List<HighlightItem>>(ctx, "relativeHighlights")[0], stableItem),
                        "Stable local highlight disappeared while model refinement was pending");
                    progressiveRelease.Set();
                    Pump(() => !Get<bool>(ctx, "refinementRunning"));
                    var progressiveItems = Get<List<HighlightItem>>(ctx, "relativeHighlights");
                    Assert(progressiveItems.Count == 2 &&
                        Object.ReferenceEquals(progressiveItems[0], stableItem) &&
                        progressiveItems[0].term == "oneAPI" && progressiveItems[0].x == 30 &&
                        progressiveItems[1].term == "bootcamp",
                        "Async model refinement did not append without disturbing the stable highlight");
                    progressiveStarted.Dispose(); progressiveRelease.Dispose();
                    Console.WriteLine("progressive-refinement-visible-while-pending-and-additive-ok");

                    var started = new ManualResetEvent(false);
                    var release = new ManualResetEvent(false);
                    var calls = new List<string>();
                    Set(ctx, "refineWords", new Func<List<OcrWord>, ScanResponse>(words => {
                        string word = words[0].text;
                        lock (calls) calls.Add(word);
                        if (word == "old") { started.Set(); release.WaitOne(3000); }
                        return Result(word);
                    }));
                    Set(ctx, "scanGeneration", 1);
                    Call(ctx, "QueueConversationRefinement", 1, target.Handle, rect,
                        new List<OcrWord> { new OcrWord { text = "old" } }, 0, 0, Result("fallback-old"));
                    Pump(() => started.WaitOne(0));
                    Call(ctx, "InvalidateContentAndSchedule", 220);
                    Assert(Get<int>(ctx, "scanGeneration") == 2, "Model generation was not invalidated");
                    Call(ctx, "QueueConversationRefinement", 2, target.Handle, rect,
                        new List<OcrWord> { new OcrWord { text = "middle" } }, 0, 0, Result("fallback-middle"));
                    Call(ctx, "InvalidateContentAndSchedule", 220);
                    Call(ctx, "QueueConversationRefinement", 3, target.Handle, rect,
                        new List<OcrWord> { new OcrWord { text = "latest" } }, 0, 0, Result("fallback-latest"));
                    release.Set();
                    Pump(() => !Get<bool>(ctx, "refinementRunning"));
                    Assert(calls.Count == 2 && calls[1] == "latest", "Latest frame was lost or middle frame ran");
                    Assert(Get<List<HighlightItem>>(ctx, "relativeHighlights")[0].term == "latest", "Stale result rendered");
                    started.Dispose(); release.Dispose();
                    Console.WriteLine("latest-frame-and-stale-callback-ok");
                    target.Activate(); Application.DoEvents();
                    NativeMethods.SetForegroundWindow(target.Handle); Application.DoEvents();
                    Pump(() => !status.Visible);
                    target.Update();
                    Set(ctx, "highlightsCurrent", true);
                    Call(ctx, "RenderHighlights");
                    int hiddenCount = 0;
                    var rendered = Get<List<HighlightForm>>(ctx, "windows");
                    foreach (var window in rendered)
                        window.VisibleChanged += delegate(object sender, EventArgs args) {
                            if (!((Form)sender).Visible) hiddenCount++;
                        };
                    int stableGeneration = Get<int>(ctx, "scanGeneration");
                    NativeRect testViewport = rect;
                    if (Get<byte[]>(ctx, "displayedFingerprint") == null)
                        Set(ctx, "displayedFingerprint", services.ComputeScreenFingerprint(testViewport));
                    if (Get<ScrollFrame>(ctx, "scrollFrame") == null)
                        Set(ctx, "scrollFrame", ScrollFrame.Capture(testViewport));
                    for (int repeat = 0; repeat < 20; repeat++)
                    {
                        Call(ctx, "QueueContentRefresh", 0, false);
                        Application.DoEvents();
                        Call(ctx, "StartScan");
                    }
                    Assert(hiddenCount == 0, "Unchanged probe hid highlight windows");
                    Assert(Get<int>(ctx, "scanGeneration") == stableGeneration, "Unchanged probe restarted OCR");
                    Assert(!Get<bool>(ctx, "scanRunning"), "Unchanged probe started background scan");
                    Console.WriteLine("unchanged-20-events-no-hide-no-ocr-ok");
                    int drawingOffset = 0;
                    target.Paint += delegate(object sender, PaintEventArgs e) {
                        Random random = new Random(17);
                        for (int i = 0; i < 900; i++)
                            e.Graphics.FillRectangle(Brushes.Black, random.Next(600),
                                random.Next(500) + drawingOffset, random.Next(3, 16), random.Next(3, 9));
                    };
                    target.Invalidate(); target.Update();
                    Rectangle client = target.RectangleToScreen(target.ClientRectangle);
                    Set(ctx, "customRegionWindow", target.Handle);
                    Set(ctx, "customRegion", new RectangleF(
                        (float)(client.Left - rect.Left) / rect.Width,
                        (float)(client.Top - rect.Top) / rect.Height,
                        (float)client.Width / rect.Width, (float)client.Height / rect.Height));
                    Call(ctx, "RenderHighlights");
                    IntPtr highlightHandle = rendered[0].Handle;
                    Assert(rendered[0].CaptureExcluded, "This OS cannot exclude highlights from capture");
                    NativeRect contentViewport = ScanRegionForm.MapRegion(
                        Get<RectangleF>(ctx, "customRegion"), rect);
                    Set(ctx, "scrollFrame", ScrollFrame.Capture(contentViewport));
                    var captureWatch = Stopwatch.StartNew();
                    for (int captureIndex = 0; captureIndex < 40; captureIndex++)
                        ScrollFrame.Capture(ScanRegionForm.MapRegion(
                            Get<RectangleF>(ctx, "customRegion"), rect));
                    captureWatch.Stop();
                    double averageCaptureMs = captureWatch.Elapsed.TotalMilliseconds / 40.0;
                    Assert(averageCaptureMs < 20.0,
                        "Sparse tracking capture is too slow for a 24ms cadence: " + averageCaptureMs);
                    int originalY = Get<List<HighlightItem>>(ctx, "relativeHighlights")[0].y;
                    Set(ctx, "scrollUntilUtc", DateTime.UtcNow.AddMilliseconds(500));
                    drawingOffset = -53; target.Invalidate(); target.Update(); NativeMethods.DwmFlush();
                    Call(ctx, "QueueScrollProbe");
                    Pump(() => Get<int>(ctx, "scrollProbeRunning") == 0);
                    int firstTrackedY = Get<List<HighlightItem>>(ctx, "relativeHighlights")[0].y;
                    Assert(firstTrackedY == originalY - 53,
                        "Live window pixels did not move highlight with text: expected " +
                        (originalY - 53) + ", actual " + firstTrackedY);
                    foreach (int offset in new int[] { -90, -20, 0 })
                    {
                        drawingOffset = offset; target.Invalidate(); target.Update(); NativeMethods.DwmFlush();
                        Call(ctx, "QueueScrollProbe");
                        Pump(() => Get<int>(ctx, "scrollProbeRunning") == 0);
                        Assert(Get<List<HighlightItem>>(ctx, "relativeHighlights")[0].y == originalY + offset,
                            "Reverse scroll or offscreen reentry lost the word");
                    }
                    rendered[0].ClipTo(new Rectangle(rendered[0].Left, rendered[0].Top + 8, 300, 300));
                    Assert(!rendered[0].Region.IsVisible(2, 2) && rendered[0].Region.IsVisible(2, 10),
                        "Partial viewport clipping failed");
                    rendered[0].ClearClip();
                    Console.WriteLine("periodic-scroll-without-wheel-and-clipping-ok average-capture-ms=" +
                        averageCaptureMs.ToString("0.00"));

                    foreach (string failure in new[] { "throw", "null", "invalid" })
                    {
                        Set(ctx, "refineWords", new Func<List<OcrWord>, ScanResponse>(words => {
                            if (failure == "throw") throw new TimeoutException("fixture");
                            return failure == "null" ? null : new ScanResponse { ok = true };
                        }));
                        Set(ctx, "scanGeneration", 4);
                        status.ShowScanning(rect);
                        Call(ctx, "QueueConversationRefinement", 4, target.Handle, rect,
                            new List<OcrWord> { new OcrWord { text = "fallback" } }, 10, 20, Result("fallback"));
                        Pump(() => !Get<bool>(ctx, "refinementRunning"));
                        var items = Get<List<HighlightItem>>(ctx, "relativeHighlights");
                        Assert(items.Count == 1 && items[0].term == "fallback" && items[0].x == 40 && items[0].y == 90,
                            "Failure did not retain current OCR result and its coordinates");
                        Assert(menu.Text.Contains("本地结果"), "Failure left pending status");
                        Pump(() => !status.Visible);
                    }
                    Console.WriteLine("timeout-null-invalid-fallback-ui-ok");
                    Set(ctx, "scanGeneration", 5);
                    status.ShowScanning(rect);
                    Call(ctx, "QueueConversationRefinement", 5, target.Handle, rect,
                        new List<OcrWord>(), 0, 0, null);
                    Pump(() => !Get<bool>(ctx, "refinementRunning"));
                    Assert(!status.Visible && menu.Text.Contains("重试") &&
                        Get<byte[]>(ctx, "lastCaptureFingerprint") == null, "Unrecoverable failure was cached or remained scanning");
                    Console.WriteLine("unrecoverable-failure-retry-ok");
                    using (var delayed = new ManualResetEvent(false))
                    {
                        Set(ctx, "scanGeneration", 6);
                        NativeRect chosen = ScanRegionForm.MapRegion(Get<RectangleF>(ctx, "customRegion"), rect);
                        Set(ctx, "lastCaptureFingerprint", services.ComputeScreenFingerprint(chosen));
                        Set(ctx, "refineWords", new Func<List<OcrWord>, ScanResponse>(delegate {
                            delayed.WaitOne(4000); return Result("stale-pixels");
                        }));
                        Call(ctx, "QueueConversationRefinement", 6, target.Handle, rect,
                            new List<OcrWord> { new OcrWord { text = "old" } }, 0, 0, Result("old"));
                        drawingOffset = 1000; target.Invalidate(); target.Update(); NativeMethods.DwmFlush();
                        delayed.Set();
                        Pump(() => !Get<bool>(ctx, "refinementRunning"));
                        Assert(Get<int>(ctx, "scanGeneration") > 6 && !Get<bool>(ctx, "highlightsCurrent"),
                            "Delayed model coordinates rendered after source pixels changed");
                    }
                    Console.WriteLine("late-model-result-pixel-validation-ok");
                }
                finally { foreach (var window in windows) window.Dispose(); target.Close(); }
            }
            return 0;
        }
    }
}
