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
    internal sealed class FamiliarityRecord
    {
        public int misses { get; set; }
        public long updated_utc_ticks { get; set; }
    }

    internal sealed class TermFamiliarityStore
    {
        internal const int SuppressionThreshold = 4;
        internal static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromSeconds(3);
        private const int MaximumRecords = 512;

        private readonly string path;
        private bool dirty;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly Dictionary<string, FamiliarityRecord> records =
            new Dictionary<string, FamiliarityRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> clicked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> currentlyVisible =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TimeSpan> visibleDurations =
            new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        private DateTime lastVisibilityUtc;
        private bool sessionActive;

        internal TermFamiliarityStore(string storagePath)
        {
            path = storagePath;
            Load();
        }

        internal int SuppressedCount
        {
            get
            {
                int count = 0;
                foreach (FamiliarityRecord record in records.Values)
                    if (record != null && record.misses >= SuppressionThreshold) count++;
                return count;
            }
        }

        internal static string Normalize(string term)
        {
            string value = (term ?? String.Empty).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            StringBuilder normalized = new StringBuilder(value.Length);
            foreach (char character in value)
                if (!Char.IsWhiteSpace(character)) normalized.Append(character);
            return normalized.ToString();
        }

        internal bool ShouldSuppress(string term)
        {
            FamiliarityRecord record;
            string key = Normalize(term);
            return key.Length > 0 && records.TryGetValue(key, out record) &&
                record != null && record.misses >= SuppressionThreshold;
        }

        internal void BeginSession()
        {
            BeginSession(DateTime.UtcNow);
        }

        internal void BeginSession(DateTime now)
        {
            clicked.Clear();
            currentlyVisible.Clear();
            visibleDurations.Clear();
            lastVisibilityUtc = now;
            sessionActive = true;
        }

        internal void UpdateVisibleTerms(IEnumerable<string> terms)
        {
            UpdateVisibleTerms(terms, DateTime.UtcNow);
        }

        internal void UpdateVisibleTerms(IEnumerable<string> terms, DateTime now)
        {
            if (!sessionActive) return;
            AccumulateVisibleTime(now);
            currentlyVisible.Clear();
            if (terms == null) return;
            foreach (string term in terms)
            {
                string key = Normalize(term);
                if (key.Length > 0) currentlyVisible.Add(key);
            }
        }

        internal void NoteClicked(string term)
        {
            NoteClicked(term, DateTime.UtcNow);
        }

        internal void NoteClicked(string term, DateTime now)
        {
            string key = Normalize(term);
            if (key.Length == 0) return;
            if (sessionActive) AccumulateVisibleTime(now);
            clicked.Add(key);
            if (records.Remove(key)) dirty = true;
        }

        internal void EndSession(bool applyLearning)
        {
            EndSession(applyLearning, DateTime.UtcNow);
        }

        internal void EndSession(bool applyLearning, DateTime now)
        {
            if (!sessionActive) { if (dirty) Save(); return; }
            AccumulateVisibleTime(now);
            bool changed = false;
            if (applyLearning)
            {
                foreach (KeyValuePair<string, TimeSpan> exposure in visibleDurations)
                {
                    if (exposure.Value < MinimumVisibleDuration || clicked.Contains(exposure.Key))
                        continue;
                    FamiliarityRecord record;
                    if (!records.TryGetValue(exposure.Key, out record) || record == null)
                    {
                        record = new FamiliarityRecord();
                        records[exposure.Key] = record;
                    }
                    if (record.misses < SuppressionThreshold) record.misses++;
                    record.updated_utc_ticks = now.Ticks;
                    changed = true;
                }
                if (TrimRecords()) changed = true;
                if (changed || dirty) Save();
            }
            if (dirty) Save();
            clicked.Clear();
            currentlyVisible.Clear();
            visibleDurations.Clear();
            sessionActive = false;
        }

        internal void CancelSession()
        {
            clicked.Clear();
            currentlyVisible.Clear();
            visibleDurations.Clear();
            sessionActive = false;
        }

        internal void Clear()
        {
            records.Clear();
            Save();
        }

        private void AccumulateVisibleTime(DateTime now)
        {
            if (now < lastVisibilityUtc) now = lastVisibilityUtc;
            TimeSpan elapsed = now - lastVisibilityUtc;
            if (elapsed > TimeSpan.Zero)
            {
                foreach (string key in currentlyVisible)
                {
                    TimeSpan duration;
                    visibleDurations.TryGetValue(key, out duration);
                    visibleDurations[key] = duration + elapsed;
                }
            }
            lastVisibilityUtc = now;
        }

        private bool TrimRecords()
        {
            bool changed = false;
            while (records.Count > MaximumRecords)
            {
                string oldestKey = null;
                long oldestTicks = Int64.MaxValue;
                foreach (KeyValuePair<string, FamiliarityRecord> item in records)
                {
                    long ticks = item.Value == null ? 0 : item.Value.updated_utc_ticks;
                    if (ticks < oldestTicks) { oldestTicks = ticks; oldestKey = item.Key; }
                }
                if (oldestKey == null) break;
                records.Remove(oldestKey);
                changed = true;
            }
            return changed;
        }

        private void Load()
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                Dictionary<string, FamiliarityRecord> saved =
                    serializer.Deserialize<Dictionary<string, FamiliarityRecord>>(
                        File.ReadAllText(path, Encoding.UTF8));
                if (saved == null) return;
                foreach (KeyValuePair<string, FamiliarityRecord> item in saved)
                {
                    string key = Normalize(item.Key);
                    if (key.Length > 0 && item.Value != null && item.Value.misses > 0)
                        records[key] = item.Value;
                }
                TrimRecords();
            }
            catch { records.Clear(); }
        }

        private void Save()
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, serializer.Serialize(records), new UTF8Encoding(false));
                File.Copy(temporary, path, true);
                File.Delete(temporary);
                dirty = false;
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }
    }

}
