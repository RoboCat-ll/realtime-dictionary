import os
import json
import re
import sys
import threading
import unittest
from urllib.request import Request, urlopen
from unittest import mock


PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)

import server


class ConfigurationTests(unittest.TestCase):
    def test_environment_endpoint_and_model_override_config_files(self):
        with mock.patch.dict(os.environ, {
            "OPENAI_BASE_URL": "https://api.siliconflow.cn/v1/",
            "OPENAI_MODEL": "deepseek-ai/DeepSeek-V4-Flash",
        }, clear=False):
            config = server.load_config()
        self.assertEqual("https://api.siliconflow.cn/v1", config["base_url"])
        self.assertEqual("deepseek-ai/DeepSeek-V4-Flash", config["model"])


class LocalAnalysisTests(unittest.TestCase):
    SAMPLE = (
        "GitHub GitLab OpenAI DeepSeek ChatGPT PaddleOCR Jupyter Notebook "
        "AirPods iPhone iPad MacBook Agent 人工智能 机器学习 深度学习 "
        "大语言模型 向量数据库 知识图谱 神经网络 图灵测试 哈希碰撞 "
        "微服务 云计算 区块链 API OCR RAG FAISS JWT K8s Docker Python "
        "JavaScript TypeScript oneAPI"
    )

    def test_difficulty_limits_are_exact_for_dense_sample(self):
        expected = {"concise": 8, "standard": 15, "detailed": 22}
        for difficulty, count in expected.items():
            with self.subTest(difficulty=difficulty):
                result = server.local_analyze(self.SAMPLE, difficulty=difficulty)
                self.assertEqual(count, len(result["entities"]))

    def test_concise_prefers_high_information_acronyms(self):
        result = server.local_analyze(self.SAMPLE, difficulty="concise")
        terms = {item["text"] for item in result["entities"]}
        self.assertTrue({"RAG", "FAISS", "JWT"}.issubset(terms))

    def test_compound_names_are_not_reduced_to_inner_acronyms(self):
        text = "使用 oneAPI 和 PaddleOCR 完成任务"
        terms = [item["text"] for item in server.local_analyze(text)["entities"]]
        self.assertIn("oneAPI", terms)
        self.assertIn("PaddleOCR", terms)
        self.assertNotIn("API", terms)
        self.assertNotIn("OCR", terms)

    def test_generic_chinese_sentence_fragment_is_not_a_local_highlight(self):
        text = "这个属于真实使用效果验证，不能由离线测试冒充。"
        terms = [item["text"] for item in server.local_analyze(text)["entities"]]
        self.assertNotIn("不能由离线测试", terms)
        self.assertEqual([], terms)

    def test_known_english_terms_have_chinese_fallbacks(self):
        for term in ("bootcamp", "oneapi", "github", "rag"):
            with self.subTest(term=term):
                explanation = server.LOCAL_EXPLANATIONS[term]
                self.assertRegex(explanation, r"[\u4e00-\u9fff]")

    def test_mixed_case_technical_terms_are_complete_candidates(self):
        samples = {
            "LoRA 可以降低大模型微调所需的显存。": "LoRA",
            "微服务通过 Kubernetes 编排，并使用 gRPC 通信。": "gRPC",
            "OAuth 2.0 与 JWT 经常用于身份认证和授权。": "OAuth 2.0",
        }
        for text, expected in samples.items():
            with self.subTest(expected=expected):
                candidates = [item[2] for item in server.extract_candidates(text)]
                self.assertIn(expected, candidates)

    def test_chinese_sentence_fragment_is_not_sent_as_candidate(self):
        terms = [item[2] for item in server.extract_candidates(
            "FAISS 和 JWT 是不同领域的技术。")]
        self.assertNotIn("是不同领域的技术", terms)

    def test_jev_candidate_limit_prefers_high_value_terms_over_source_order(self):
        text = " ".join([f"ordinaryword{index}" for index in range(16)] + ["RAG"])
        candidates = [
            (match.start(), match.end(), match.group(0), 40)
            for match in re.finditer(r"ordinaryword\d+", text)
        ]
        rag_start = text.rfind("RAG")
        candidates.append((rag_start, rag_start + 3, "RAG", 125))
        captured = {}

        class Response:
            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self):
                state = captured["payload"]["state"]
                answers = {
                    item["id"]: {"type": "noul", "noul": 0.9 if item["text"] == "RAG" else 0.1}
                    for item in state["candidates"]
                }
                return json.dumps({"answers": answers}).encode("utf-8")

        def open_request(request, timeout):
            captured["payload"] = json.loads(request.data.decode("utf-8"))
            return Response()

        local_result = {"entities": [], "actions": [], "analysis_mode": "local"}
        with mock.patch.object(server.urllib.request, "urlopen", side_effect=open_request), \
                mock.patch.object(server, "TYPESAFE_API_KEY", "test-key"):
            result = server.analyze_with_jev(
                text, candidates, "standard", "candidate-priority", local_result)

        state = captured["payload"]["state"]
        sent_terms = [item["text"] for item in state["candidates"]]
        self.assertIsInstance(state, dict)
        self.assertEqual(server.JEV_CANDIDATE_LIMITS["standard"], len(sent_terms))
        self.assertNotIn("RAG", sent_terms)
        self.assertIn("RAG", [item["text"] for item in result["entities"]])
        self.assertEqual(1, result["analysis_local_accept_count"])

    def test_jev_shortlist_drops_obvious_mundane_terms(self):
        text = "RAG 使用模型，ACID 完成检索"
        candidates = server.extract_candidates(text)
        captured = {}

        class Response:
            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self):
                state = captured["payload"]["state"]
                answers = {item["id"]: {"type": "noul", "noul": 0.9}
                           for item in state["candidates"]}
                return json.dumps({"answers": answers}).encode("utf-8")

        def open_request(request, timeout):
            captured["payload"] = json.loads(request.data.decode("utf-8"))
            return Response()

        with mock.patch.object(server.urllib.request, "urlopen", side_effect=open_request), \
                mock.patch.object(server, "TYPESAFE_API_KEY", "test-key"):
            server.analyze_with_jev(
                text, candidates, "standard", "mundane-filter",
                {"entities": [], "actions": [], "analysis_mode": "local"})

        state = captured["payload"]["state"]
        self.assertNotIn("RAG", [item["text"] for item in state["candidates"]])
        self.assertIn("ACID", [item["text"] for item in state["candidates"]])
        self.assertNotIn("模型", [item["text"] for item in state["candidates"]])

    def test_conservative_fallback_keeps_known_terms_only(self):
        text = "oneAPI 安排 bootcamp，提到 ACID 和 ordinaryword"
        terms = {item["text"] for item in
                 server.conservative_fallback_entities(text)["entities"]}
        self.assertTrue({"oneAPI", "bootcamp"}.issubset(terms))
        self.assertNotIn("ACID", terms)
        self.assertNotIn("ordinaryword", terms)

    def test_instant_mode_is_strict_and_does_not_teach_selection_memory(self):
        text = "使用 OAuth 2.0 和 bootcamp，稍后评估 ACID 与 ordinaryword"
        before = dict(server.TERM_SELECTIONS)
        result = server.analyze(text, mode="instant", context_id="instant-test")
        terms = {item["text"] for item in result["entities"]}
        self.assertEqual("local_instant", result["analysis_mode"])
        self.assertTrue({"OAuth 2.0", "bootcamp"}.issubset(terms))
        self.assertNotIn("ACID", terms)
        self.assertNotIn("ordinaryword", terms)
        self.assertEqual(before, dict(server.TERM_SELECTIONS))

    def test_http_analyze_accepts_instant_mode_without_provider_call(self):
        http = server.ThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        worker = threading.Thread(target=http.serve_forever, daemon=True)
        worker.start()
        try:
            with mock.patch.object(server, "PORT", http.server_address[1]):
                payload = json.dumps({
                    "text": "使用 OAuth 2.0 和 bootcamp，稍后评估 ordinaryword",
                    "mode": "instant",
                    "difficulty": "standard",
                    "context_id": "instant-http-test",
                }).encode("utf-8")
                request = Request(
                    "http://127.0.0.1:%d/analyze" % http.server_address[1],
                    data=payload,
                    headers={
                        "Content-Type": "application/json",
                        "X-RealtimeDictionary-Token": server.TOKEN,
                    },
                )
                with mock.patch.object(server, "call_llm") as model_call:
                    with urlopen(request, timeout=3) as response:
                        result = json.loads(response.read().decode("utf-8"))
            self.assertEqual("local_instant", result["analysis_mode"])
            self.assertEqual({"OAuth 2.0", "bootcamp"},
                             {item["text"] for item in result["entities"]})
            model_call.assert_not_called()
        finally:
            http.shutdown()
            http.server_close()
            worker.join(timeout=3)


