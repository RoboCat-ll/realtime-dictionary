import os
import json
import re
import sys
import threading
import tempfile
import unittest
from urllib.error import HTTPError
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

    def test_typesafe_settings_load_from_user_config(self):
        with tempfile.TemporaryDirectory() as directory:
            config_dir = os.path.join(directory, "RealtimeDictionary")
            os.makedirs(config_dir)
            with open(os.path.join(config_dir, "config.json"), "w", encoding="utf-8") as stream:
                json.dump({
                    "typesafe_api_key": "typesafe-fixture",
                    "typesafe_base_url": "https://example.typesafe.test/",
                    "typesafe_model": "jev-fixture",
                }, stream)
            with mock.patch.dict(os.environ, {"APPDATA": directory}, clear=True):
                config = server.load_config()
        self.assertEqual("typesafe-fixture", config["typesafe_api_key"])
        self.assertEqual("https://example.typesafe.test", config["typesafe_base_url"])
        self.assertEqual("jev-fixture", config["typesafe_model"])

    def test_typesafe_environment_overrides_user_config(self):
        with tempfile.TemporaryDirectory() as directory:
            config_dir = os.path.join(directory, "RealtimeDictionary")
            os.makedirs(config_dir)
            with open(os.path.join(config_dir, "config.json"), "w", encoding="utf-8") as stream:
                json.dump({"typesafe_api_key": "file-key", "typesafe_model": "file-model"}, stream)
            with mock.patch.dict(os.environ, {
                "APPDATA": directory,
                "TYPESAFE_API_KEY": "environment-key",
                "TYPESAFE_MODEL": "environment-model",
            }, clear=True):
                config = server.load_config()
        self.assertEqual("environment-key", config["typesafe_api_key"])
        self.assertEqual("environment-model", config["typesafe_model"])

    def test_typesafe_validation_requires_noul_probability(self):
        class Response:
            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

            def read(self):
                return json.dumps({"answers": {"highlight": {
                    "type": "noul", "noul": 0.92}}}).encode("utf-8")

        with mock.patch.object(server.urllib.request, "urlopen", return_value=Response()) as opener:
            result = server.validate_typesafe_key(
                "typesafe-fixture", "https://api.typesafe.ai", "jev-latest")
        self.assertTrue(result["ok"])
        request = opener.call_args.args[0]
        payload = json.loads(request.data.decode("utf-8"))
        self.assertEqual("noul", payload["questions"]["highlight"]["type"])
        self.assertEqual("jev-latest", payload["model"])

    def test_health_exposes_versioned_provider_contract_without_secrets(self):
        http = server.ThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        worker = threading.Thread(target=http.serve_forever, daemon=True)
        worker.start()
        try:
            with mock.patch.object(server, "PORT", http.server_address[1]):
                with urlopen("http://127.0.0.1:%d/health" % http.server_address[1], timeout=3) as response:
                    result = json.loads(response.read().decode("utf-8"))
        finally:
            http.shutdown()
            http.server_close()
        self.assertEqual("realtime-dictionary", result["product_id"])
        self.assertEqual(server.API_PROTOCOL_VERSION, result["protocol_version"])
        self.assertIn("analysis_provider", result)
        self.assertIn("explanation_model", result)
        self.assertEqual(server.SELECTION_MODEL, result["selection_model"])
        self.assertNotIn("api_key", result)
        self.assertNotIn("token", result)


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

    def test_lookup_timeout_is_bounded_for_interactive_use(self):
        with mock.patch.dict(os.environ, {
                "REALTIME_DICTIONARY_LOOKUP_TIMEOUT_SECONDS": "7"}):
            self.assertEqual(7, server.lookup_timeout_seconds())
        with mock.patch.dict(os.environ, {
                "REALTIME_DICTIONARY_LOOKUP_TIMEOUT_SECONDS": "60"}):
            self.assertEqual(8, server.lookup_timeout_seconds())

    def test_lookup_uses_hard_wall_clock_deadline(self):
        generated = json.dumps({
            "canonical_term": "RAG",
            "explanation": "RAG 会先检索资料再生成回答。",
            "entities": [],
        }, ensure_ascii=False)
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline",
                                  return_value=generated) as call:
            server.lookup("RAG", context="知识库检索", refresh=True)
        self.assertEqual(server.LOOKUP_TIMEOUT_SECONDS,
                         call.call_args.kwargs["deadline_seconds"])
        self.assertEqual(server.LOOKUP_TIMEOUT_SECONDS,
                         call.call_args.kwargs["request_timeout"])
        self.assertEqual(server.LOOKUP_MODEL, call.call_args.kwargs["model"])

    def test_siliconflow_uses_a_dedicated_fast_lookup_model(self):
        self.assertEqual(
            "Qwen/Qwen3.5-35B-A3B",
            server.select_lookup_model(
                "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V4-Flash"))
        self.assertEqual(
            "custom-model",
            server.select_lookup_model(
                "https://example.test/v1", "custom-model"))
        self.assertEqual(
            "override-model",
            server.select_lookup_model(
                "https://api.siliconflow.cn/v1", "configured", "override-model"))

    def test_siliconflow_uses_a_dedicated_fast_selection_model(self):
        self.assertEqual(
            "Qwen/Qwen2.5-7B-Instruct",
            server.select_selection_model(
                "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V4-Flash"))
        self.assertEqual(
            "custom-model",
            server.select_selection_model(
                "https://example.test/v1", "custom-model"))
        self.assertEqual(
            "override-model",
            server.select_selection_model(
                "https://api.siliconflow.cn/v1", "configured", "override-model"))

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

    def test_novel_versioned_and_chinese_embedded_product_names_are_candidates(self):
        text = "grok4.5 道德低，ollama本地部署很简单。hello 不需要查词。"
        candidates = {item[2]: item[3] for item in server.extract_candidates(text)}
        self.assertGreaterEqual(candidates["grok4.5"], server.JEV_AUTO_ACCEPT_SCORE)
        self.assertGreaterEqual(candidates["ollama"], 70)
        self.assertNotIn("hello", candidates)
        instant = server.deterministic_strong_entities(text)
        self.assertIn("grok4.5", [item["text"] for item in instant["entities"]])
        self.assertNotIn("ollama", [item["text"] for item in instant["entities"]])

    def test_chinese_sentence_fragment_is_not_sent_as_candidate(self):
        terms = [item[2] for item in server.extract_candidates(
            "FAISS 和 JWT 是不同领域的技术。")]
        self.assertNotIn("是不同领域的技术", terms)

    def test_neural_layers_survive_instant_and_timeout_without_model(self):
        terms = ["Conv2D", "Conv2d", "conv2d", "nn.Conv2d",
                 "torch.nn.ConvTranspose2d", "BatchNorm2d", "MaxPool2D"]
        text = "，".join(terms)
        for analyze in (server.deterministic_strong_entities,
                        server.conservative_fallback_entities):
            with self.subTest(path=analyze.__name__):
                entities = analyze(text)["entities"]
                self.assertEqual(terms, [item["text"] for item in entities])
                for item in entities:
                    self.assertEqual(item["text"], text[item["start"]:item["end"]])

    def test_neural_layer_rule_does_not_accept_fragments_or_arbitrary_numbers(self):
        for term in ("MyConv2D", "Conv2Dextra", "Conv2", "Conv9d", "user123",
                     "PT", "DO", "x.nn.Conv2d"):
            with self.subTest(term=term):
                self.assertEqual([], server.deterministic_strong_entities(term)["entities"])

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


