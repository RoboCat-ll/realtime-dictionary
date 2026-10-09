using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SemanticOverlay.NativeHost
{
    internal static class FivePointFlowTest
    {
        static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        static void Pump(Func<bool> done) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Check(done(), "UI callback timed out");
        }
        [STAThread] static int Main(string[] args) {
            Application.EnableVisualStyles();
            ServiceManager.DisableUsageMetricsForDiagnostics = true;
            ServiceManager.DisableFloatSizeRestoreForDiagnostics = true;
            try {
                var primary = new List<OcrWord> { new OcrWord { text="A℃C", x=10,y=10,w=50,h=20 },
                    new OcrWord { text="ACC",x=80,y=10,w=50,h=20 },
                    new OcrWord { text="30℃",x=140,y=10,w=50,h=20 } };
                var english = new List<OcrWord> { new OcrWord { text="AIGC",x=10,y=10,w=50,h=20 },
                    new OcrWord { text="AIGC",x=80,y=10,w=50,h=20 },
                    new OcrWord { text="ABC",x=140,y=10,w=50,h=20 } };
                primary = ServiceManager.RepairLatinOcrArtifacts(primary, english);
                Check(primary[0].text=="AIGC" && primary[1].text=="ACC" && primary[2].text=="30℃",
                    "Local Latin repair changes legitimate abbreviation or temperature");
                primary[0].text="A℃C"; english[0].x=25;
                primary = ServiceManager.RepairLatinOcrArtifacts(primary, english);
                Check(primary[0].text=="A℃C", "Neighboring or mismatched box repaired");
                english[0].x=10; english.Add(english[0]);
                primary = ServiceManager.RepairLatinOcrArtifacts(primary, english);
                Check(primary[0].text=="A℃C", "Ambiguous English candidates repaired");
                if (args.Length > 0) {
                    string image = args[0];
                    if (args.Length > 1 && args[1] == "frame") {
                        using (var frame = new Bitmap(image)) {
                            Rectangle bubble;
                            Check(MessageBubbleDetector.FindInBitmap(frame, new Point(1270,420), out bubble), "Failure bubble not found");
                            bubble.Inflate(6,5); bubble.Intersect(new Rectangle(Point.Empty,frame.Size));
                            Console.WriteLine("failure-bubble="+bubble);
                            using (var crop = frame.Clone(bubble, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                            using (var enlarged = new Bitmap((int)Math.Round(bubble.Width*1.7), (int)Math.Round(bubble.Height*1.7)))
                            using (var drawing = Graphics.FromImage(enlarged)) {
                                drawing.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                drawing.DrawImage(crop,new Rectangle(Point.Empty,enlarged.Size),new Rectangle(Point.Empty,crop.Size),GraphicsUnit.Pixel);
                                image=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"rtd-ocr-failure-exact.png");
                                enlarged.Save(image);
                            }
                        }
                    }
                    ServiceManager.PreferencePathOverrideForDiagnostics = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "rtd-ocr-probe-"+Guid.NewGuid().ToString("N")+".json");
                    using (var service = new ServiceManager()) {
                        var read = typeof(ServiceManager).GetMethod("ReadWindowsOcrWords",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        var raw = (List<OcrWord>)read.Invoke(service, new object[] { image, false });
                        var repaired = (List<OcrWord>)read.Invoke(service, new object[] { image, true });
                        Console.WriteLine("local-crop-primary=" + String.Join("|",raw.ConvertAll(value=>value.text).ToArray()));
                        Console.WriteLine("local-crop-mixed=" + String.Join("|",repaired.ConvertAll(value=>value.text).ToArray()));
                        Check(repaired.Exists(value=>value.text=="AIGC"), "Real failure crop not recovered");
                    }
                    ServiceManager.PreferencePathOverrideForDiagnostics=null;
                }
                int calls = 0;
                bool lastCorrection = false;
                using (var form = new SelectionAnalysisForm(null, null, null,
                    delegate(string text, bool correction, bool refresh) {
                        calls++; lastCorrection = correction;
                        return new SelectionAnalysisResponse { analysis_mode = "model", explanation = "语境释义",
                            display_text = text, ocr_corrected = correction };
                    })) {
                    form.OpenText("讨论 ACC", false, "bubble_ocr", "wechat", true);
                    Pump(() => Field<Button>(form, "analyze").Enabled && calls == 1);
                    Check(lastCorrection && Field<Label>(form, "sentenceTitle").Text.Contains("仍请核对"),
                        "OCR correction was represented as certain source");
                    Field<TextBox>(form, "source").Text = "讨论 AIGC";
                    var task = (Task)typeof(SelectionAnalysisForm).GetMethod("RunAnalysis",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { true });
                    Pump(() => task.IsCompleted && Field<Button>(form, "analyze").Enabled);
                    Check(calls == 2 && !lastCorrection && Field<RichTextBox>(form, "sentence").Text == "讨论 AIGC" &&
                        Field<Label>(form, "sentenceTitle").Text.Contains("手动修改"),
                        "User source correction was not authoritative");
                    form.Close();
                }
                using (var form = new CalendarForm(new HighlightItem { title = "参加班会",
                    start_iso = "2027-10-08T17:00", utc_offset = "+08:00" }, null, null, null, null)) {
                    form.Show(); Application.DoEvents();
                    var duration = Field<ComboBox>(form, "durationChoices");
                    Check(duration.Visible && !Field<TextBox>(form, "end").Visible &&
                        Field<TextBox>(form, "end").Text == "" && duration.SelectedIndex == -1,
                        "Only-missing-duration flow duplicates inputs or guesses duration");
                    foreach (Control control in Field<List<Control>>(form, "clarificationInputs"))
                        Check(!control.Visible, "Redundant freeform clarification is shown");
                    duration.SelectedIndex = 3;
                    Check(Field<TextBox>(form, "end").Visible && !Field<TextBox>(form, "start").Visible,
                        "Manual end entry exposes unrelated prefilled fields");
                    duration.SelectedIndex = 1;
                    Check(Field<TextBox>(form, "end").Text == "2027-10-08T18:00" && !duration.Visible &&
                        Field<Label>(form, "draftSummary").Text.Contains("18:00"), "Duration failed to prefill summary");
                    form.Close();
                }
                var chunk = new CaptionAudioChunk { Attempts = 2 };
                int terminals = 0;
                chunk.Complete("transcript", delegate(string outcome, int elapsed, int attempts) {
                    Check(outcome == "transcript" && attempts == 2 && elapsed >= 0,
                        "Caption timing/retry outcome not usable"); terminals++;
                });
                chunk.Complete("gap", delegate { terminals++; });
                Check(terminals == 1, "One retried chunk counted as multiple speech outcomes");
                Console.WriteLine("five-point-ocr-source-user-edit-duration-and-caption-outcomes-ok");
                return 0;
            } catch (Exception error) { Console.WriteLine(error); return 1; }
        }
    }
}
