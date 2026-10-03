using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace SemanticOverlay.NativeHost
{
    internal static class CredentialProtectionTest
    {
        static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            string key = "fixture-not-a-real-credential";
            var config = new Dictionary<string, object> {
                { "base_url", "https://api.siliconflow.cn/v1" },
                { "model", "Qwen/Qwen2.5-7B-Instruct" },
                { "api_key", key },
            };
            CredentialProtection.Save(args[0], config);
            CredentialProtection.Save(args[0], new Dictionary<string, object> {
                { "text_base_url", "https://api.deepseek.com/" },
                { "text_model", "fixture-model" },
                { "text_api_key", key }
            });
            string json = File.ReadAllText(args[0], Encoding.UTF8);
            if (json.Contains(key)) return 3;
            string archiveRoot = Path.Combine(Path.GetDirectoryName(args[0]), "owned-archive-fixture");
            var archive = new CaptionHistoryArchive(archiveRoot);
            DateTime day = new DateTime(2026, 9, 28);
            archive.BeginSession(day, "fixture");
            if (!archive.Append(new CaptionEntry { timestamp = day, text = "first fixture" })) return 4;
            archive.BeginSession(day.AddDays(-1), "fixture");
            if (!archive.Append(new CaptionEntry { timestamp = day.AddDays(-1), text = "second fixture" })) return 5;
            if (archive.DeleteDate(day) != 1 || archive.LoadDate(day).Count != 0 ||
                archive.LoadDate(day.AddDays(-1)).Count != 1) return 6;
            return 0;
        }
    }
}
