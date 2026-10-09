using System;
using System.IO;
using System.Text;

namespace SemanticOverlay.NativeHost
{
    internal static class BoundedJournal
    {
        private static readonly object gate = new object();
        internal static void Append(string path, string line, long maximumBytes)
        {
            lock (gate) {
                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string encoded = line + Environment.NewLine;
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(encoded) > maximumBytes) {
                    string previous = path + ".1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                File.AppendAllText(path, encoded, new UTF8Encoding(false));
            }
        }
    }
}
