using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SemanticOverlay.NativeHost
{
    internal sealed class PreferenceStore
    {
        private readonly object gate = new object();
        private readonly string path;
        private Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal PreferenceStore(string file, Action<Exception> loadFailure)
        {
            path = file;
            try
            {
                if (!File.Exists(path)) return;
                var loaded = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (loaded != null)
                    values = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception error) { if (loadFailure != null) loadFailure(error); }
        }

        internal bool TryGetValue(string key, out string value)
        {
            lock (gate) return values.TryGetValue(key, out value);
        }

        internal void SetAndSave(string key, string value)
        {
            lock (gate)
            {
                var next = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                next[key] = value;
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(next), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                // Publish in-memory state only after the durable replacement succeeds.
                values = next;
            }
        }
    }
}