class OffsetCorrectionTests(unittest.TestCase):
    def test_repeated_term_uses_next_occurrence(self):
        text = "RAG and RAG"
        payload = {
            "entities": [
                {"text": "RAG", "type": "concept", "start": 0, "end": 3},
                {"text": "RAG", "type": "concept", "start": 0, "end": 3},
            ]
        }
        result = server.extract_json(json.dumps(payload), text)
        self.assertEqual([(0, 3), (8, 11)], [
            (item["start"], item["end"]) for item in result["entities"]
        ])

    def test_invalid_offsets_are_dropped(self):
        payload = {"entities": [{"text": "missing", "start": 9, "end": 16}]}
        result = server.extract_json(json.dumps(payload), "RAG")
        self.assertEqual([], result["entities"])


class TaskExtractionTests(unittest.TestCase):
    SAMPLE = "我们9月3号下午14:00和oneapi的同事约一个bootcamp讨论会啊"

    def test_local_analysis_returns_concepts_and_calendar_candidate(self):
        result = server.local_analyze(self.SAMPLE)
        terms = {item["text"].casefold() for item in result["entities"]}
        self.assertTrue({"oneapi", "bootcamp"}.issubset(terms))
        self.assertEqual(1, len(result["actions"]))
        action = result["actions"][0]
        self.assertEqual("9月3号下午14:00", action["text"])
        self.assertEqual(action["text"], self.SAMPLE[action["start"]:action["end"]])
        self.assertEqual("calendar_event", action["type"])
        self.assertTrue(action["needs_confirmation"])
        self.assertEqual("", action["start_iso"])
        self.assertEqual("9月3号下午14:00", action["time_text"])
        self.assertTrue(action["title"].startswith("与oneapi"))

    def test_bare_time_without_action_cue_is_not_a_task(self):
        result = server.local_analyze("报告中提到9月3号下午14:00的历史数据")
        self.assertEqual([], result["actions"])

    def test_relative_day_with_action_cue_is_a_task(self):
        text = "我们明天下午14:00和OneAPI同事开bootcamp讨论会。"
        result = server.local_analyze(text)
        self.assertEqual(1, len(result["actions"]))
        action = result["actions"][0]
        self.assertEqual("明天下午14:00", action["text"])
        self.assertEqual(action["text"], text[action["start"]:action["end"]])
        self.assertEqual("", action["start_iso"])

    def test_relative_day_without_action_cue_is_not_a_task(self):
        result = server.local_analyze("报告记录了明天下午14:00的预测值")
        self.assertEqual([], result["actions"])

    def test_model_action_offsets_are_corrected_and_fields_sanitized(self):
        payload = {
            "entities": [],
            "actions": [{
                "text": "9月3号下午14:00",
                "start": 0,
                "end": 1,
                "title": "  讨论会  ",
                "start_iso": " 2026-09-03T14:00 ",
                "end_iso": " 2026-09-03T15:00 ",
                "utc_offset": " +08:00 ",
                "confidence": 2,
                "needs_confirmation": False,
            }],
        }
        result = server.extract_json(json.dumps(payload, ensure_ascii=False), self.SAMPLE)
        action = result["actions"][0]
        self.assertEqual((2, 13), (action["start"], action["end"]))
        self.assertEqual("讨论会", action["title"])
        self.assertEqual("2026-09-03T14:00", action["start_iso"])
        self.assertEqual("2026-09-03T15:00", action["end_iso"])
        self.assertEqual("+08:00", action["utc_offset"])
        self.assertEqual(1.0, action["confidence"])
        self.assertTrue(action["needs_confirmation"])

    def test_model_action_invalid_prefill_fields_are_discarded(self):
        payload = {"entities": [], "actions": [{
            "text": "9月3号下午14:00", "start": 2, "end": 13,
            "title": "讨论会", "start_iso": "2026-02-30T14:00",
            "end_iso": "tomorrow", "utc_offset": "+15:00"
        }]}
        action = server.extract_json(json.dumps(payload, ensure_ascii=False), self.SAMPLE)["actions"][0]
        self.assertEqual("", action["start_iso"])
        self.assertEqual("", action["end_iso"])
        self.assertEqual("", action["utc_offset"])


