import json
import os
import sys
import unittest
from unittest import mock

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

import server


class TermStabilityTests(unittest.TestCase):
    def setUp(self):
        server.ANALYZE_CACHE.clear()
        server.ANALYZE_INFLIGHT.clear()
        server.MODEL_ANALYSIS_CALLS.clear()
        server.TERM_SELECTIONS.clear()

    def tearDown(self):
        server.ANALYZE_CACHE.clear()
        server.ANALYZE_INFLIGHT.clear()
        server.MODEL_ANALYSIS_CALLS.clear()
        server.TERM_SELECTIONS.clear()

    def test_common_units_and_ui_words_are_always_removed(self):
        text = "每隔80毫秒刷新窗口，画面有60帧"
        payload = {"entities": [
            {"text": "80毫秒", "start": 2, "end": 6},
            {"text": "窗口", "start": 8, "end": 10},
            {"text": "画面", "start": 11, "end": 13},
            {"text": "60帧", "start": 14, "end": 17},
        ], "actions": []}
        parsed = server.extract_json(json.dumps(payload, ensure_ascii=False), text)
        self.assertEqual([], server.stabilize_model_entities(
            text, parsed["entities"], "standard", 15))

    def test_professional_compound_is_not_removed(self):
        text = "这需要优化毫秒级延迟"
        item = {"text": "毫秒级延迟", "start": 5, "end": 10}
        result = server.stabilize_model_entities(text, [item], "standard", 15)
        self.assertEqual(["毫秒级延迟"], [entity["text"] for entity in result])

    def test_accepted_term_reappears_when_next_model_omits_it(self):
        first = "RAG 和向量数据库用于检索"
        first_start = first.index("向量数据库")
        server.stabilize_model_entities(first, [
            {"text": "向量数据库", "start": first_start,
             "end": first_start + len("向量数据库")}], "standard", 15)
        second = "随后仍使用向量数据库回答"
        result = server.stabilize_model_entities(second, [], "standard", 15)
        self.assertEqual(["向量数据库"], [entity["text"] for entity in result])
        self.assertEqual("向量数据库", second[result[0]["start"]:result[0]["end"]])

    def test_difficulty_memories_do_not_cross(self):
        text = "毫秒级延迟需要优化"
        server.stabilize_model_entities(text, [
            {"text": "毫秒级延迟", "start": 0, "end": 5}], "detailed", 22)
        self.assertEqual([], server.stabilize_model_entities(
            text, [], "concise", 8))

    def test_conversation_memories_do_not_cross(self):
        text = "继续使用 RAG 完成检索"
        start = text.index("RAG")
        server.stabilize_model_entities(text, [
            {"text": "RAG", "start": start, "end": start + 3}],
            "standard", 15, "chat-a")
        self.assertEqual([], server.stabilize_model_entities(
            "另一个群聊提到 RAG", [], "standard", 15, "chat-b"))

    def test_remembered_terms_win_at_density_limit(self):
        prior = "RAG 与 FAISS"
        rag = prior.index("RAG")
        server.stabilize_model_entities(prior, [
            {"text": "RAG", "start": rag, "end": rag + 3}],
            "concise", 8, "chat-a")
        current = "RAG FAISS JWT K8s Docker Python JavaScript TypeScript OpenAI"
        fresh = []
        for term in current.split()[1:]:
            start = current.index(term)
            fresh.append({"text": term, "start": start, "end": start + len(term)})
        result = server.stabilize_model_entities(
            current, fresh, "concise", 8, "chat-a")
        self.assertIn("RAG", [item["text"] for item in result])
        self.assertEqual(8, len(result))

    def test_ascii_memory_does_not_match_inside_longer_word(self):
        text = "API 用于服务调用"
        server.stabilize_model_entities(text, [
            {"text": "API", "start": 0, "end": 3}],
            "standard", 15, "chat-a")
        self.assertEqual([], server.stabilize_model_entities(
            "CAPITAL 是普通英文", [], "standard", 15, "chat-a"))

    def test_local_path_uses_the_same_mundane_filter(self):
        with mock.patch.object(server, "extract_candidates", return_value=[
                (0, 2, "毫秒", 100), (3, 6, "RAG", 100)]):
            result = server.analyze(
                "毫秒 RAG", mode="local", context_id="chat-a")
        self.assertEqual(["RAG"], [item["text"] for item in result["entities"]])

    def test_local_preview_does_not_override_later_model_selection(self):
        with mock.patch.object(server, "extract_candidates", return_value=[
                (0, 3, "RAG", 100)]):
            preview = server.analyze(
                "RAG 用于检索", mode="local", context_id="chat-a")
        self.assertEqual(["RAG"], [item["text"] for item in preview["entities"]])
        self.assertEqual(0, len(server.TERM_SELECTIONS))
        result = server.stabilize_model_entities(
            "RAG 用于检索", [], "standard", 15, "chat-a")
        self.assertEqual([], result)

    def test_analyze_applies_filter_after_model(self):
        text = "每隔毫秒刷新 RAG"
        content = json.dumps({"entities": [
            {"text": "毫秒", "start": 2, "end": 4},
            {"text": "RAG", "start": 7, "end": 10}], "actions": []}, ensure_ascii=False)
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline", return_value=content):
            result = server.analyze(text)
        self.assertEqual(["RAG"], [item["text"] for item in result["entities"]])


if __name__ == "__main__":
    unittest.main()
