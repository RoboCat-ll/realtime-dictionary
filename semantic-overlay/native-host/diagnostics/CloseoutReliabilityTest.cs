using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class CloseoutReliabilityTest
    {
        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(instance);
        }
        private static Task Call(object instance, string name, params object[] args)
        {
            return (Task)instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(instance, args);
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private static void Wait(Func<bool> done)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done())
            {
                Application.DoEvents(); Thread.Sleep(10);
                if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("Synthetic UI wait expired");
            }
            Application.DoEvents();
        }

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            int result = 1;
            using (var harness = new Form { Text = "Isolated interaction verification", Width = 300, Height = 120 })
            {
                harness.Shown += delegate {
                    harness.BeginInvoke(new Action(delegate { result = RunChecks(); harness.Close(); }));
                };
                Application.Run(harness);
            }
            return result;
        }

        private static int RunChecks()
        {
            try
            {
                using (var entered = new ManualResetEvent(false))
                using (var release = new ManualResetEvent(false))
                using (var form = new SelectionAnalysisForm(null, null, null,
                    delegate(string text, bool correction, bool refresh) {
                        if (text == "旧消息") { entered.Set(); release.WaitOne(5000); }
                        return new SelectionAnalysisResponse { display_text = text,
                            analysis_mode = "model", explanation = text + "的解释" };
                    }))
                {
                    form.Show();
                    var source = Field<TextBox>(form, "source");
                    var submit = Field<Button>(form, "analyze");
                    source.Text = "旧消息";
                    Task old = Call(form, "RunAnalysis", false);
                    Wait(delegate { return entered.WaitOne(0); });
                    Require(!submit.Enabled, "Submission must be disabled only for the current request");
                    Wait(delegate { return Field<Label>(form, "status").Text.Contains("已等待"); });
                    source.Text = "新消息";
                    Require(submit.Enabled && Field<string>(form, "passageText") == "新消息",
                        "Editing must restore submission and use the new passage for contextual lookup");
                    Task newer = Call(form, "RunAnalysis", true);
                    Wait(delegate { return newer.IsCompleted; });
                    Require(!newer.IsFaulted, "New message analysis faulted");
                    release.Set(); Wait(delegate { return old.IsCompleted; });
                    Require(Field<TextBox>(form, "body").Text == "新消息的解释" && submit.Enabled,
                        "Obsolete analysis must not overwrite the corrected message: " + Field<TextBox>(form, "body").Text + "; " + Field<Label>(form, "status").Text + "; enabled=" + submit.Enabled);
                    source.Text = " ";
                    Require(!submit.Enabled, "Empty edited messages must not submit");
                    form.Close();
                }

                int sentenceRefreshes = 0;
                int reminderOpens = 0;
                using (var form = new SelectionAnalysisForm(null, delegate { reminderOpens++; }, null,
                    delegate(string text, bool correction, bool refresh) {
                        if (refresh && ++sentenceRefreshes == 1) throw new TimeoutException("private-provider-body");
                        if (refresh && sentenceRefreshes == 2) return new SelectionAnalysisResponse {
                            analysis_mode = "local_fallback", explanation = "服务暂不可用" };
                        return new SelectionAnalysisResponse { analysis_mode = "model",
                            explanation = "可靠整句含义", display_text = "RAG 的原句",
                            actions = new System.Collections.Generic.List<AnalysisEntity> {
                                new AnalysisEntity { text = "RAG", title = "讨论 RAG", start = 0, end = 3 } },
                            terms = new System.Collections.Generic.List<SelectionTerm> {
                                new SelectionTerm { text = "RAG", explanation = "检索增强生成" } } };
                    }))
                {
                    form.Show();
                    var input = Field<TextBox>(form, "source");
                    input.Text = "RAG 的原句";
                    Task first = Call(form, "RunAnalysis", false);
                    Wait(delegate { return first.IsCompleted; });
                    var wordAction = Field<Button>(form, "explainSelected");
                    var actionRow = Field<FlowLayoutPanel>(form, "terms");
                    Require(actionRow.ClientRectangle.Contains(wordAction.Bounds) && !actionRow.AutoScroll,
                        "Selected-word action must fit without a redundant scrollbar");
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        Task retry = Call(form, "RunAnalysis", true);
                        Wait(delegate { return retry.IsCompleted; });
                        Require(Field<TextBox>(form, "body").Text == "可靠整句含义" &&
                            Field<RichTextBox>(form, "sentence").Text == "RAG 的原句" &&
                            Field<Label>(form, "status").Text.Contains("保留"),
                            "Sentence refresh failure must preserve meaning and source");
                        Require(Field<System.Collections.IList>(form, "linkedTerms").Count == 1,
                            "Refresh failure must retain clickable term annotations");
                        var rows = Field<FlowLayoutPanel>(form, "tasks");
                        ((Button)rows.Controls[0].Controls[1]).PerformClick();
                        Require(reminderOpens == attempt + 1,
                            "Retained calendar candidate must remain clickable after refresh");
                    }
                    Task recovered = Call(form, "RunAnalysis", true);
                    Wait(delegate { return recovered.IsCompleted; });
                    Require(Field<Label>(form, "status").Text == "整句解释 · 模型结果" &&
                        Field<Button>(form, "analyze").Enabled, "Retry must recover normally");
                    input.Text = "另一条消息";
                    Require(Field<string>(form, "successfulAnalysisInput") == null,
                        "Editing a sentence must invalidate retained analysis");
                    form.Close();
                }

                int briefCalls = 0;
                using (var form = new SelectionAnalysisForm(null, null,
                    delegate(string term, string context, string detail) {
                        if (++briefCalls == 1) throw new TimeoutException("private-provider-body");
                        return new LookupResponse { lookup_mode = "model", explanation = "可靠释义" };
                    }))
                {
                    form.Show();
                    Task failed = Call(form, "ShowTerm", new SelectionTerm { text = "RAG" });
                    Wait(delegate { return failed.IsCompleted; });
                    Button retry = Field<Button>(form, "expandTerm");
                    Require(retry.Enabled && retry.Text == "重试解释" && briefCalls == 1,
                        "Brief failure must offer an explicit retry without automatic requests");
                    Require(!Field<TextBox>(form, "termBody").Text.Contains("private-provider-body"),
                        "Provider exception content must not be displayed");
                    Task retryTask = Call(form, "ToggleTermDetails");
                    Wait(delegate { return retryTask.IsCompleted; });
                    Require(briefCalls == 2 && Field<TextBox>(form, "termBody").Text == "可靠释义" &&
                        retry.Text == "展开解释", "Brief retry must restore normal expansion");
                    form.Close();
                }

                using (var entered = new ManualResetEvent(false))
                using (var release = new ManualResetEvent(false))
                using (var form = new ManualLookupForm(null, delegate(string term, bool refresh) {
                    if (term == "old") { entered.Set(); release.WaitOne(5000); }
                    return new LookupResponse { term = term.ToUpperInvariant(), lookup_mode = "model",
                        explanation = term + " definition" };
                }))
                {
                    form.Show();
                    var input = Field<TextBox>(form, "query");
                    input.Text = "old";
                    Task old = Call(form, "RunLookup", false);
                    Wait(delegate { return entered.WaitOne(0); });
                    input.Text = "new";
                    Require(Field<Button>(form, "lookup").Enabled,
                        "Editing a pending lookup must restore the action");
                    Task newer = Call(form, "RunLookup", false);
                    Wait(delegate { return newer.IsCompleted; });
                    release.Set(); Wait(delegate { return old.IsCompleted; });
                    Require(!newer.IsFaulted && input.Text == "NEW" &&
                        Field<TextBox>(form, "body").Text == "new definition",
                        "Canonical rendering must work while stale lookup results are discarded");
                    form.Close();
                }

                int refreshCalls = 0;
                using (var form = new ManualLookupForm(null, delegate(string term, bool refresh) {
                    if (!refresh) return new LookupResponse { term = term, lookup_mode = "model",
                        explanation = "已有可靠释义", can_refresh = true };
                    if (++refreshCalls == 2) return new LookupResponse {
                        lookup_mode = "local_fallback", explanation = "服务暂不可用", can_refresh = true };
                    throw new TimeoutException("private-provider-body");
                }))
                {
                    form.Show();
                    Field<TextBox>(form, "query").Text = "RAG";
                    Task first = Call(form, "RunLookup", false);
                    Wait(delegate { return first.IsCompleted; });
                    Task refresh = Call(form, "RunLookup", true);
                    Wait(delegate { return refresh.IsCompleted; });
                    Require(Field<TextBox>(form, "body").Text == "已有可靠释义" &&
                        Field<Label>(form, "notice").Text.Contains("保留"),
                        "Failed refresh must preserve the prior same-query model definition");
                    Task fallback = Call(form, "RunLookup", true);
                    Wait(delegate { return fallback.IsCompleted; });
                    Require(Field<TextBox>(form, "body").Text == "已有可靠释义" &&
                        Field<Button>(form, "retry").Enabled,
                        "Fallback refresh must preserve the model answer and allow another retry");
                    Field<TextBox>(form, "query").Text = "other";
                    Task edited = Call(form, "RunLookup", true);
                    Wait(delegate { return edited.IsCompleted; });
                    Require(!Field<TextBox>(form, "body").Text.Contains("已有可靠释义"),
                        "An edited query must not inherit the prior definition");
                    form.Close();
                }

                // Separate temporary profile; no real settings or credentials are read.
                string directory = Path.Combine(Path.GetTempPath(), "RealtimeDictionaryCloseout-" + Guid.NewGuid().ToString("N"));
                string path = Path.Combine(directory, "preferences.json");
                var store = new PreferenceStore(path, null);
                var workers = new Task[8];
                for (int index = 0; index < workers.Length; index++)
                {
                    int captured = index;
                    workers[index] = Task.Factory.StartNew(delegate {
                        for (int value = 0; value < 8; value++)
                            store.SetAndSave("key" + captured, value.ToString());
                    });
                }
                Task.WaitAll(workers);
                var reloaded = new PreferenceStore(path, null);
                for (int index = 0; index < workers.Length; index++)
                {
                    string value;
                    Require(reloaded.TryGetValue("KEY" + index, out value) && value == "7",
                        "Concurrent preference saves lost a key or broke the existing JSON format");
                }
                Require(Directory.GetFiles(directory, "*.tmp").Length == 0,
                    "Successful atomic saves must consume their temporary files");
                Console.WriteLine("pending-edit-stale-result-brief-retry-wait-feedback-and-concurrent-preferences-ok");
                return 0;
            }
            catch (Exception error) { Console.WriteLine(error); return 1; }
        }
    }
}
