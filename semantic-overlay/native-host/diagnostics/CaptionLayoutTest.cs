using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
namespace SemanticOverlay.NativeHost {
    internal static class CaptionLayoutTest {
        [STAThread] static void Main() {
            using (CaptionLyricForm form = new CaptionLyricForm()) {
                form.SetSourceTop(340);
                NativeRect target = new NativeRect { Left=100, Top=100, Right=1020, Bottom=720 };
                form.ShowLines("same caption", "same caption", target);
                string previous = (string)typeof(CaptionLyricForm).GetField("previousLine", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
                if (previous != "") throw new Exception("Duplicate lyric line");
                if (form.Bottom > target.Top + 340 - 12) throw new Exception("Lyric overlaps source caption");
                int left = form.Left, top = form.Top;
                target.Left+=60; target.Right+=60; target.Top+=40; target.Bottom+=40;
                form.PositionFor(target);
                if (form.Left!=left+60 || form.Top!=top+40) throw new Exception("Lyric did not follow window movement");
                form.Close();
            }
            using (CaptionLyricForm form = new CaptionLyricForm(Path.Combine(
                Path.GetTempPath(), "caption-layout-test-" + Guid.NewGuid().ToString("N") + ".json"))) {
                System.Drawing.Rectangle area = Screen.PrimaryScreen.WorkingArea;
                NativeRect target = new NativeRect {
                    Left = area.Left + 100, Top = area.Bottom - 350,
                    Right = area.Left + Math.Min(900, area.Width - 100),
                    Bottom = area.Bottom + 500
                };
                form.ShowAudioLines("first line", "second line", 0, target);
                if (form.Left < area.Left || form.Right > area.Right ||
                    form.Top < area.Top || form.Bottom > area.Bottom)
                    throw new Exception("Audio card extended beyond the visible monitor");
                form.Close();
            }
            Console.WriteLine("PASS caption layout: no duplicate, follows target, audio card stays on screen");
        }
    }
}
