import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
from unittest import mock
from urllib.request import Request

import provider_policy as policy
import server


def pricing_page(rows):
    stream = "\n".join(str(i) + ":" + json.dumps(row) for i, row in enumerate(rows))
    return "<script>self.__next_f.push(" + json.dumps([1, stream]) + ")</script>"


def model_row(name="Qwen/free", price="0", completion="0"):
    return {"modelName": name, "status": "normal", "price": price,
            "subType": "chat", "pricing": [{"price": price, "specification": "prompt"},
                                               {"price": completion, "specification": "completion"}]}


class BillingGuardTests(unittest.TestCase):
    def test_prices_require_all_zero_and_exact_interface(self):
        prices = policy.parse_free_prices(pricing_page([
            model_row(), model_row("Qwen/paid", completion="0.1"),
            model_row("DeepSeek/free-alias"), model_row("Qwen/unknown", price="NaN")]))
        self.assertEqual({"Qwen/free"}, set(prices))
        with mock.patch.object(policy, "refresh_free_prices", return_value=prices):
            policy.enforce_free_only("https://api.siliconflow.cn/v1", "Qwen/free", "/chat/completions")
            with self.assertRaises(policy.PolicyBlocked):
                policy.enforce_free_only("https://api.siliconflow.cn/v1", "Qwen/free", "/audio/transcriptions")

    def test_deepseek_and_alias_hosts_block_without_network(self):
        for endpoint in ("https://API.SILICONFLOW.CN./v1", "https://api.siliconflow.com/v1"):
            with mock.patch.object(policy, "refresh_free_prices") as price, \
                    mock.patch.object(server.urllib.request, "build_opener") as network:
                request = Request(endpoint + "/chat/completions",
                                  data=json.dumps({"model": "Pro/deepseek-ai/DeepSeek-V4-Flash"}).encode())
                with self.assertRaises(policy.PolicyBlocked):
                    server.provider_urlopen(request, timeout=2)
                price.assert_not_called()
                network.assert_not_called()

    def test_price_fetch_failure_cannot_reuse_stale_free_status(self):
        with mock.patch.object(policy, "_checked_at", time.time()-901), \
                mock.patch.object(policy, "_prices", {"Qwen/free": {"operation": "/chat/completions"}}), \
                mock.patch.object(policy.urllib.request, "urlopen", side_effect=TimeoutError()):
            with self.assertRaises(policy.PolicyBlocked):
                policy.enforce_free_only("https://api.siliconflow.cn/v1", "Qwen/free", "/chat/completions")
            self.assertEqual({}, policy._prices)

    def test_validation_blocks_paid_model_before_catalog_or_probe(self):
        with mock.patch.object(server, "provider_urlopen") as network:
            result = server.validate_api_key("fixture-only", "https://api.siliconflow.cn/v1",
                                             "deepseek-ai/DeepSeek-V4-Flash")
            self.assertFalse(result["ok"])
            self.assertIn("请求未发送", result["message"])
            network.assert_not_called()

    def test_speech_blocked_before_audio_upload(self):
        request = Request("https://api.siliconflow.cn/v1/audio/transcriptions",
            data=b'--x\r\nContent-Disposition: form-data; name="model"\r\n\r\nunknown-ASR\r\n--x')
        with mock.patch.object(policy, "refresh_free_prices", return_value={}), \
                mock.patch.object(server.urllib.request, "build_opener") as network:
            with self.assertRaises(policy.PolicyBlocked):
                server.provider_urlopen(request, timeout=2)
            network.assert_not_called()

    def test_paid_official_provider_unchanged_by_siliconflow_rule(self):
        with mock.patch.object(policy, "refresh_free_prices") as prices:
            policy.enforce_free_only("https://api.deepseek.com/v1", "deepseek-flash", "/chat/completions")
            prices.assert_not_called()

    def test_authenticated_redirect_blocked(self):
        with mock.patch.object(server.urllib.request, "build_opener") as opener:
            server.provider_urlopen(Request("https://api.deepseek.com/models"), 1)
            handler = opener.call_args.args[0]
            with self.assertRaises(policy.PolicyBlocked):
                handler.redirect_request(None, None, 302, "Found", {}, "https://other.test/")