class LookupContextTests(unittest.TestCase):
    def test_key_validation_runs_a_real_bounded_inference_probe(self):
        class Response:
            def __init__(self, body):
                self.body = body

            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self):
                return json.dumps(self.body).encode()

        requests = []

        def open_request(request, timeout):
            requests.append((request, timeout))
            if request.full_url.endswith('/models'):
                return Response({"data": [{"id": server.MODEL}]})
            return Response({
                "choices": [{"message": {"content": '{"ok":true}'}}]
            })

        with mock.patch.object(server, "BASE_URL", "https://api.deepseek.com"), \
                mock.patch.object(server, "MODEL", "deepseek-v4-flash"), \
                mock.patch.object(server, "provider_urlopen", side_effect=open_request):
            result = server.validate_api_key("test-secret-key")
        self.assertTrue(result["ok"])
        self.assertEqual(2, len(requests))
        probe = json.loads(requests[1][0].data.decode())
        self.assertEqual({"type": "disabled"}, probe["thinking"])
        self.assertEqual(32, probe["max_tokens"])
        self.assertNotIn("test-secret-key", requests[1][0].data.decode())

    def test_realtime_model_call_disables_thinking_and_bounds_output(self):
        class Response:
            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self):
                return json.dumps({
                    "choices": [{"message": {"content": '{"entities":[]}'}}]
                }).encode()

        captured = {}

        def open_request(request, timeout):
            captured.update(json.loads(request.data.decode()))
            self.assertEqual(15, timeout)
            return Response()

        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "BASE_URL", "https://api.deepseek.com"), \
                mock.patch.object(server, "provider_urlopen", side_effect=open_request):
            server.call_llm([{"role": "user", "content": "RAG"}])
        self.assertEqual({"type": "disabled"}, captured["thinking"])
        self.assertEqual(600, captured["max_tokens"])
        self.assertEqual({"type": "json_object"}, captured["response_format"])

    def test_siliconflow_uses_its_no_thinking_parameter(self):
        payload = {}
        server.add_no_thinking_parameter(payload, "https://api.siliconflow.cn/v1")
        self.assertEqual(False, payload["enable_thinking"])
        self.assertNotIn("thinking", payload)

    def test_model_endpoint_rejects_nonlocal_plain_http(self):
        with self.assertRaises(ValueError):
            server.normalize_model_endpoint("http://example.com/v1", "example/model")
        endpoint, model = server.normalize_model_endpoint(
            "http://127.0.0.1:11434/v1/", "local-model")
        self.assertEqual("http://127.0.0.1:11434/v1", endpoint)
        self.assertEqual("local-model", model)

    def test_cache_key_changes_with_context(self):
        first = server.lookup_cache_key("model", "机器学习模型")
        second = server.lookup_cache_key("model", "时装模特 model")
        self.assertNotEqual(first, second)

    def test_context_is_normalized_and_bounded(self):
        context = "  第一行\n\n第二行  " + ("很长" * 400)
        normalized = server.normalize_lookup_context(context)
        self.assertLessEqual(len(normalized), 500)
        self.assertFalse(normalized.startswith(" "))
        self.assertNotIn("\n", normalized)

    def test_previous_explanation_is_normalized_and_bounded(self):
        value = "  第一版\n解释  " + ("很长" * 400)
        normalized = server.normalize_previous_explanation(value)
        self.assertLessEqual(len(normalized), 500)
        self.assertEqual("第一版 解释", normalized[:6])
        self.assertNotIn("\n", normalized)

    def test_refresh_bypasses_cache_and_replaces_current_result(self):
        original_cache = dict(server.LOOKUP_CACHE)
        try:
            server.LOOKUP_CACHE.clear()
            key = server.lookup_cache_key("RAG", "在知识库中使用 RAG")
            server.LOOKUP_CACHE[key] = {
                "term": "RAG",
                "explanation": "旧解释",
                "entities": [],
                "sources": [],
                "lookup_mode": "model",
                "can_refresh": True,
            }
            generated = json.dumps({
                "explanation": "RAG 会先检索资料，再让模型结合资料回答。",
                "entities": [],
            }, ensure_ascii=False)
            with mock.patch.object(server, "API_KEY", "test-key"), \
                    mock.patch.object(server, "call_llm", return_value=generated) as call:
                result = server.lookup(
                    "RAG",
                    context="在知识库中使用 RAG",
                    refresh=True,
                    previous_explanation="旧解释",
                )
            self.assertEqual(1, call.call_count)
            self.assertNotEqual("旧解释", result["explanation"])
            self.assertTrue(result["can_refresh"])
            self.assertEqual(result["explanation"], server.LOOKUP_CACHE[key]["explanation"])
        finally:
            server.LOOKUP_CACHE.clear()
            server.LOOKUP_CACHE.update(original_cache)

    def test_model_lookup_failure_falls_back_to_retryable_chinese_result(self):
        original_cache = dict(server.LOOKUP_CACHE)
        try:
            server.LOOKUP_CACHE.clear()
            with mock.patch.object(server, "API_KEY", "test-key"), \
                    mock.patch.object(server, "call_llm", side_effect=TimeoutError("slow")):
                result = server.lookup("bootcamp", context="参加 bootcamp 培训")
            self.assertEqual("local_fallback", result["lookup_mode"])
            self.assertTrue(result["can_refresh"])
            self.assertRegex(result["explanation"], r"[\u4e00-\u9fff]")
        finally:
            server.LOOKUP_CACHE.clear()
            server.LOOKUP_CACHE.update(original_cache)

    def test_timeout_for_unknown_term_does_not_start_public_network_lookup(self):
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm", side_effect=TimeoutError()), \
                mock.patch.object(server, "public_lookup") as public:
            result = server.lookup("unfamiliar_test_concept", refresh=True)
        public.assert_not_called()
        self.assertTrue(result["can_refresh"])
        self.assertRegex(result["explanation"], r"[\u4e00-\u9fff]")


