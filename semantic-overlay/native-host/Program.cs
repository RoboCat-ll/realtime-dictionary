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
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (EventWaitHandle activation = new EventWaitHandle(false,
                EventResetMode.AutoReset, "Local\\RealtimeDictionary.NativeHost.Activate.v1"))
            using (Mutex mutex = new Mutex(true, "Local\\RealtimeDictionary.NativeHost.v1", out created))
            {
                if (!created)
                {
                    activation.Set();
                    return;
                }
                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var context = new OverlayContext())
                using (var activationTimer = new System.Windows.Forms.Timer { Interval = 250 })
                {
                    activationTimer.Tick += delegate {
                        if (activation.WaitOne(0)) context.RevealRunningApplication();
                    };
                    activationTimer.Start();
                    Application.Run(context);
                }
            }
        }
    }

}
