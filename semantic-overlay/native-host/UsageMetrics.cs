using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SemanticOverlay.NativeHost
{
    // Product evidence only. The API intentionally accepts bounded categorical
    // fields and numbers, never chat text, terms, explanations, screenshots or keys.
    internal sealed class UsageMetricsStore
    {
        private const long MaximumBytes = 5 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly string path;
        private readonly string sessionId = Guid.NewGuid().ToString("N");

        internal string Path { get { return path; } }

        internal UsageMetricsStore(string overridePath = null)
        {
            path = overridePath ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary",
                "usage-events.jsonl");
        }

        internal void RecordLookup(
            string triggerMode,
            string sourceApp,
            string textSource,
            string lookupMode,
            int elapsedMs,
            bool manualCorrection,
            bool cacheHit,
            bool success)
        {
            var fields = BaseEvent("lookup_completed");
            fields["trigger_mode"] = Category(triggerMode, "active_lookup", "auto_highlight");
            fields["source_app"] = Category(sourceApp, "wechat", "qq", "other");
            fields["text_source"] = Category(textSource,
                "accessibility", "clipboard", "ocr", "typed", "highlight");
            fields["lookup_mode"] = SafeMode(lookupMode);
            fields["elapsed_ms"] = Math.Max(0, Math.Min(elapsedMs, 600000));
            fields["manual_correction"] = manualCorrection;
            fields["cache_hit"] = cacheHit;
            fields["success"] = success;
            Append(fields);
        }

        internal void RecordSelection(
            string sourceApp,
            string textSource,
            string analysisMode,
            int elapsedMs,
            bool manualCorrection,
            bool success,
            int termCount)
        {
            var fields = BaseEvent("selection_analysis_completed");
            fields["trigger_mode"] = "selection_passage";
            fields["source_app"] = Category(sourceApp, "wechat", "qq", "other");
            fields["text_source"] = Category(textSource,
                "message_accessibility", "bubble_ocr", "accessibility", "ocr");
            fields["lookup_mode"] = SafeMode(analysisMode);
            fields["elapsed_ms"] = Math.Max(0, Math.Min(elapsedMs, 600000));
            fields["manual_correction"] = manualCorrection;
            fields["success"] = success;
            fields["count"] = Math.Max(0, Math.Min(termCount, 5));
            Append(fields);
        }

        internal void RecordFeedback(string triggerMode, string sourceApp, string feedback)
        {
            var fields = BaseEvent("feedback_submitted");
            fields["trigger_mode"] = Category(triggerMode, "active_lookup", "auto_highlight");
            fields["source_app"] = Category(sourceApp, "wechat", "qq", "other");
            fields["feedback"] = Category(feedback,
                "useful", "unnecessary_highlight", "wrong_explanation");
            Append(fields);
        }

        internal void RecordHighlights(string sourceApp, int count, string analysisMode)
        {
            var fields = BaseEvent("highlights_rendered");
            fields["trigger_mode"] = "auto_highlight";
            fields["source_app"] = Category(sourceApp, "wechat", "qq", "other");
            fields["count"] = Math.Max(0, Math.Min(count, 5));
            fields["lookup_mode"] = SafeMode(analysisMode);
            Append(fields);
        }

        private Dictionary<string, object> BaseEvent(string name)
        {
            return new Dictionary<string, object> {
                { "timestamp_utc", DateTime.UtcNow.ToString("o") },
                { "session_id", sessionId },
                { "event_name", name },
            };
        }

        private static string Category(string value, params string[] allowed)
        {
            foreach (string item in allowed)
                if (String.Equals(value, item, StringComparison.OrdinalIgnoreCase))
                    return item;
            return "unknown";
        }

        private static string SafeMode(string value)
        {
            value = (value ?? String.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value.Length > 40) return "unknown";
            foreach (char character in value)
                if (!(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9') &&
                    character != '_' && character != '-') return "unknown";
            return value;
        }

        private void Append(Dictionary<string, object> fields)
        {
            try
            {
                lock (gate)
                {
                    FileInfo existing = new FileInfo(path);
                    if (existing.Exists && existing.Length >= MaximumBytes) return;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    File.AppendAllText(path, serializer.Serialize(fields) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
