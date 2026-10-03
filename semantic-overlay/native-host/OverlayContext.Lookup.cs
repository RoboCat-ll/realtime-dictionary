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
    internal sealed partial class OverlayContext : ApplicationContext
    {
        private void BeginAssistantItemAction(HighlightItem item, Rectangle anchor)
        {
            if (!active || item == null)
                return;
            assistantLookupAnchor.SetHighlightBounds(anchor, item);
            BeginItemAction(assistantLookupAnchor, item);
        }

        private void BeginLookup(HighlightForm source, string term, string context)
        {
            if (!active || source == null || string.IsNullOrWhiteSpace(term))
                return;
            lookupHistory.Clear();
            currentLookup = null;
            BeginLookupTerm(source, term.Trim(), context, source.Bounds);
        }

        private void BeginItemAction(HighlightForm source, HighlightItem item)
        {
            if (item == null)
                return;
            if (String.Equals(item.kind, "task", StringComparison.OrdinalIgnoreCase))
            {
                lookupGeneration++;
                lookupHistory.Clear();
                currentLookup = null;
                selectedHighlight = source;
                OpenReminderEditor(item, null);
                services.Log("Reminder confirmation opened");
                return;
            }
            services.NoteTermClicked(item.term);
            BeginLookup(source, item.term, item.context);
        }

        private void OpenReminderEditor(HighlightItem candidate, Form owner)
        {
            if (candidate == null)
                return;
            HideDefinition();
            using (CalendarForm editor = new CalendarForm(
                candidate, services.ExportCalendar, reminders.Create, reminders.ShowList,
                delegate(LocalReminderResult result)
                {
                    UpdateReminderMenu();
                    ShowNotice("提醒已创建，可从托盘的“本地提醒”查看。", ToolTipIcon.Info);
                }))
            {
                if (owner != null && owner.Visible)
                    editor.ShowDialog(owner);
                else
                    editor.ShowDialog();
            }
        }

        private void IgnoreHighlight(HighlightForm source, string term)
        {
            string value = (term ?? String.Empty).Trim();
            if (value.Length == 0)
                return;
            services.IgnoreTerm(value);
            if (selectedHighlight == source)
                HideDefinition();
            relativeHighlights.RemoveAll(delegate(HighlightItem item)
            {
                return String.Equals(item.term, value, StringComparison.OrdinalIgnoreCase);
            });
            RenderHighlights();
            trayStatusItem.Text = "状态：已忽略 1 个词";
            ShowNotice("以后不再标注“" + value + "”；可从托盘恢复。", ToolTipIcon.Info);
        }

        private void BeginNestedLookup(string term)
        {
            if (!active || selectedHighlight == null || string.IsNullOrWhiteSpace(term) ||
                currentLookup == null || lookupHistory.Count >= 3)
                return;
            lookupHistory.Add(currentLookup);
            BeginLookupTerm(
                selectedHighlight,
                term.Trim(),
                currentLookup.explanation,
                definitionWindow.Bounds);
        }

        private void RetryCurrentLookup()
        {
            if (!active || selectedHighlight == null || currentLookup == null ||
                !currentLookup.can_refresh)
                return;
            BeginLookupTerm(
                selectedHighlight,
                currentLookup.term,
                currentLookup.context,
                definitionWindow.Bounds,
                true,
                currentLookup.explanation);
        }

        private async void BeginLookupTerm(
            HighlightForm source,
            string term,
            string context,
            Rectangle anchor,
            bool refresh = false,
            string previousExplanation = null)
        {
            selectedHighlight = source;
            Stopwatch lookupWatch = Stopwatch.StartNew();
            int generation = ++lookupGeneration;
            if (refresh)
                definitionWindow.ShowLoading(term, anchor, lookupHistory.Count > 0, true);
            else
                definitionWindow.ShowPreview(
                    term,
                    String.IsNullOrWhiteSpace(context)
                        ? "正在从本地术语索引查找，并在需要时后台补充 AI 解释。"
                        : "已保留当前句子作为语境，正在先查本地术语索引。",
                    anchor,
                    lookupHistory.Count > 0);
            definitionWindow.Update();
            services.Log("Lookup started (" + term.Length + " chars)");
            string normalizedContext = (context ?? String.Empty).Trim();
            if (normalizedContext.Length > 500)
                normalizedContext = normalizedContext.Substring(0, 500);
            LookupResponse cached;
            if (ShortcutLookup.TryExplain(term, normalizedContext, out cached))
            {
                ApplyLookupResponse(source, cached.term, normalizedContext, anchor,
                    generation, cached, false, lookupWatch, false);
                return;
            }
            if (!refresh)
            {
                try
                {
                    services.EnsureRunning();
                    LookupResponse instant = await Task.Factory.StartNew(delegate
                    {
                        return services.LookupInstant(term, normalizedContext);
                    });
                    if (generation != lookupGeneration || selectedHighlight != source || !active)
                        return;
                    if (instant != null && !String.IsNullOrWhiteSpace(instant.explanation))
                    {
                        if (!instant.needs_model)
                        {
                            ApplyLookupResponse(source, term, normalizedContext, anchor,
                                generation, instant, false, lookupWatch, instant.cached);
                            return;
                        }
                        definitionWindow.ShowPreview(
                            instant.term ?? term,
                            instant.explanation,
                            anchor,
                            lookupHistory.Count > 0);
                    }
                }
                catch (Exception error)
                {
                    services.Log("Instant lookup failed: " + error.GetType().Name);
                }
            }

            LookupResponse response;
            try
            {
                response = await Task.Factory.StartNew(delegate
                {
                    try
                    {
                        services.EnsureRunning();
                        return services.Lookup(term, normalizedContext, refresh, previousExplanation);
                    }
                    catch (Exception error)
                    {
                        return new LookupResponse { term = term, error = error.Message };
                    }
                });
            }
            catch (Exception error)
            {
                response = new LookupResponse { term = term, error = error.Message };
            }
            if (generation != lookupGeneration || selectedHighlight != source || !active)
                return;
            ApplyLookupResponse(
                source, term, normalizedContext, anchor, generation, response,
                refresh, lookupWatch, false);
        }

        private void ApplyLookupResponse(
            HighlightForm source,
            string term,
            string context,
            Rectangle anchor,
            int generation,
            LookupResponse response,
            bool refresh,
            Stopwatch lookupWatch,
            bool cacheHit)
        {
            if (generation != lookupGeneration || selectedHighlight != source || !active)
                return;
            if (refresh && currentLookup != null &&
                (response == null || !string.IsNullOrEmpty(response.error) ||
                 string.IsNullOrWhiteSpace(response.explanation)))
            {
                definitionWindow.ShowDefinition(
                    currentLookup.term,
                    currentLookup.explanation,
                    currentLookup.entities,
                    currentLookup.sources,
                    currentLookup.anchor,
                    lookupHistory.Count > 0,
                    currentLookup.can_refresh);
                services.Log("Lookup refresh failed");
                services.RecordLookupMetric("auto_highlight", ClassifyChatApp(targetWindow),
                    "highlight", "refresh_failed",
                    lookupWatch.ElapsedMilliseconds, false, cacheHit, false);
                return;
            }
            if (response == null)
                response = new LookupResponse { term = term, error = "empty response" };
            string explanation = response.explanation;
            if (string.IsNullOrWhiteSpace(explanation))
                explanation = "暂时无法获取可靠解释。";
            currentLookup = new LookupView
            {
                term = term,
                context = context,
                explanation = explanation,
                entities = response.entities,
                sources = response.sources,
                can_refresh = response.can_refresh,
                anchor = anchor
            };
            definitionWindow.ShowDefinition(
                term,
                explanation,
                response.entities,
                response.sources,
                anchor,
                lookupHistory.Count > 0,
                response.can_refresh);
            services.Log(
                "Lookup finished" +
                (string.IsNullOrEmpty(response.error) ? ": ok" : ": failed"));
            services.RecordLookupMetric("auto_highlight", ClassifyChatApp(targetWindow),
                "highlight", response.lookup_mode,
                lookupWatch.ElapsedMilliseconds, false, cacheHit,
                String.IsNullOrWhiteSpace(response.error) && !String.IsNullOrWhiteSpace(response.explanation));
        }

        private void OnDefinitionFeedback(string feedback)
        {
            services.RecordFeedbackMetric("auto_highlight", ClassifyChatApp(targetWindow), feedback);
            if (String.Equals(feedback, "useful", StringComparison.Ordinal))
            {
                if (currentLookup != null) services.NoteTermClicked(currentLookup.term);
                return;
            }
            if (String.Equals(feedback, "unnecessary_highlight", StringComparison.Ordinal) &&
                currentLookup != null)
            {
                string term = currentLookup.term;
                services.IgnoreTerm(term);
                relativeHighlights.RemoveAll(delegate(HighlightItem item)
                {
                    return item != null && String.Equals(item.term, term,
                        StringComparison.OrdinalIgnoreCase);
                });
                HideDefinition();
                RenderHighlights();
                ShowNotice("已记录：以后不再自动标注这个词。", ToolTipIcon.Info);
                return;
            }
            if (String.Equals(feedback, "wrong_explanation", StringComparison.Ordinal))
                ShowNotice("已记录；可点击“换个解释”重试。", ToolTipIcon.Info);
        }

        private void NavigateBack()
        {
            if (!active || selectedHighlight == null || lookupHistory.Count == 0)
                return;
            LookupView previous = lookupHistory[lookupHistory.Count - 1];
            lookupHistory.RemoveAt(lookupHistory.Count - 1);
            currentLookup = previous;
            lookupGeneration++;
            definitionWindow.ShowDefinition(
                previous.term,
                previous.explanation,
                previous.entities,
                previous.sources,
                previous.anchor,
                lookupHistory.Count > 0,
                previous.can_refresh);
            services.Log("Lookup history back");
        }

        private void HideDefinition()
        {
            if (selectedHighlight == null && !definitionWindow.Visible)
                return;
            lookupGeneration++;
            selectedHighlight = null;
            currentLookup = null;
            lookupHistory.Clear();
            definitionWindow.Hide();
        }

    }
}
