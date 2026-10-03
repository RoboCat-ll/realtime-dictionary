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
    internal static class ShortcutLookup
    {
        internal static bool TryNormalize(string term, out string canonical)
        {
            canonical = null;
            string compact = System.Text.RegularExpressions.Regex.Replace(
                term ?? String.Empty, @"\s+", String.Empty)
                .Replace('＋', '+').Replace('－', '-').Replace('–', '-').Replace('—', '-');
            string[] parts = System.Text.RegularExpressions.Regex.Split(compact, @"[+\-]");
            if (parts.Length < 2) return false;
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "ctrl", "Ctrl" }, { "ctr1", "Ctrl" }, { "control", "Ctrl" },
                { "alt", "Alt" }, { "a1t", "Alt" }, { "shift", "Shift" }, { "win", "Win" }
            };
            List<string> keys = new List<string>();
            for (int index = 0; index < parts.Length - 1; index++)
            {
                string modifier;
                if (!aliases.TryGetValue(parts[index], out modifier)) return false;
                keys.Add(modifier);
            }
            string key = parts[parts.Length - 1];
            string functionKey = key.ToUpperInvariant().Replace('O', '0');
            int functionNumber;
            if (functionKey.StartsWith("F") && Int32.TryParse(functionKey.Substring(1), out functionNumber) &&
                functionNumber >= 1 && functionNumber <= 12)
                key = "F" + functionNumber;
            else
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z0-9][Oo0]$"))
                    key = key.Substring(0, 1);
                if (!System.Text.RegularExpressions.Regex.IsMatch(key,
                    @"^(?:[A-Za-z0-9]|Enter|Tab|Esc|Delete|Space)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
                key = key.Length == 1 ? key.ToUpperInvariant() :
                    Char.ToUpperInvariant(key[0]) + key.Substring(1).ToLowerInvariant();
            }
            keys.Add(key);
            canonical = String.Join("+", keys.ToArray());
            return true;
        }

        internal static bool TryExplain(string term, string context, out LookupResponse result)
        {
            result = null;
            string canonical;
            if (!TryNormalize(term, out canonical)) return false;
            string normalized = canonical.ToLowerInvariant();
            string body = canonical + " 是键盘组合快捷键：按住前面的修饰键，再按最后一个键。具体功能取决于当前软件的设置。";
            if (normalized == "ctrl+alt+g") body = canonical + " 在实时字典中用于清除高亮并结束当前会话，程序仍在托盘运行。";
            if (normalized == "ctrl+alt+k") body = canonical + " 在实时字典的对话模式中用于进入一次 10 秒待选状态；随后单击一条聊天消息即可解释，点击后自动退出待选状态。";
            if (normalized == "ctrl+alt+d") body = canonical + " 在实时字典中用于查询选中文字；读取不到时可以输入或粘贴。";
            result = new LookupResponse { term = canonical, explanation = body + "\n\n来源：本地快捷键规则。",
                lookup_mode = "local_shortcut", can_refresh = false };
            return true;
        }
    }

    internal static class UnicodeSpans
    {
        internal static void Convert(string text, List<AnalysisEntity> entities)
        {
            if (entities == null) return;
            text = text ?? String.Empty;
            List<int> offsets = new List<int>();
            for (int index = 0; index < text.Length; index++)
            {
                offsets.Add(index);
                if (Char.IsHighSurrogate(text[index]) && index + 1 < text.Length &&
                    Char.IsLowSurrogate(text[index + 1])) index++;
            }
            offsets.Add(text.Length);
            entities.RemoveAll(delegate(AnalysisEntity entity)
            {
                if (entity == null || entity.start < 0 || entity.end <= entity.start ||
                    entity.end >= offsets.Count) return true;
                int start = offsets[entity.start], end = offsets[entity.end];
                if (text.Substring(start, end - start) != entity.text) return true;
                entity.start = start; entity.end = end;
                return false;
            });
        }
    }

}
