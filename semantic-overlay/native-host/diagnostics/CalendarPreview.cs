using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class CalendarPreview
    {
        [STAThread]
        private static void Main(string[] args)
        {
            NativeMethods.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServiceManager services = new ServiceManager();
            CalendarForm form = new CalendarForm(new HighlightItem {
                title = "与 oneAPI 同事讨论 bootcamp", time_text = "9月3号下午14:00"
            }, services.ExportCalendar);
            Func<string, object> field = name => typeof(CalendarForm).GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
            Timer timer = new Timer { Interval = 1500 };
            int step = 0;
            timer.Tick += delegate
            {
                if (step++ == 0) { ((Button)field("export")).PerformClick(); return; }
                if (((Label)field("status")).Text.IndexOf("开始时间") < 0)
                    throw new Exception("Empty date was not visibly rejected");
                ((TextBox)field("start")).Text = "2026-09-20T14:00";
                ((TextBox)field("end")).Text = "2026-09-20T15:00";
                ((TextBox)field("offset")).Text = "+08:00";
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(args[0], ImageFormat.Png);
                }
                timer.Stop();
                form.Close();
            };
            timer.Start();
            Application.Run(form);
            timer.Dispose();
            services.Dispose();
        }
    }
}
