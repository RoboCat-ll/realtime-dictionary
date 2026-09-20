import unittest
from unittest.mock import patch
import urllib.error
import server


class ShortcutLookupTests(unittest.TestCase):
    def test_model_suffix_inside_gpt_is_rejected(self):
        text = "模型已从 GPT-6 Astra 更改为 GPT-5.6 Solo"
        start = text.index("PT")
        result = server._valid_filtered_entities(text, [
            {"text": "PT", "type": "concept", "start": start, "end": start + 2}])
        self.assertEqual([], result)

    def test_unknown_two_letter_ocr_fragments_are_rejected(self):
        text = "后面还有 PT 和 DO 这两个错误高亮"
        entities = []
        for term in ("PT", "DO"):
            start = text.index(term)
            entities.append({"text": term, "type": "concept",
                             "start": start, "end": start + len(term)})
        self.assertEqual([], server._valid_filtered_entities(text, entities))

    def test_known_two_letter_technical_terms_are_retained(self):
        text = "AI 与 UI 都是这里的有效术语"
        entities = []
        for term in ("AI", "UI"):
            start = text.index(term)
            entities.append({"text": term, "type": "concept",
                             "start": start, "end": start + len(term)})
        self.assertEqual(["AI", "UI"], [item["text"] for item in
                         server._valid_filtered_entities(text, entities)])

    def test_shortcut_component_expands_before_two_letter_filter(self):
        text = "错误示例 Ctrl-Alt-DO"
        start = text.index("DO")
        result = server._valid_filtered_entities(text, [
            {"text": "DO", "type": "concept", "start": start, "end": start + 2}])
        self.assertEqual("Ctrl-Alt-DO", result[0]["text"])

    def test_fragment_expands_only_to_existing_chord(self):
        text = "😀 请按 Ctrl+Alt+G 清除高亮"
        start = text.index("rl+Alt+G")
        result = server._valid_filtered_entities(text, [
            {"text": "rl+Alt+G", "start": start, "end": start + 8}])
        self.assertEqual(result[0]["text"], "Ctrl+Alt+G")
        self.assertEqual(text[result[0]["start"]:result[0]["end"]], "Ctrl+Alt+G")
        self.assertIn("清除高亮", server.shortcut_explanation("Ctrl+Alt+G", "实时字典"))

    def test_ocr_shortcut_without_provider(self):
        with patch.object(server, "call_llm", side_effect=AssertionError("billable call")):
            result = server.lookup("Ctr1+A1t+K", "实时字典高亮")
        self.assertIn("刷新当前窗口", result["explanation"])
        self.assertEqual(result["lookup_mode"], "local_shortcut")

    def test_hyphen_and_trailing_o_are_canonicalized_locally(self):
        for source in ("Ctrl-Alt-D", "Ctrl－Alt－DO", "Ctr1–A1t–D0"):
            with self.subTest(source=source):
                with patch.object(server, "call_llm", side_effect=AssertionError("billable call")):
                    result = server.lookup(source, "实时字典高亮")
                self.assertEqual(result["term"], "Ctrl+Alt+D")
                self.assertIn("查询选中文字", result["explanation"])
                self.assertEqual(result["lookup_mode"], "local_shortcut")

    def test_do_not_invent_other_apps_shortcut(self):
        self.assertIn("刷新当前窗口", server.shortcut_explanation("Ctrl+Alt+K", "编辑器"))
        self.assertIn("取决于当前软件", server.shortcut_explanation("Ctrl+Shift+P", "编辑器"))
        self.assertIsNone(server.shortcut_explanation("C++"))
        self.assertIsNone(server.shortcut_explanation("AirPodsPro6"))

    def test_safe_failure_categories(self):
        error = RuntimeError("private provider body")
        error.__cause__ = urllib.error.HTTPError("https://invalid", 401, "secret", {}, None)
        self.assertEqual(server.lookup_failure_notice(error), "模型认证失败")
        self.assertEqual(server.lookup_failure_notice(TimeoutError()), "模型请求超时")
        self.assertNotIn("产品名", server.infer_local_explanation("unknown"))
