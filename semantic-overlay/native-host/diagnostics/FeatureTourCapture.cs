using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    // Native UI walkthrough with synthetic data only. No network, private chat,
    // reminders, real preferences or archive writes. Not an acceptance test.
    internal static class FeatureTourCapture
    {
        static T Field<T>(object target, string name) {
            return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        }
        static Task Call(object target, string name, params object[] args) {
            return (Task)target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target,args);
        }
        static void Wait(Func<bool> done) {
            DateTime deadline=DateTime.UtcNow.AddSeconds(5);
            while(!done()) { Application.DoEvents(); Thread.Sleep(10); if(DateTime.UtcNow>deadline) throw new Exception("Demo timeout"); }
            Application.DoEvents();
        }
        static void Save(Form form,string root,string name) {
            form.TopMost=true; form.Show(); form.BringToFront(); form.Activate(); form.Refresh(); Application.DoEvents();
            foreach(Control control in form.Controls) ClearSelection(control);
            Thread.Sleep(200); Application.DoEvents();
            using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) {
                using(var graphics=Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(form.PointToScreen(Point.Empty),Point.Empty,form.ClientSize);
                bitmap.Save(Path.Combine(root,name+".png"));
            }
        }
        static void ClearSelection(Control control) {
            var text=control as TextBoxBase; if(text!=null)text.Select(0,0);
            foreach(Control child in control.Controls)ClearSelection(child);
        }
        [STAThread] static int Main(string[] args) {
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            ServiceManager.DisableFloatSizeRestoreForDiagnostics=true;
            ServiceManager.PreferencePathOverrideForDiagnostics=Path.Combine(args[0],"isolated-preferences.json");
            Directory.CreateDirectory(args[0]);
            using(var form=new SelectionAnalysisForm(null,null,
                delegate(string term,string context,string detail) {
                    return new LookupResponse {lookup_mode="model", explanation=detail=="expanded"
                        ? "例如问报销标准：先检索公司制度，再据此回答。资料过期或检索不准时，仍可能答错。"
                        : "这里的反馈，是用户使用产品后提出的意见或发现的问题。"};
                }, delegate(string text,bool correction,bool refresh) {
                    return new SelectionAnalysisResponse {ok=true,analysis_mode="model",display_text=text,
                        explanation="团队给问答功能接入了资料检索，用户认为回答中编造的内容变少了。",
                        terms=new List<SelectionTerm>{new SelectionTerm{text="RAG",explanation="检索增强生成：先找相关资料，再让模型依据资料回答。"}}};
                })) {
                form.OpenText("这轮我们上了 RAG，用户反馈幻觉明显少了。",true,"accessibility","demo");
                Wait(()=>Field<TextBox>(form,"body").Text.Contains("团队"));
                Field<Label>(form,"status").Text="交互演示 · 示例数据";
                Save(form,args[0],"01-sentence");
                var task=Call(form,"ShowTerm",new SelectionTerm{text="RAG",explanation="检索增强生成：先找相关资料，再让模型依据资料回答。"});
                Wait(()=>task.IsCompleted);Save(form,args[0],"02-word");
                task=Call(form,"ToggleTermDetails");Wait(()=>task.IsCompleted);
                Save(form,args[0],"03-expanded");
                typeof(SelectionAnalysisForm).GetMethod("ShowSentenceView",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
                var sentence=Field<RichTextBox>(form,"sentence");sentence.Select(sentence.Text.IndexOf("反馈"),2);
                task=Call(form,"ExplainSelectedTerm");Wait(()=>task.IsCompleted);Save(form,args[0],"04-selected");
            }
            using(var calendar=new CalendarForm(new HighlightItem {title="参加项目讨论会",time_text="10月12日下午3点，讨论1小时",
                start_iso="2026-10-12T15:00",end_iso="2026-10-12T16:00"},null,null,null,null))
                Save(calendar,args[0],"05-calendar");
            using(var caption=new CaptionLyricForm(null)) {
                caption.ShowAudioLines("Today we are discussing RAG.",
                    "It helps the model answer using relevant documents.",0,
                    new NativeRect { Left=100,Top=100,Right=1100,Bottom=800 });
                Save(caption,args[0],"08-caption");
            }
            using(var history=new CaptionHistoryForm()) {
                history.Present(new List<CaptionEntry>{
                    new CaptionEntry{timestamp=new DateTime(2026,10,9,10,0,0),text="Today we are discussing retrieval-augmented generation."},
                    new CaptionEntry{timestamp=new DateTime(2026,10,9,10,0,6),text="It helps the model answer using relevant documents."},
                    new CaptionEntry{timestamp=new DateTime(2026,10,9,10,0,12),text="We still need to check the quality of the sources."}});
                Save(history,args[0],"06-history");
                history.Translate=delegate(string source){return new CaptionTranslationResponse{translation="我们仍然需要检查资料来源的质量。"};};
                Field<Button>(history,"translateButton").PerformClick();
                Wait(()=>Field<LinkLabel>(history,"explanation").Text.Contains("资料来源"));
                Save(history,args[0],"07-translation");
            }
            Console.WriteLine("Captured eight native UI scenes; synthetic data; no network or reminder creation.");
            return 0;
        }
    }
}
