import json
import unittest
import urllib.error
from threading import Thread
from unittest import mock
from urllib.request import Request, urlopen

import server


class CaptionTranslationTests(unittest.TestCase):
    def test_explicit_translation_sends_only_the_selected_excerpt(self):
        source = "The bootcamp starts at five."
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "call_llm_with_deadline",
                               return_value="训练营五点开始。") as translate:
            result = server.translate_caption_text(source)
        self.assertEqual("训练营五点开始。", result["translation"])
        messages = translate.call_args.args[0]
        self.assertEqual(source, messages[1]["content"])
        self.assertFalse(translate.call_args.kwargs["json_mode"])

    def test_missing_key_and_oversized_excerpt_are_rejected_without_model_call(self):
        with mock.patch.object(server, "call_llm_with_deadline") as translate:
            with self.assertRaises(ValueError):
                server.translate_caption_text("x" * 1001)
            with mock.patch.object(server, "API_KEY", ""):
                with self.assertRaises(RuntimeError):
                    server.translate_caption_text("Hello")
        translate.assert_not_called()

    def test_http_translation_requires_token_and_rejects_large_requests(self):
        http = server.ThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        thread = Thread(target=http.serve_forever, daemon=True)
        thread.start()
        url = "http://127.0.0.1:%d/caption/translate" % http.server_address[1]
        try:
            with mock.patch.object(server, "PORT", http.server_address[1]):
                body = json.dumps({"text": "Hello there"}).encode()
                with self.assertRaises(urllib.error.HTTPError) as denied:
                    urlopen(Request(url, data=body,
                                    headers={"Content-Type": "application/json"}), timeout=3)
                self.assertEqual(403, denied.exception.code)
                headers = {"Content-Type": "application/json",
                           "X-RealtimeDictionary-Token": server.TOKEN}
                with mock.patch.object(server, "translate_caption_text",
                                       return_value={"translation": "你好"}) as translate:
                    with urlopen(Request(url, data=body, headers=headers), timeout=3) as response:
                        self.assertEqual("你好", json.load(response)["translation"])
                    translate.assert_called_once_with("Hello there")
                too_long = json.dumps({"text": "x" * 1001}).encode()
                with self.assertRaises(urllib.error.HTTPError) as invalid:
                    urlopen(Request(url, data=too_long, headers=headers), timeout=3)
                self.assertEqual(400, invalid.exception.code)
        finally:
            http.shutdown()
            http.server_close()
            thread.join()


if __name__ == "__main__":
    unittest.main()
