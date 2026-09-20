using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SemanticOverlay.Diagnostics
{
    internal static class HotkeyBlocker
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private static int Main()
        {
            const uint controlAltNoRepeat = 0x0002 | 0x0001 | 0x4000;
            if (!RegisterHotKey(IntPtr.Zero, 1, controlAltNoRepeat, 0x4B))
                return Marshal.GetLastWin32Error();
            try
            {
                Thread.Sleep(15000);
                return 0;
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, 1);
            }
        }
    }
}