class UsagePrivacyTests(unittest.TestCase):
    def test_success_unknown_and_failure_survive_reload_without_content(self):
        with tempfile.TemporaryDirectory() as profile, mock.patch.dict(os.environ, {"APPDATA": profile}):
            for payload in ({"usage": {"prompt_tokens": 14, "completion_tokens": 3,
                                       "prompt_tokens_details": {"cached_tokens": 4}},
                             "choices": [{"message": {"content": "private response"}}]}, {"text": "private audio"}):
                record = policy.start_usage("https://api.deepseek.com", "fixture-model", "/chat/completions")
                response = policy.AccountedResponse(io.BytesIO(json.dumps(payload).encode()), record, time.monotonic())
                with response: response.read()
            # A process crash leaves submitted usage unknown, rather than silently zero.
            policy.start_usage("https://api.deepseek.com", "fixture-model", "/chat/completions")
            summary = policy.usage_summary()["groups"][0]
            self.assertEqual(3, summary["requests"])
            self.assertEqual(2, summary["unknown_usage_requests"])
            self.assertEqual(14, summary["reported_tokens"]["prompt_tokens"])
            content = Path(policy.ledger_path()).read_text(encoding="utf-8")
            self.assertNotIn("private", content)

    def test_ledger_write_failure_blocks_network(self):
        with mock.patch.object(policy, "write_usage", side_effect=OSError()), \
                mock.patch.object(server.urllib.request, "build_opener") as network:
            with self.assertRaises(policy.PolicyBlocked):
                server.provider_urlopen(Request("https://api.deepseek.com/chat/completions",
                    data=b'{"model":"fixture"}'), 2)
            network.assert_not_called()


@unittest.skipUnless(os.name == "nt", "Windows CurrentUser DPAPI")
class ProtectedConfigurationTests(unittest.TestCase):
    def make_config(self, directory):
        target = Path(directory) / "RealtimeDictionary" / "config.json"
        binary = Path(server.HERE) / "native-host/bin/CredentialProtectionTest.exe"
        if not binary.exists(): self.skipTest("Native credential diagnostic not built")
        subprocess.run([str(binary), str(target)], check=True)
        return target

    def test_native_encryption_loads_in_python_and_keeps_provider_scopes(self):
        with tempfile.TemporaryDirectory() as profile, mock.patch.dict(os.environ, {"APPDATA": profile}, clear=False):
            target = self.make_config(profile)
            config = server.load_config()
            self.assertEqual("fixture-not-a-real-credential", config["api_key"])
            self.assertEqual(config["api_key"], config["text_api_key"])
            self.assertNotIn("fixture-not-a-real-credential", target.read_text(encoding="utf-8"))
            self.assertEqual("", config["configuration_warning"])

    def test_changed_endpoint_and_tampered_ciphertext_never_fall_back(self):
        for mode in ("endpoint", "ciphertext"):
            with tempfile.TemporaryDirectory() as profile, mock.patch.dict(os.environ, {
                    "APPDATA": profile, "SILICONFLOW_API_KEY": "ambient-must-not-be-used"}, clear=False):
                target = self.make_config(profile)
                config = json.loads(target.read_text(encoding="utf-8"))
                if mode == "endpoint": config["base_url"] = "https://api.siliconflow.cn/other"
                else: config["text_api_key_protected"] = "dpapi-v1:invalid"
                target.write_text(json.dumps(config), encoding="utf-8")
                loaded = server.load_config()
                self.assertEqual("", loaded["api_key"])
                self.assertEqual("", loaded["text_api_key"])
                self.assertTrue(loaded["text_config_invalid"])

