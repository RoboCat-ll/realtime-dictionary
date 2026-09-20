using System;
using System.Threading;
using System.Windows.Forms;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace SemanticOverlay.NativeHost
{
    internal static class CaptionHost
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(
                true, "Local\\RealtimeDictionary.NativeHost.v1", out created))
            {
                if (!created)
                    return;
                ServiceManager.WorkModeOverrideForDiagnostics = "caption";
                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (OverlayContext context = new OverlayContext())
                using (System.Windows.Forms.Timer inspection = new System.Windows.Forms.Timer { Interval = 120 })
                {
                    DateTime started = DateTime.UtcNow;
                    bool captured = false;
                    inspection.Tick += delegate {
                        if (captured || (DateTime.UtcNow - started).TotalSeconds < 10) return;
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        CaptionLyricForm lyric = (CaptionLyricForm)typeof(OverlayContext).GetField("captionLyricWindow", flags).GetValue(context);
                        IntPtr target = (IntPtr)typeof(OverlayContext).GetField("targetWindow", flags).GetValue(context);
                        if (!lyric.Visible || target == IntPtr.Zero || NativeMethods.GetForegroundWindow() != target) return;
                        uint owner;
                        NativeMethods.GetWindowThreadProcessId(target, out owner);
                        if (Process.GetProcessById((int)owner).ProcessName != "CaptionTarget") return;
                        NativeRect rect = (NativeRect)typeof(OverlayContext).GetField("targetRect", flags).GetValue(context);
                        string previous = (string)typeof(CaptionLyricForm).GetField("previousLine", flags).GetValue(lyric);
                        string current = (string)typeof(CaptionLyricForm).GetField("currentLine", flags).GetValue(lyric);
                        if (String.IsNullOrEmpty(current)) return;
                        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..",".."));
                        using (Bitmap bitmap = new Bitmap(rect.Width, rect.Height))
                        using (Graphics graphics = Graphics.FromImage(bitmap)) {
                            NativeMethods.DwmFlush();
                            graphics.CopyFromScreen(rect.Left,rect.Top,0,0,bitmap.Size);
                            bitmap.Save(Path.Combine(root,"tests","caption-verified-preview.png"),ImageFormat.Png);
                        }
                        // Only synthetic target metrics are retained; never write caption content.
                        File.WriteAllText(Path.Combine(root,"tests","caption-layout-observation.json"),
                            new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                                observed_at=DateTime.Now.ToString("o"), visible=true,
                                duplicate_lines=String.Equals(previous,current,StringComparison.Ordinal),
                                transparent_background=lyric.TransparencyKey==lyric.BackColor,
                                previous_length=previous.Length, current_length=current.Length,
                                lyric_height=lyric.Height, target_height=rect.Height
                            }));
                        captured=true;
                    };
                    inspection.Start();
                    Application.Run(context);
                }
            }
        }
    }
}
