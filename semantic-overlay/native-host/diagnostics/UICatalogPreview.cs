using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost {
 internal static class UICatalogPreview {
  static string output; static List<string> inventory = new List<string>();
  static BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
  static T Field<T>(object x,string n) { return (T)x.GetType().GetField(n,flags).GetValue(x); }
  static void Set(object x,string n,object v) { x.GetType().GetField(n,flags).SetValue(x,v); }
  static object Call(object x,string n,params object[] v) { return x.GetType().GetMethod(n,flags).Invoke(x,v); }
  static void Record(string name,string title,string group) { inventory.Add(name+"\t"+title+"\t"+group); }
  static void Capture(Control control,string name,string title,string group) {
   Form f = control as Form;
   if(f!=null) { f.TopMost=true; f.Show(); Rectangle area=Screen.PrimaryScreen.WorkingArea;
    f.Location=new Point(area.Left+(area.Width-f.Width)/2,area.Top+Math.Max(0,(area.Height-f.Height)/2));
    NativeMethods.SetWindowDisplayAffinity(f.Handle,0); f.Activate(); }
   Application.DoEvents(); Thread.Sleep(250); Application.DoEvents();
   Rectangle rect = f!=null ? f.Bounds : control.RectangleToScreen(control.ClientRectangle);
   using(Bitmap image=new Bitmap(rect.Width,rect.Height)) {
    using(Graphics g=Graphics.FromImage(image)) g.CopyFromScreen(rect.Location,Point.Empty,image.Size);
    image.Save(Path.Combine(output,name+".png"));
   }
   Record(name,title,group);
  }
  static void Preview(Form f,string name,string title,string group) { using(f) { Capture(f,name,title,group); f.Hide(); } }
  static void Preference(object x,string n,object v) { Set(x,"<"+n+">k__BackingField",v); }
  static NativeRect Target() { Rectangle a=Screen.PrimaryScreen.WorkingArea;return new NativeRect {
   Left=a.Left+100,Top=a.Top+60,Right=a.Right-100,Bottom=a.Bottom-60 }; }
  static void Tray() {
   var ctx=(OverlayContext)FormatterServices.GetUninitializedObject(typeof(OverlayContext));
   var svc=(ServiceManager)FormatterServices.GetUninitializedObject(typeof(ServiceManager));
   var reminders=(LocalReminderManager)FormatterServices.GetUninitializedObject(typeof(LocalReminderManager));
   Set(reminders,"store",new LocalReminderStore(null));Set(svc,"familiarity",new TermFamiliarityStore(null));
   foreach(var pair in new Dictionary<string,object> { {"WorkMode","conversation"},{"PresentationMode","assistant"},
    {"Difficulty","standard"},{"ScanScope","auto"},{"CaptionAudioScope","process"},{"CaptionArchiveEnabled",true},
    {"CaptionPromptEnabled",true},{"SelectionToolbarEnabled",true},{"AutoLearnFamiliarTerms",true},{"ExperimentalFeaturesEnabled",true} })
    Preference(svc,pair.Key,pair.Value);
   using(var tray=new NotifyIcon()) using(var history=new CaptionHistoryForm()) using(var action=new SelectionActionForm()) {
    Set(ctx,"services",svc);Set(ctx,"reminders",reminders);Set(ctx,"tray",tray);
    Set(ctx,"captionHistoryWindow",history);Set(ctx,"selectionAction",action);
    Set(ctx,"trayStatusItem",new ToolStripMenuItem("状态：对话模式 · 界面示例"));
    Set(ctx,"trayUsageItem",new ToolStripMenuItem("解释模型：示例配置 · 无实际调用"));Set(ctx,"reminderMenuItem",new ToolStripMenuItem());
    Call(ctx,"InitializeTrayMenu");var menu=tray.ContextMenuStrip;
    menu.Show(new Point(120,110));Capture(menu,"01-tray","托盘主菜单","入口与设置");menu.Hide();
    foreach(string text in new[]{"设置","实验功能"}) {
     var item=menu.Items.OfType<ToolStripMenuItem>().Single(x=>x.Text==text);
     item.DropDown.Show(new Point(120,110));
     Capture(item.DropDown,text=="设置"?"02-settings":"03-experiments",text+"菜单","入口与设置");item.HideDropDown();
    }
   }
  }
  static void Screens() {
   Tray();
   Preview(new ApiKeyForm((a,b,c)=>new KeyValidationResult {ok=false}),"04-model","解释/语音模型配置（空凭证）","入口与设置");
   using(var f=new SelectionAnalysisForm(null,null)) {
    f.Show();Field<TextBox>(f,"body").Text="这句话是在讨论检索增强生成：先找到相关资料，再生成回答。";
    Field<Label>(f,"status").Text="界面预览 · 测试数据，不代表模型输出或响应速度";
    Field<RichTextBox>(f,"sentence").Text="我们可以用 RAG 帮助回答内部知识库的问题。";
    Set(f,"passageText",Field<RichTextBox>(f,"sentence").Text);
    Call(f,"RenderTerms",new List<SelectionTerm>{new SelectionTerm{text="RAG",explanation="这里指检索增强生成，用检索到的材料辅助回答。"}});
    Call(f,"RenderTasks",null,null);Capture(f,"06-message","消息解释：默认卡片","消息与查词");
    Call(f,"ShowTerm",new SelectionTerm{text="RAG",explanation="这里指检索增强生成，用检索到的材料辅助回答。"});
    Capture(f,"07-term","消息解释：词语注释展开","消息与查词");
    Call(f,"SetSourceEditorVisible",true);Field<TextBox>(f,"source").Text=Field<RichTextBox>(f,"sentence").Text;
    Capture(f,"08-correction","消息解释：修改识别原文","消息与查词");Call(f,"SetSourceEditorVisible",false);
    Field<TextBox>(f,"body").Text="连接暂时不可用，请重试。原句仍可修改或选词。";
    Field<Label>(f,"status").Text="解释失败 · 状态预览";
    Capture(f,"09-error","消息解释：失败状态","消息与查词");f.Hide();
   }
   using(var f=new ManualLookupForm(null,(term,refresh)=>new LookupResponse{explanation="检索增强生成：先查找相关资料，再组织回答。",lookup_mode="model"})) {
    Field<TextBox>(f,"query").Text="RAG";Field<TextBox>(f,"body").Text="检索增强生成：先查找相关资料，再组织回答。";
    Field<Label>(f,"notice").Text="界面示例 · 未发送查询";Capture(f,"10-manual","独立主动查词窗口","消息与查词");f.Hide();
   }
   using(var f=new DefinitionForm()) { f.ShowDefinition("bootcamp","这里指集中培训或项目集训营。",null,null,new Rectangle(200,200,80,30),false,true);
    Capture(f,"11-definition","兼容查词浮卡","消息与查词");f.Hide(); }
   using(var f=new SelectionActionForm()) {f.Present("RAG",new Point(250,250),new Rectangle(240,220,80,30));
    Capture(f,"12-selection-action","选词后的就地解释按钮","消息与查词");f.Hide();}
   using(var f=new CalendarForm(new HighlightItem {title="参加班会",time_text="10月8号下午5点",start_iso="2026-10-08T17:00",utc_offset="+08:00"},
    p=>new CalendarResponse{ok=false,error="预览不执行请求"},null,null,null)) {
    Capture(f,"13-calendar-missing","日程确认：只补缺失信息","日程与提醒");Field<ComboBox>(f,"durationChoices").SelectedIndex=1;
    Capture(f,"14-calendar-ready","日程确认：待明确创建","日程与提醒");
    Field<Button>(f,"editDraft").PerformClick();Capture(f,"15-calendar-edit","日程确认：修改全部信息","日程与提醒");f.Hide(); }
   var store=new LocalReminderStore(null);store.Create(new LocalReminderRequest {title="参加项目讨论会",start="2026-10-08T17:00",
    end="2026-10-08T18:00",utc_offset="+08:00",lead_minutes=10},new DateTime(2026,9,30,0,0,0,DateTimeKind.Utc));
   Preview(new ReminderListForm(store,null),"16-reminders","本地提醒列表（测试数据）","日程与提醒");
   Preview(new ReminderAlertForm(new LocalReminderItem {title="参加项目讨论会",start="2026-10-08T17:00",utc_offset="+08:00"}),
    "17-reminder-alert","到时提醒浮窗（未启动声音）","日程与提醒");
   Preview(new OutlookCalendarForm(new Dictionary<string,object>{{"title","参加项目讨论会"},{"start","2026-10-08T17:00"},
    {"end","2026-10-08T18:00"},{"utc_offset","+08:00"}},p=>new CalendarResponse{ok=false}),"18-outlook","微软日历连接（实验，未登录）","日程与提醒");
   Preview(new CaptionConsentForm(),"19-caption-consent","启动字幕的发送许可","字幕与历史");
   using(var f=new CaptionLyricForm(null)) {f.ShowAudioLines("Let's review the project schedule.","We will meet on October eighth at five p.m.",0,Target());
    Capture(f,"20-live-caption","实时字幕可拖动悬浮框","字幕与历史");f.Hide();}
   using(var f=new CaptionHistoryForm()) {f.SetEntries(new List<CaptionEntry>{new CaptionEntry{timestamp=new DateTime(2026,9,30,14,0,1),text="Let's review the project schedule."},
    new CaptionEntry{timestamp=new DateTime(2026,9,30,14,0,5),text="We will meet on October eighth at five p.m."}});
    Capture(f,"21-caption-history","字幕记录：回看与选词","字幕与历史");
    Field<FlowLayoutPanel>(f,"explanationPanel").Visible=true;
    Field<LinkLabel>(f,"explanation").Text="译文预览：我们将在10月8日下午5点开会。\n测试数据，未调用翻译服务。";
    Capture(f,"22-caption-translation","字幕记录：按需翻译展开","字幕与历史");f.Hide();}
   using(var f=new CaptionStatusForm()) {f.ShowState("正在收集语音 · 界面示例",Target());Capture(f,"23-caption-status","字幕收集状态提示","字幕与历史");f.Hide();}
   using(var f=new StatusForm()) {f.ShowMessage("请点击一条消息",Target(),60000);Capture(f,"24-click-status","快捷键待选提示","实验与辅助");f.Hide();}
   using(var f=new AssistantPanelForm()) {f.StartForTarget(Target());f.SetItems(new[]{new HighlightItem {term="RAG",kind="concept"},new HighlightItem{term="bootcamp",kind="concept"}});
    Call(f,"SetExpanded",true);Capture(f,"25-assistant","兼容整窗扫描：悬浮助手","实验与辅助");f.Hide();}
   using(var f=new HighlightForm()) {f.SetHighlightBounds(new Rectangle(250,250,170,40),new HighlightItem{term="RAG",kind="concept"});
    Capture(f,"26-highlight","兼容文字位置高亮层","实验与辅助");f.Hide();}
   using(var bitmap=new Bitmap(900,520)) {using(Graphics g=Graphics.FromImage(bitmap)){g.Clear(Color.White);g.DrawString("测试画面：我们正在讨论 RAG 和 bootcamp。",SystemFonts.MessageBoxFont,Brushes.Black,90,100);}
    Preview(new ScanRegionForm(bitmap),"27-scan-region","手动框选识别范围（实验）","实验与辅助");}
   using(var f=new Form {Text="今日模型用量",Size=new Size(680,420),TopMost=true}) {
    f.Controls.Add(new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,
     Text="界面示例：未读取真实账号用量。\r\n\r\n物理请求：0\r\n已报告 tokens：0\r\n未知用量：0\r\n此窗口用于显示服务返回的用量摘要。"});
    Capture(f,"28-usage","模型用量窗口（示例内容）","入口与设置");f.Hide();
   }
   using(var timer=new System.Windows.Forms.Timer {Interval=350}) {
    timer.Tick+=delegate {IntPtr hwnd=NativeMethods.GetForegroundWindow();uint pid;NativeMethods.GetWindowThreadProcessId(hwnd,out pid);
     if(pid!=(uint)System.Diagnostics.Process.GetCurrentProcess().Id)return;
     NativeRect box;if(!NativeMethods.GetWindowRect(hwnd,out box))return;
     using(Bitmap bitmap=new Bitmap(box.Width,box.Height)) {using(Graphics g=Graphics.FromImage(bitmap))g.CopyFromScreen(new Point(box.Left,box.Top),Point.Empty,bitmap.Size);
      bitmap.Save(Path.Combine(output,"29-privacy.png"));}
     Record("29-privacy","隐私与密钥状态提示（示例）","入口与设置");timer.Stop();
     NativeMethods.SendMessage(hwnd,0x0010,IntPtr.Zero,IntPtr.Zero);
    };
    timer.Start();MessageBox.Show("新凭证绑定当前 Windows 用户和对应服务地址。\r\n配置检查未发现明文凭证警告。\r\n查询词和原句不写日志；历史日志不会自动清除。\r\n字幕默认保存本机，可关闭保存或按日期删除。\r\n\r\n此图为示例状态，未读取真实配置。",
     "隐私与密钥状态",MessageBoxButtons.OK,MessageBoxIcon.Information);
   }
  }
  static void Assemble() {
   File.WriteAllLines(Path.Combine(output,"manifest.tsv"),inventory,Encoding.UTF8);
   var html=new StringBuilder("<!doctype html><meta charset='utf-8'><title>实时字典界面图册</title><style>body{font:16px system-ui;background:#eef2f6;color:#152438;margin:30px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:24px}article{background:white;padding:20px;border-radius:12px}img{max-width:100%;max-height:680px;object-fit:contain}h2{font-size:18px}small{color:#58687d}</style><h1>实时字典 · 当前界面图册</h1><p>真实程序界面，隔离测试内容。未调用模型，未登录日历。实验界面不代表稳定性已验收。</p><main>");
   foreach(string row in inventory) {string[] parts=row.Split('\t');string path=Path.Combine(output,parts[0]+".png");
    html.Append("<article><h2>").Append(parts[1]).Append("</h2><small>").Append(parts[2]).Append("</small><p><a href='").Append(parts[0]).Append(".png'><img src='data:image/png;base64,")
     .Append(Convert.ToBase64String(File.ReadAllBytes(path))).Append("'></a></article>");}
   html.Append("</main><p>不展示不可见热键宿主 MessageForm；系统文件选择框、文件资源管理器和外部帮助阅读器不是独立产品页面。用量、隐私使用占位内容。</p>");
   File.WriteAllText(Path.Combine(output,"index.html"),html.ToString(),Encoding.UTF8);
   for(int start=0;start<inventory.Count;start+=6) {using(var sheet=new Bitmap(1200,1360)) using(Graphics g=Graphics.FromImage(sheet)) {
    g.Clear(Color.FromArgb(237,242,248));using(Font title=new Font("Microsoft YaHei UI",18,FontStyle.Bold))
    using(Font label=new Font("Microsoft YaHei UI",12)) {g.DrawString("实时字典界面图册 · "+(start/6+1)+" / "+((inventory.Count+5)/6),title,Brushes.Black,25,15);
     g.DrawString("实际程序窗口 · 测试数据 · 未执行模型请求",label,Brushes.DimGray,25,52);
     for(int i=start;i<Math.Min(start+6,inventory.Count);i++) {string[] p=inventory[i].Split('\t');int x=20+((i-start)%2)*600,y=90+((i-start)/2)*420;
      g.FillRectangle(Brushes.White,x,y,560,398);g.DrawString((i+1)+". "+p[1],label,Brushes.Black,new RectangleF(x+12,y+8,536,45));
      using(Image image=Image.FromFile(Path.Combine(output,p[0]+".png"))) {double scale=Math.Min(530.0/image.Width,335.0/image.Height);
       scale=Math.Min(1,scale);int w=(int)(image.Width*scale),h=(int)(image.Height*scale);g.DrawImage(image,x+(560-w)/2,y+54+(335-h)/2,w,h);}
     }
    }
    sheet.Save(Path.Combine(output,"overview-"+(start/6+1)+".png"));
   }}
  }
  [STAThread] static int Main(string[] args) {
   try {output=args[0];Directory.CreateDirectory(output);NativeMethods.SetProcessDPIAware();Application.EnableVisualStyles();
    if(args.Length>1&&args[1]=="compose") inventory=File.ReadAllLines(Path.Combine(output,"manifest.tsv")).Where(x=>x.Trim().Length>0).ToList();
    else using(var backdrop=new Form {FormBorderStyle=FormBorderStyle.None,Bounds=Screen.PrimaryScreen.Bounds,BackColor=Color.FromArgb(237,242,248)}) {
     backdrop.Show();Application.DoEvents();Screens();backdrop.Hide(); }
    Assemble();Console.WriteLine("Captured "+inventory.Count+" UI states");return 0;
   }catch(Exception ex){Console.WriteLine("UI catalog failed: "+ex.Message);return 1;}
  }
 }
}
