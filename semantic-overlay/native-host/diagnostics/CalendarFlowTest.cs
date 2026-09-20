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
            Application.EnableVisualStyles();
            string checkState = "clear";
            int exports = 0, clarifications = 0;
            using (CalendarForm form = new CalendarForm(new HighlightItem {
                title = "OneAPI bootcamp", time_text = "9月3号下午14:00"
            }, payload => {
                string operation = payload.ContainsKey("operation") ? (string)payload["operation"] : "export";
                if (operation == "clarify") {
                    clarifications++;
                    return new CalendarResponse { ok=true, start="2027-09-03T14:00", end="2027-09-03T15:00", utc_offset="+08:00", message="请核对草稿" };
                }
                if (operation == "check") return new CalendarResponse { ok=true, state=checkState, message=checkState };
                exports++;
                throw new Exception("Export must not occur without confirmation");
            })) {
                Func<string, object> field = name => typeof(CalendarForm).GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                TextBox start = (TextBox)field("start"), end = (TextBox)field("end");
                Button check = (Button)field("check"), export = (Button)field("export");
                form.Show(); Application.DoEvents();
                Assert(start.Text == "" && end.Text == "" && !export.Enabled, "Missing dates guessed or export enabled");
                Button clarify = Descendants(form).OfType<Button>().Single(b => b.Text == "整理补充信息");
                clarify.PerformClick();
                PumpUntil(() => clarify.Enabled);
                Assert(clarifications == 1 && start.Text == "2027-09-03T14:00" && end.Text == "2027-09-03T15:00", "Clarification did not fill proposal");
                Assert(!export.Enabled, "Clarification exported or skipped check");
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
            Console.WriteLine("PASS Calendar UI: missing fields, clarification, check, edit invalidation, duplicate/incomplete block, no unconfirmed export");
        }
    }
}
