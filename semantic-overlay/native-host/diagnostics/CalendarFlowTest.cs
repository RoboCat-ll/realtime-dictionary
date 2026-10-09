using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal static class CalendarFlowTest
    {
        static void Assert(bool condition, string text) { if (!condition) throw new Exception(text); }
        static IEnumerable<Control> Descendants(Control root) {
            foreach (Control child in root.Controls) {
                yield return child;
                foreach (Control nested in Descendants(child)) yield return nested;
            }
        }
        static void PumpUntil(Func<bool> done) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(done(), "UI operation timed out");
        }
        [STAThread]
        static void Main() {
            try { RunTests(); }
            catch (Exception error) { Console.WriteLine("FAIL Calendar UI: " + error.Message); Environment.ExitCode = 1; }
        }
        static void RunTests() {
            Control.CheckForIllegalCrossThreadCalls = true;
            Application.EnableVisualStyles();
            string checkState = "clear";
            int exports = 0, clarifications = 0;
            string lastDraftSource = "";
            using (var beijing = new CalendarForm(new HighlightItem {
                title = "班会", start_iso = "2026-10-08T17:00", end_iso = "2026-10-08T18:00"
            }, null, null, null, null)) {
                beijing.Show(); Application.DoEvents();
                var zone = (TextBox)typeof(CalendarForm).GetField("offset",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(beijing);
                Assert(zone.Text == "+08:00" && !zone.Visible,
                    "Beijing default missing or timezone input exposed");
                Descendants(beijing).OfType<Button>().Single(b => b.Text == "修改已识别信息").PerformClick();
                Assert(!zone.Visible && !Descendants(beijing).OfType<Label>().Any(l => l.Visible && l.Text.Contains("时区")),
                    "Expanded draft still asks for timezone");
                beijing.Close();
            }
            using (CalendarForm form = new CalendarForm(new HighlightItem {
                title = "OneAPI bootcamp", time_text = "9月3号下午14:00"
            }, payload => {
                string operation = payload.ContainsKey("operation") ? (string)payload["operation"] : "export";
                if (operation == "clarify") {
                    clarifications++;
                    lastDraftSource = (string)payload["time_text"];
                    return new CalendarResponse { ok=true, start="2027-09-03T14:00", end="2027-09-03T15:00", utc_offset="+08:00", message="请核对草稿" };
                }
                if (operation == "check") return new CalendarResponse { ok=true, state=checkState, message=checkState };
                exports++;
                throw new Exception("Export must not occur without confirmation");
            }, null, null, null)) {
                Func<string, object> field = name => typeof(CalendarForm).GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                TextBox start = (TextBox)field("start"), end = (TextBox)field("end");
                Button check = (Button)field("check"), export = (Button)field("export");
                form.Show(); Application.DoEvents();
                Button clarify = Descendants(form).OfType<Button>().Single(b => b.Text == "整理补充信息");
                PumpUntil(() => clarify.Enabled && clarifications == 1);
                Assert(clarifications == 1 && start.Text == "2027-09-03T14:00" && end.Text == "2027-09-03T15:00", "Clarification did not fill proposal");
                Assert(!export.Enabled, "Clarification exported or skipped check");
                Assert(!start.Visible && !end.Visible, "Known dates should be summarized, not repeated as inputs");
                Descendants(form).OfType<Button>().Single(b => b.Text == "修改已识别信息").PerformClick();
                Assert(start.Visible && end.Visible, "Explicit edit must reveal known fields");
                clarify.PerformClick(); PumpUntil(() => clarify.Enabled && clarifications == 2);
                Assert(clarifications == 2, "Explicit clarification must remain available");
                start.Text = "2027-09-04T18:00";
                clarify.PerformClick();
                PumpUntil(() => clarify.Enabled && clarifications >= 3);
                Assert(lastDraftSource.Contains("2027年9月4号18:00") && lastDraftSource.Contains("UTC+08:00"),
                    "Supplement must parse the currently edited draft, not the original message date");
                Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("其他方式：")).PerformClick();
                Application.DoEvents();
                check.PerformClick(); PumpUntil(() => check.Enabled);
                Assert(export.Enabled, "Clear check did not enable explicit confirmation");
                start.Text = "2027-09-03T16:00";
                Assert(!export.Enabled, "Edit did not invalidate conflict check");
                foreach (string state in new[] { "duplicate", "incomplete" }) {
                    checkState = state; check.PerformClick(); PumpUntil(() => check.Enabled);
                    Assert(!export.Enabled, state + " incorrectly enabled export");
                }
                Assert(exports == 0, "Unexpected export");
                form.Close();
            }
            using (var form = new CalendarForm(new HighlightItem {
                title = "班会", time_text = "10月8号17:00", end_iso = "2026-10-08T18:00", utc_offset = "+08:00"
            }, payload => new CalendarResponse { ok = true, start = "2026-10-08T17:00", end = "",
                utc_offset = "", message = "按当前日期预填，请核对" }, null, null, null)) {
                form.Show();
                Func<string, TextBox> field = name => (TextBox)typeof(CalendarForm).GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                PumpUntil(() => field("start").Text != "");
                Assert(field("end").Text == "2026-10-08T18:00" && field("offset").Text == "+08:00",
                    "Initial clarification erased existing fields");
                Assert(Descendants(form).OfType<Label>().Any(l => l.Text.Contains("预填")), "Inference basis not visible");
                Button clarify = Descendants(form).OfType<Button>().Single(b => b.Text == "整理补充信息");
                clarify.PerformClick(); PumpUntil(() => clarify.Enabled);
                Assert(field("end").Text == "2026-10-08T18:00" && field("offset").Text == "+08:00",
                    "Explicit supplement must not erase known fields when parser returns blanks");
                var choices = (ComboBox)typeof(CalendarForm).GetField("durationChoices",
                    BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                choices.SelectedIndex = 1;
                field("start").Text = "2026-10-08T19:00";
                Assert(field("end").Text == "2026-10-08T20:00", "Chosen duration must follow a start edit");
                field("end").Text = "2026-10-08T21:30";
                field("start").Text = "2026-10-08T20:00";
                Assert(field("end").Text == "2026-10-08T21:30" && choices.SelectedIndex == -1,
                    "Manual end edit must cancel automatic duration tracking");
                choices.SelectedIndex = 0;
                field("start").Text = "invalid";
                Assert(field("end").Text == "", "Invalid start must not retain a stale generated end");
                form.Close();
            }
            Console.WriteLine("PASS Calendar UI: missing fields, clarification, check, edit invalidation, duplicate/incomplete block, no unconfirmed export");
        }
    }
}