class AnalysisCostControlTests(unittest.TestCase):
    def setUp(self):
        server.ANALYZE_CACHE.clear()
        server.ANALYZE_INFLIGHT.clear()
        server.MODEL_ANALYSIS_CALLS.clear()

    def tearDown(self):
        server.ANALYZE_CACHE.clear()
        server.ANALYZE_INFLIGHT.clear()
        server.MODEL_ANALYSIS_CALLS.clear()

    def test_sentence_without_plausible_candidate_skips_model(self):
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline") as called:
            result = server.analyze("好的，谢谢！")
        called.assert_not_called()
        self.assertEqual("local_no_candidate", result["analysis_mode"])

    def test_identical_text_reuses_bounded_analysis_cache(self):
        model_json = json.dumps({
            "entities": [{"text": "OneAPI", "start": 3, "end": 9}],
            "actions": [],
        })
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline", return_value=model_json) as called:
            first = server.analyze("使用 OneAPI")
            second = server.analyze("使用 OneAPI")
        self.assertEqual("llm", first["analysis_mode"])
        self.assertTrue(second["analysis_cached"])
        self.assertEqual(1, called.call_count)
        self.assertEqual(320, called.call_args.kwargs["max_tokens"])

    def test_cache_does_not_reuse_offsets_across_whitespace_changes(self):
        model_json = json.dumps({"entities": [], "actions": []})
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline", return_value=model_json) as called:
            server.analyze("使用 OneAPI")
            server.analyze("使用  OneAPI")
        self.assertEqual(2, called.call_count)

    def test_hourly_ceiling_preserves_local_highlights(self):
        model_json = json.dumps({
            "entities": [{"text": "OneAPI", "start": 3, "end": 9}],
            "actions": [],
        })
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "MODEL_ANALYSIS_LIMIT_PER_HOUR", 1), \
                mock.patch.object(server, "call_llm_with_deadline", return_value=model_json) as called:
            server.analyze("使用 OneAPI")
            limited = server.analyze("使用 PaddleOCR")
        self.assertEqual(1, called.call_count)
        self.assertEqual("local_budget", limited["analysis_mode"])
        self.assertTrue(limited["entities"])

    def test_simultaneous_identical_text_coalesces_paid_request(self):
        entered = threading.Event()
        release = threading.Event()
        model_json = json.dumps({
            "entities": [{"text": "OneAPI", "start": 3, "end": 9}],
            "actions": [],
        })

        def delayed_model(*args, **kwargs):
            entered.set()
            release.wait(2)
            return model_json

        results = []
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline", side_effect=delayed_model) as called:
            first = threading.Thread(target=lambda: results.append(server.analyze("使用 OneAPI")))
            second = threading.Thread(target=lambda: results.append(server.analyze("使用 OneAPI")))
            first.start()
            self.assertTrue(entered.wait(1))
            second.start()
            release.set()
            first.join(2)
            second.join(2)

        self.assertEqual(1, called.call_count)
        self.assertEqual(2, len(results))
        self.assertTrue(any(item.get("analysis_coalesced") for item in results))


if __name__ == "__main__":
    unittest.main()