class SelectionAnalysisTests(unittest.TestCase):
    def test_selection_terms_must_be_exact_complete_source_terms(self):
        source = "我们用 Bootcamp 学习 oneAPI，随后休息。"
        generated = json.dumps({
            "explanation": "这段话描述一次集中学习安排。",
            "terms": [
                {"text": "bootcamp", "explanation": "集中训练营。"},
                {"text": "API", "explanation": "接口。"},
                {"text": "不存在的词", "explanation": "模型臆造。"},
                {"text": "随后", "explanation": "普通连接词。"},
                {"text": "oneAPI", "explanation": "跨架构开发体系。"},
            ],
        }, ensure_ascii=False)
        result = server.extract_selection_json(generated, source)
        self.assertEqual("这段话描述一次集中学习安排。", result["explanation"])
        self.assertEqual(["Bootcamp", "oneAPI"],
                         [item["text"] for item in result["terms"]])

    def test_selection_ocr_correction_repairs_terms_and_drops_orphan_date(self):
        source = "和。neapi的同事约一个b。。tcamp讨论会啊 2026/09/"
        generated = json.dumps({
            "corrected_text": "和 oneAPI 的同事约一个 bootcamp 讨论会啊",
            "explanation": "对方准备约同事开一场讨论会。",
            "terms": [
                {"text": "oneAPI", "explanation": "跨架构开发体系。"},
                {"text": "bootcamp", "explanation": "集中讨论或训练活动。"},
                {"text": "知识点", "explanation": "需要学习的内容。"},
            ],
        }, ensure_ascii=False)
        result = server.extract_selection_json(
            generated, source, allow_ocr_correction=True)
        self.assertTrue(result["ocr_corrected"])
        self.assertEqual("和 oneAPI 的同事约一个 bootcamp 讨论会啊",
                         result["display_text"])
        self.assertEqual(["oneAPI", "bootcamp"],
                         [item["text"] for item in result["terms"]])

    def test_selection_local_ocr_repair_is_bounded_to_unique_technical_terms(self):
        source = "和。neapi同事约一个b。。tcamp，然后说 one a pi 2026/09/"
        repaired, changed = server.repair_selection_ocr_locally(source)
        self.assertTrue(changed)
        self.assertEqual("和。oneAPI同事约一个bootcamp，然后说 oneAPI", repaired)
        ordinary, changed = server.repair_selection_ocr_locally(
            "we use ordinary words in this sentence")
        self.assertFalse(changed)
        self.assertEqual("we use ordinary words in this sentence", ordinary)

    def test_selection_local_ocr_repair_restores_uniquely_repeated_category_label(self):
        source = ("或者说任务或者时间安排，这句话要能自动被识别成/ "
                  "oneAPI是知识点，bootcamp是知识点")
        repaired, changed = server.repair_selection_ocr_locally(source)
        self.assertTrue(changed)
        self.assertIn("识别成任务，oneAPI", repaired)

        partial, changed = server.repair_selection_ocr_locally(
            "这是任务，这句话被识别成务 oneAPI是知识点")
        self.assertTrue(changed)
        self.assertIn("识别成任务，oneAPI", partial)

        ambiguous, changed = server.repair_selection_ocr_locally(
            "任务和日程都提到了，后面识别成/ oneAPI是知识点")
        self.assertFalse(changed)
        self.assertIn("识别成/", ambiguous)

        unrelated, changed = server.repair_selection_ocr_locally(
            "任务路径是 docs/tasks / oneAPI是知识点")
        self.assertFalse(changed)
        self.assertIn("/", unrelated)

    def test_selection_ocr_correction_rejects_paraphrase_and_exact_text_rewrite(self):
        source = "我们9月3号下午开会讨论 oneAPI"
        generated = json.dumps({
            "corrected_text": "团队计划组织一次技术交流活动",
            "explanation": "这是会议安排。",
            "terms": [],
        }, ensure_ascii=False)
        ocr = server.extract_selection_json(
            generated, source, allow_ocr_correction=True)
        exact = server.extract_selection_json(
            json.dumps({"corrected_text": "改写文本", "explanation": "说明", "terms": []},
                       ensure_ascii=False),
            source, allow_ocr_correction=False)
        self.assertFalse(ocr["ocr_corrected"])
        self.assertEqual(source, ocr["display_text"])
        self.assertFalse(exact["ocr_corrected"])
        self.assertEqual(source, exact["display_text"])

    def test_selection_ocr_correction_restores_only_tiny_context_unique_chinese_omission(self):
        source = "或者说任务或者时间安排，这句话要能自动被识别成/ oneAPI是知识点"
        corrected = "或者说任务或者时间安排，这句话要能自动被识别成任务，oneAPI是知识点"
        display, changed = server.accept_selection_correction(source, corrected)
        self.assertTrue(changed)
        self.assertEqual(corrected, display)

        overreach = "或者说任务或者时间安排，这句话要能自动被识别成重要日程任务，oneAPI是知识点"
        display, changed = server.accept_selection_correction(source, overreach)
        self.assertFalse(changed)
        self.assertEqual(source, display)

        readable = "这句话会被识别成任务，oneAPI是知识点"
        dropped = "这句话会被识别成，oneAPI是知识点"
        display, changed = server.accept_selection_correction(readable, dropped)
        self.assertFalse(changed)
        self.assertEqual(readable, display)

    def test_selection_analysis_uses_one_bounded_model_request(self):
        generated = json.dumps({
            "explanation": "对方建议先通过小范围测试验证交互。",
            "terms": [{"text": "bootcamp", "explanation": "集中训练营。"}],
        }, ensure_ascii=False)
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline",
                                  return_value=generated) as call:
            result = server.analyze_selection("先测试 bootcamp 的交互")
        self.assertEqual("model", result["analysis_mode"])
        self.assertEqual(1, call.call_count)
        self.assertEqual(server.ANALYSIS_TIMEOUT_SECONDS,
                         call.call_args.kwargs["deadline_seconds"])
        self.assertEqual(server.SELECTION_MODEL, call.call_args.kwargs["model"])

    def test_selection_model_result_retains_exact_local_terms(self):
        generated = json.dumps({
            "explanation": "对方建议把代码放入仓库，方便后续协作。",
            "terms": [],
        }, ensure_ascii=False)
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline",
                                  return_value=generated):
            result = server.analyze_selection("建议把代码上传到 GitHub 并建立仓库")
        self.assertEqual("model", result["analysis_mode"])
        self.assertEqual(["GitHub"], [item["text"] for item in result["terms"]])

    def test_selection_timeout_discloses_timeout_instead_of_generic_unavailable(self):
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline",
                                  side_effect=TimeoutError("slow")):
            result = server.analyze_selection("建议把代码上传到 GitHub")
        self.assertEqual("local_fallback", result["analysis_mode"])
        self.assertIn("超时", result["explanation"])
        self.assertEqual("模型请求超时", result["notice"])

    def test_selection_without_model_is_honest_and_offline(self):
        with mock.patch.object(server, "API_KEY", ""), \
                mock.patch.object(server, "call_llm_with_deadline") as call:
            result = server.analyze_selection("使用 bootcamp 学习")
        call.assert_not_called()
        self.assertEqual("local_unavailable", result["analysis_mode"])
        self.assertIn("未配置", result["explanation"])
        self.assertEqual(["bootcamp"], [item["text"] for item in result["terms"]])

    def test_selection_local_terms_deduplicate_repeated_occurrences(self):
        terms = server.selection_local_terms(
            "oneAPI 可以统一工具链，后面再次提到 oneAPI 和 bootcamp")
        self.assertEqual(["oneAPI", "bootcamp"],
                         [item["text"] for item in terms])

    def test_selection_http_requires_token_and_rejects_oversize_text(self):
        http = server.ThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        worker = threading.Thread(target=http.serve_forever, daemon=True)
        worker.start()
        url = "http://127.0.0.1:%d/selection/analyze" % http.server_address[1]
        try:
            with mock.patch.object(server, "PORT", http.server_address[1]):
                no_token = Request(url, data=json.dumps({"text": "RAG"}).encode(),
                                   headers={"Content-Type": "application/json"})
                with self.assertRaises(HTTPError) as denied:
                    urlopen(no_token, timeout=3)
                self.assertEqual(403, denied.exception.code)

                oversized = Request(
                    url,
                    data=json.dumps({"text": "a" * 1001}).encode(),
                    headers={"Content-Type": "application/json",
                             "X-RealtimeDictionary-Token": server.TOKEN},
                )
                with self.assertRaises(HTTPError) as rejected:
                    urlopen(oversized, timeout=3)
                self.assertEqual(400, rejected.exception.code)
        finally:
            http.shutdown()
            http.server_close()
            worker.join(timeout=3)


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
    def test_instant_lookup_uses_bundled_glossary_without_network_or_model(self):
        with mock.patch.object(server, "call_llm_with_deadline") as model, \
                mock.patch.object(server, "public_lookup") as public:
            result = server.instant_lookup("LLM", context="我们使用 LLM 处理文本")
        self.assertEqual("local_glossary", result["lookup_mode"])
        self.assertTrue(result["needs_model"])
        self.assertIn("大语言模型", result["explanation"])
        model.assert_not_called()
        public.assert_not_called()

    def test_instant_unknown_term_is_honest_and_requests_background_model(self):
        with mock.patch.object(server, "call_llm_with_deadline") as model, \
                mock.patch.object(server, "public_lookup") as public:
            result = server.instant_lookup(
                "grok4.5", context="网页端现在只有 grok4.5 可以使用")
        self.assertEqual("local_context", result["lookup_mode"])
        self.assertTrue(result["needs_model"])
        self.assertIn("不会用猜测冒充答案", result["explanation"])
        model.assert_not_called()
        public.assert_not_called()

    def test_repeated_explicit_lookup_calls_online_model_each_time(self):
        generated = json.dumps({
            "canonical_term": "LLM",
            "explanation": "LLM 是大语言模型。",
            "entities": [],
        }, ensure_ascii=False)
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm_with_deadline", return_value=generated) as call:
            first = server.lookup("LLM", context="使用 LLM")
            second = server.lookup("LLM", context="使用 LLM")
        self.assertEqual(2, call.call_count)
        self.assertEqual("model", first["lookup_mode"])
        self.assertEqual("model", second["lookup_mode"])

    def test_lookup_canonicalization_accepts_only_small_ocr_like_edits(self):
        self.assertTrue(server.plausible_ocr_canonicalization("“itHub0", "GitHub"))
        self.assertTrue(server.plausible_ocr_canonicalization(
            "realtime-dictionar", "realtime dictionary"))
        self.assertFalse(server.plausible_ocr_canonicalization("model", "GitHub"))
        self.assertFalse(server.plausible_ocr_canonicalization("AI", "API"))

    def test_lookup_returns_locally_validated_canonical_term(self):
        generated = json.dumps({
            "canonical_term": "GitHub",
            "explanation": "可能指 GitHub，一个代码托管与协作平台。",
            "entities": [],
        }, ensure_ascii=False)
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm", return_value=generated):
            result = server.lookup("“itHub0")
        self.assertEqual("GitHub", result["term"])

    def test_lookup_rejects_unrelated_model_canonical_term(self):
        parsed = server.extract_lookup_json(json.dumps({
            "canonical_term": "GitHub",
            "explanation": "这是一个无法确认的词。",
            "entities": [],
        }, ensure_ascii=False), "model")
        self.assertEqual("model", parsed["canonical_term"])

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

    def test_refresh_requests_a_new_model_explanation(self):
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

    def test_model_lookup_failure_falls_back_to_retryable_chinese_result(self):
        with mock.patch.object(server, "API_KEY", "test-key"), \
                mock.patch.object(server, "call_llm", side_effect=TimeoutError("slow")):
            result = server.lookup("bootcamp", context="参加 bootcamp 培训")
        self.assertEqual("local_fallback", result["lookup_mode"])
        self.assertTrue(result["can_refresh"])
        self.assertRegex(result["explanation"], r"[\u4e00-\u9fff]")

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
        # Keep the OpenAI-compatible fixture path deterministic even when the
        # current Windows profile has a real Jev credential configured.
        self.typesafe_patch = mock.patch.object(server, "TYPESAFE_API_KEY", "")
        self.typesafe_patch.start()
        self.addCleanup(self.typesafe_patch.stop)
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
        self.assertEqual(600, called.call_args.kwargs["max_tokens"])

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
