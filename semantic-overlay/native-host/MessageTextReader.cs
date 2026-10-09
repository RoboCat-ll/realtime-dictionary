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
    internal sealed class MessageProbeResult
    {
        internal string Text { get; set; }
        internal Rectangle Bounds { get; set; }
        internal string Source { get; set; }
        internal bool Exact { get; set; }
    }

    internal static class MessageTextReader
    {
        private static int accessibilityReadBusy;

        internal static MessageProbeResult ReadAtPointBounded(Point point, Rectangle window,
            int maxLength)
        {
            return ReadBounded(delegate { return ReadAtPoint(point, window, maxLength); }, 400);
        }

        internal static MessageProbeResult ReadBounded(Func<MessageProbeResult> read, int timeoutMs)
        {
            if (Interlocked.CompareExchange(ref accessibilityReadBusy, 1, 0) != 0) return null;
            MessageProbeResult result = null;
            var worker = new Thread(delegate()
            {
                try { result = read(); }
                catch { }
                finally { Interlocked.Exchange(ref accessibilityReadBusy, 0); }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            try { worker.Start(); }
            catch { Interlocked.Exchange(ref accessibilityReadBusy, 0); return null; }
            return worker.Join(Math.Max(1, timeoutMs)) ? result : null;
        }

        internal static MessageProbeResult ReadAtPoint(Point point, Rectangle window, int maxLength)
        {
            try
            {
                var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(
                    RuntimeEnvironment.GetRuntimeDirectory(), "WPF", "UIAutomationClient.dll"));
                Type elementType = assembly.GetType("System.Windows.Automation.AutomationElement");
                Type textPatternType = assembly.GetType("System.Windows.Automation.TextPattern");
                Type valuePatternType = assembly.GetType("System.Windows.Automation.ValuePattern");
                Type walkerType = assembly.GetType("System.Windows.Automation.TreeWalker");
                Type pointType = ResolvePointType(elementType);
                object automationPoint = Activator.CreateInstance(pointType,
                    new object[] { (double)point.X, (double)point.Y });
                object element = elementType.GetMethod("FromPoint").Invoke(null,
                    new object[] { automationPoint });
                object walker = ResolveControlViewWalker(walkerType);
                object best = null;
                string bestText = null;
                Rectangle bestBounds = Rectangle.Empty;
                int bestScore = Int32.MinValue;
                for (int depth = 0; element != null && depth < 9; depth++)
                {
                    object current = elementType.GetProperty("Current").GetValue(element, null);
                    bool password = (bool)current.GetType().GetProperty("IsPassword").GetValue(current, null);
                    if (!password)
                    {
                        Rectangle bounds = ReadBounds(current);
                        string controlType = Convert.ToString(
                            current.GetType().GetProperty("ControlType").GetValue(current, null));
                        string text = ReadPatternText(element, elementType, textPatternType, maxLength);
                        bool patternText = !String.IsNullOrWhiteSpace(text);
                        if (!patternText)
                            text = ReadPatternText(element, elementType, valuePatternType, maxLength);
                        if (String.IsNullOrWhiteSpace(text))
                            text = Convert.ToString(current.GetType().GetProperty("Name").GetValue(current, null));
                        text = Normalize(text);
                        if (IsCandidate(text, controlType, bounds, window, point, maxLength))
                        {
                            int areaPenalty = Math.Max(0, bounds.Width * bounds.Height / 5000);
                            int score = Math.Min(text.Length, maxLength + 1) * 100 - areaPenalty +
                                (patternText ? 500 : 0);
                            if (score > bestScore)
                            {
                                best = element; bestText = text; bestBounds = bounds; bestScore = score;
                            }
                        }
                    }
                    element = walkerType.GetMethod("GetParent", new Type[] { elementType })
                        .Invoke(walker, new object[] { element });
                }
                if (best == null) return null;
                return new MessageProbeResult { Text = bestText, Bounds = bestBounds,
                    Source = "message_accessibility", Exact = true };
            }
            catch { return null; }
        }

        internal static Type ResolvePointType(Type elementType)
        {
            // WindowsBase may live alongside UIAutomationClient rather than in the
            // default assembly search path. Use the API's own parameter type.
            var fromPoint = elementType == null ? null : elementType.GetMethod("FromPoint");
            var parameters = fromPoint == null ? null : fromPoint.GetParameters();
            return parameters != null && parameters.Length == 1
                ? parameters[0].ParameterType : null;
        }

        internal static object ResolveControlViewWalker(Type walkerType)
        {
            if (walkerType == null) return null;
            var field = walkerType.GetField("ControlViewWalker");
            if (field != null) return field.GetValue(null);
            var property = walkerType.GetProperty("ControlViewWalker");
            return property == null ? null : property.GetValue(null, null);
        }

        private static string ReadPatternText(object element, Type elementType,
            Type patternType, int maxLength)
        {
            if (patternType == null) return null;
            try
            {
                object id = patternType.GetField("Pattern").GetValue(null);
                object pattern = elementType.GetMethod("GetCurrentPattern").Invoke(element,
                    new object[] { id });
                if (patternType.Name == "TextPattern")
                {
                    object range = patternType.GetProperty("DocumentRange").GetValue(pattern, null);
                    return Convert.ToString(range.GetType().GetMethod("GetText").Invoke(range,
                        new object[] { maxLength + 1 }));
                }
                object current = patternType.GetProperty("Current").GetValue(pattern, null);
                return Convert.ToString(current.GetType().GetProperty("Value").GetValue(current, null));
            }
            catch { return null; }
        }

        private static Rectangle ReadBounds(object current)
        {
            try
            {
                object rect = current.GetType().GetProperty("BoundingRectangle").GetValue(current, null);
                Type type = rect.GetType();
                return Rectangle.Round(new RectangleF(
                    Convert.ToSingle(type.GetProperty("X").GetValue(rect, null)),
                    Convert.ToSingle(type.GetProperty("Y").GetValue(rect, null)),
                    Convert.ToSingle(type.GetProperty("Width").GetValue(rect, null)),
                    Convert.ToSingle(type.GetProperty("Height").GetValue(rect, null))));
            }
            catch { return Rectangle.Empty; }
        }

        internal static string Normalize(string text)
        {
            string source = (text ?? String.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return Regex.Replace(source, @"[^\S\n]+", " ").Trim();
        }

        internal static bool FitsBubble(Rectangle bounds, Rectangle bubble)
        {
            if (bubble.IsEmpty) return true;
            bubble.Inflate(8, 8);
            return !bounds.IsEmpty && bubble.Contains(bounds);
        }

        internal static bool IsCandidate(string text, string controlType, Rectangle bounds,
            Rectangle window, Point point, int maxLength)
        {
            if (String.IsNullOrWhiteSpace(text) || bounds.IsEmpty ||
                !bounds.Contains(point) || !window.IntersectsWith(bounds)) return false;
            string kind = (controlType ?? String.Empty).ToLowerInvariant();
            if (kind.Contains("edit") || kind.Contains("button") || kind.Contains("menu") ||
                kind.Contains("titlebar") || kind.Contains("toolbar") || kind.Contains("image")) return false;
            if (bounds.Height > Math.Min(600, window.Height * 3 / 4) ||
                bounds.Width > window.Width * 97 / 100) return false;
            if (text.Length <= 10 && Regex.IsMatch(text,
                @"^(发送|表情|图片|文件|语音|搜索|更多|关闭|最小化|最大化)$")) return false;
            if (text.Length <= 30 && Regex.IsMatch(text,
                @"^\s*(?:\d{4}[/\-.年])?\d{1,2}[/\-.月]\d{1,2}(?:日)?(?:\s+\d{1,2}:\d{2})?\s*$"))
                return false;
            return text.Length <= maxLength;
        }
    }

    internal static class MessageBubbleDetector
    {
        internal static bool TryFind(Rectangle window, Point click, out Rectangle bubble)
        {
            bubble = Rectangle.Empty;
            Rectangle capture = Rectangle.Intersect(window,
                new Rectangle(click.X - 800, click.Y - 300, 1600, 600));
            if (capture.Width < 100 || capture.Height < 60) return false;
            using (Bitmap bitmap = new Bitmap(capture.Width, capture.Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(capture.Left, capture.Top, 0, 0, capture.Size,
                    CopyPixelOperation.SourceCopy);
                Rectangle local;
                if (!FindInBitmap(bitmap,
                    new Point(click.X - capture.Left, click.Y - capture.Top), out local)) return false;
                bubble = Rectangle.FromLTRB(capture.Left + local.Left, capture.Top + local.Top,
                    capture.Left + local.Right, capture.Top + local.Bottom);
                bubble.Inflate(6, 5);
                bubble.Intersect(window);
                return bubble.Width >= 80 && bubble.Height >= 24;
            }
        }

        internal static bool FindInBitmap(Bitmap bitmap, Point click, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (bitmap == null || click.X < 0 || click.Y < 0 ||
                click.X >= bitmap.Width || click.Y >= bitmap.Height) return false;
            Rectangle dataBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(dataBounds, ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bitmap.UnlockBits(data);

            Dictionary<int, int[]> colors = new Dictionary<int, int[]>();
            for (int y = Math.Max(0, click.Y - 20); y <= Math.Min(bitmap.Height - 1, click.Y + 20); y++)
                for (int x = Math.Max(0, click.X - 20); x <= Math.Min(bitmap.Width - 1, click.X + 20); x++)
                {
                    int offset = y * data.Stride + x * 4;
                    int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                    if (r + g + b < 90) continue;
                    int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                    int[] value;
                    if (!colors.TryGetValue(key, out value))
                    {
                        value = new int[4]; colors[key] = value;
                    }
                    value[0]++; value[1] += r; value[2] += g; value[3] += b;
            }
            if (colors.Count == 0) return false;
            int total = bitmap.Width * bitmap.Height;
            Rectangle bestBounds = Rectangle.Empty;
            int bestCount = 0;
            foreach (int[] candidate in colors.Values
                .OrderByDescending(value => value[0]).Take(6))
            {
                int red = candidate[1] / candidate[0], green = candidate[2] / candidate[0],
                    blue = candidate[3] / candidate[0];
                Rectangle component;
                int count;
                if (!TryComponent(pixels, data.Stride, bitmap.Width, bitmap.Height,
                    click, red, green, blue, out component, out count)) continue;
                int area = component.Width * component.Height;
                if (count < 500 || component.Width < 70 || component.Height < 20 ||
                    component.Height > 420 ||
                    (area > total * 3 / 4 && count > total / 2)) continue;
                if (count > bestCount)
                {
                    bestCount = count;
                    bestBounds = component;
                }
            }
            bounds = bestBounds;
            return bestCount > 0;
        }

        private static bool TryComponent(byte[] pixels, int stride, int width, int height,
            Point click, int red, int green, int blue, out Rectangle bounds, out int count)
        {
            bounds = Rectangle.Empty;
            count = 0;
            int total = width * height;
            bool[] visited = new bool[total];
            int[] queue = new int[total];
            // Text glyphs can enclose a tiny island of the bubble color. Inspect
            // each nearby component instead of accepting only the nearest seed.
            for (int seedY = Math.Max(0, click.Y - 36); seedY <= Math.Min(height - 1, click.Y + 36); seedY++)
                for (int seedX = Math.Max(0, click.X - 36); seedX <= Math.Min(width - 1, click.X + 36); seedX++)
                {
                    int seedIndex = seedY * width + seedX;
                    if (visited[seedIndex] || !Close(pixels, seedY * stride + seedX * 4, red, green, blue)) continue;
                    int head = 0, tail = 0, componentCount = 0;
                    queue[tail++] = seedIndex; visited[seedIndex] = true;
                    int minX = seedX, maxX = seedX, minY = seedY, maxY = seedY;
                    while (head < tail)
                    {
                        int index = queue[head++];
                        int x = index % width, y = index / width;
                        if (!Close(pixels, y * stride + x * 4, red, green, blue)) continue;
                        componentCount++;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                        if (x > 0) Enqueue(index - 1, visited, queue, ref tail);
                        if (x + 1 < width) Enqueue(index + 1, visited, queue, ref tail);
                        if (y > 0) Enqueue(index - width, visited, queue, ref tail);
                        if (y + 1 < height) Enqueue(index + width, visited, queue, ref tail);
                    }
                    Rectangle component = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                    Rectangle hitBounds = component;
                    hitBounds.Inflate(4, 4);
                    if (componentCount > count && hitBounds.Contains(click))
                    {
                        count = componentCount;
                        bounds = component;
                    }
                }
            return count > 0;
        }
        private static void Enqueue(int index, bool[] visited, int[] queue, ref int tail)
        {
            if (visited[index]) return;
            visited[index] = true;
            queue[tail++] = index;
        }

        private static bool Close(byte[] pixels, int offset, int red, int green, int blue)
        {
            int dr = Math.Abs(pixels[offset + 2] - red);
            int dg = Math.Abs(pixels[offset + 1] - green);
            int db = Math.Abs(pixels[offset] - blue);
            return dr <= 18 && dg <= 18 && db <= 18 && dr + dg + db <= 36;
        }
    }

}
