using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SemanticOverlay.NativeHost
{
    internal sealed class CaptionArchiveCursor
    {
        public string File;
        public long Offset;
        public bool DiscardLine;
    }

    internal static class CaptionArchivePages
    {
        // Cursor offsets refer to bytes, independent of StreamReader buffering.
        public static CaptionArchiveReadResult Read(string directory, CaptionArchiveCursor cursor)
        {
            var result = new CaptionArchiveReadResult();
            if (!Directory.Exists(directory)) return result;
            string[] files;
            try { files = Directory.GetFiles(directory, "*.jsonl"); }
            catch { result.SkippedFiles++; return result; }
            Array.Sort(files, StringComparer.Ordinal);
            var serializer = new JavaScriptSerializer();
            long work = 0;
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (cursor != null && StringComparer.Ordinal.Compare(name, cursor.File) < 0) continue;
                long offset = cursor != null && name == cursor.File ? cursor.Offset : 0;
                bool discard = cursor != null && name == cursor.File && cursor.DiscardLine;
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var input = new BufferedStream(stream, 4096))
                    {
                        if (offset > stream.Length) { result.SkippedFiles++; continue; }
                        input.Seek(offset, SeekOrigin.Begin);
                        while (true)
                        {
                            long lineStart = offset;
                            var bytes = new List<byte>();
                            bool ended = false, eof = false;
                            while (work < 8 * 1024 * 1024 || (!discard && bytes.Count < 40000))
                            {
                                int value = input.ReadByte();
                                if (value < 0) { eof = true; break; }
                                offset++; work++;
                                if (value == 10) { ended = true; break; }
                                if (!discard && bytes.Count < 40000) bytes.Add((byte)value);
                                else discard = true;
                            }
                            if (!ended && !eof)
                            {
                                // A bounded page may stop in an oversized line. Skip its
                                // remainder on the next page; never parse a partial record.
                                result.SkippedLines++;
                                result.Next = new CaptionArchiveCursor { File = name, Offset = offset, DiscardLine = true };
                                return result;
                            }
                            if (discard) { result.SkippedLines++; discard = false; }
                            else if (bytes.Count > 0)
                            {
                                try
                                {
                                    string line = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                                    if (lineStart == 0) line = line.TrimStart('\uFEFF');
                                    if (line.Length > 10000) throw new InvalidDataException();
                                    var record = serializer.Deserialize<Dictionary<string, object>>(line);
                                    DateTime stamp, started;
                                    object value;
                                    string kind = record != null && record.TryGetValue("kind", out value) ? value as string : null;
                                    string text = record != null && record.TryGetValue("text", out value) ? value as string : null;
                                    string timestamp = record != null && record.TryGetValue("timestamp", out value) ? value as string : null;
                                    if ((kind != "line" && kind != "gap" && kind != "replace_last") ||
                                        String.IsNullOrWhiteSpace(text) || text.Length > 2000 || !DateTime.TryParse(timestamp, out stamp))
                                        throw new InvalidDataException();
                                    string start = record.TryGetValue("session_started", out value) ? value as string : null;
                                    if (!DateTime.TryParse(start, out started)) started = stamp;
                                    string source = record.TryGetValue("source", out value) ? value as string : null;
                                    string raw = record.TryGetValue("raw_text", out value) ? value as string : null;
                                    var entry = new CaptionEntry { timestamp = stamp, text = text, is_gap = kind == "gap",
                                        raw_text = kind == "gap" || (raw != null && raw.Length > 2000) ? null : raw,
                                        session_key = Path.GetFileNameWithoutExtension(file),
                                        session_label = started.ToString("HH:mm") + " · " + (String.IsNullOrWhiteSpace(source) ? "会议字幕" : source) };
                                    int last = result.Entries.Count - 1;
                                    bool replace = kind == "replace_last" && last >= 0 &&
                                        result.Entries[last].session_key == entry.session_key && !result.Entries[last].is_gap;
                                    if (!replace && result.Entries.Count == 2000)
                                    {
                                        result.Next = new CaptionArchiveCursor { File = name, Offset = lineStart };
                                        return result;
                                    }
                                    if (replace) result.Entries[last] = entry;
                                    else result.Entries.Add(entry);
                                }
                                catch { result.SkippedLines++; }
                            }
                            if (eof) break;
                            if (work >= 8 * 1024 * 1024)
                            {
                                result.Next = new CaptionArchiveCursor { File = name, Offset = offset };
                                return result;
                            }
                        }
                    }
                }
                catch { result.SkippedFiles++; }
            }
            return result;
        }
    }
}
