using System;
using System.Drawing;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class AudioLyricRangeTest
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            using (CaptionLyricForm lyric = new CaptionLyricForm())
            {
                NativeRect target = new NativeRect { Left=100, Top=100, Right=1300, Bottom=850 };
                string text = "我们明天下午和 OneAPI 的同事举行 bootcamp 讨论会。";
                lyric.ShowLines("", text, target);
                Application.DoEvents();
                Rectangle oneApi, bootcamp;
                if (!lyric.TryGetCurrentRange(text.IndexOf("OneAPI"), 6, out oneApi) ||
                    !lyric.TryGetCurrentRange(text.IndexOf("bootcamp"), 8, out bootcamp))
                    throw new Exception("Term ranges were not measurable");
                if (!lyric.Bounds.Contains(oneApi) || !lyric.Bounds.Contains(bootcamp) || oneApi.IntersectsWith(bootcamp))
                    throw new Exception("Term ranges were outside lyric or overlapping");
                lyric.Hide();
                if (lyric.TryGetCurrentRange(0, 2, out oneApi))
                    throw new Exception("Hidden lyric exposed clickable term range");
            }
            Console.WriteLine("PASS audio lyric term rectangles are bounded and disappear when hidden");
        }
    }
}
