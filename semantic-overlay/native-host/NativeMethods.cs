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
    internal sealed class AnalysisEntity
    {
        public string text { get; set; }
        public string type { get; set; }
        public int start { get; set; }
        public int end { get; set; }
        public string title { get; set; }
        public string time_text { get; set; }
        public string start_iso { get; set; }
        public string end_iso { get; set; }
        public string utc_offset { get; set; }
        public bool needs_confirmation { get; set; }
    }

    internal sealed class HighlightItem
    {
        public string term { get; set; }
        public string context { get; set; }
        public string kind { get; set; }
        public string title { get; set; }
        public string time_text { get; set; }
        public string start_iso { get; set; }
        public string end_iso { get; set; }
        public string utc_offset { get; set; }
        public bool needs_confirmation { get; set; }
        public int x { get; set; }
        public int y { get; set; }
        public int w { get; set; }
        public int h { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Contains(int x, int y)
        {
            return x >= Left && x < Right && y >= Top && y < Bottom;
        }
    }

    internal static class NativeMethods
    {
        public const int EmGetFirstVisibleLine = 0x00CE;
        public const int EmLineScroll = 0x00B6;
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int message,
            IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        public const int WmHotkey = 0x0312;
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();
        public const int WmMouseWheel = 0x020A;
        public const int WmMouseActivate = 0x0021;
        public const int MaNoActivate = 3;
        public const int WmLButtonUp = 0x0202;
        public const int WhMouseLl = 14;
        public const int WsExTransparent = 0x00000020;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExNoActivate = 0x08000000;
        public const int CsDropShadow = 0x00020000;
        public const int SwShowNoActivate = 4;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpShowWindow = 0x0040;
        public static readonly IntPtr HwndTopMost = new IntPtr(-1);

        public const uint EventObjectShow = 0x8002;
        public const uint EventObjectValueChange = 0x800E;
        public const uint EventObjectLocationChange = 0x800B;
        public const uint EventObjectNameChange = 0x800C;
        public const int ObjIdWindow = 0;
        public const uint WinEventOutOfContext = 0x0000;
        public const uint WinEventSkipOwnProcess = 0x0002;

        public delegate void WinEventDelegate(
            IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId,
            uint eventThread, uint eventTime);
        public delegate IntPtr MouseHookDelegate(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseHookData
        {
            public NativePoint point;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int virtualKey);
        public const int VkMenu = 0x12;
        [DllImport("user32.dll")]
        public static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(
            IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        public static extern IntPtr BeginDeferWindowPos(int numberOfWindows);
        [DllImport("user32.dll")]
        public static extern IntPtr DeferWindowPos(
            IntPtr positionInfo, IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        public static extern bool EndDeferWindowPos(IntPtr positionInfo);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
            uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowsHookEx(
            int hookId, MouseHookDelegate callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(
            IntPtr hook, int code, IntPtr message, IntPtr data);
    }}
