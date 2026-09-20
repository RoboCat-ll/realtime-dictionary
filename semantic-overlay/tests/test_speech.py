import io
import json
import math
import struct
import unittest
from unittest import mock
import urllib.error
import server
from threading import Thread
from urllib.request import Request, urlopen


def wav():
    samples = [int(3200 * math.sin(2 * math.pi * 440 * index / 16000)) for index in range(6400)]
    data = struct.pack("<%dh" % len(samples), *samples)
    return (b"RIFF" + (36 + len(data)).to_bytes(4, "little") + b"WAVE" +
            b"fmt " + (16).to_bytes(4, "little") + (1).to_bytes(2, "little") +
            (1).to_bytes(2, "little") + (16000).to_bytes(4, "little") +
            (32000).to_bytes(4, "little") + (2).to_bytes(2, "little") +
            (16).to_bytes(2, "little") + b"data" + len(data).to_bytes(4, "little") + data)


def silent_wav():
    data = b"\x00" * (16000 * 2)
    return (b"RIFF" + (36 + len(data)).to_bytes(4, "little") + b"WAVE" +
            b"fmt " + (16).to_bytes(4, "little") + (1).to_bytes(2, "little") +
            (1).to_bytes(2, "little") + (16000).to_bytes(4, "little") +
            (32000).to_bytes(4, "little") + (2).to_bytes(2, "little") +
            (16).to_bytes(2, "little") + b"data" + len(data).to_bytes(4, "little") + data)


class SpeechTests(unittest.TestCase):
    def test_http_transcribe_requires_token_and_returns_text(self):
        http = server.ThreadingHTTPServer(('127.0.0.1', 0), server.Handler)
        thread = Thread(target=http.serve_forever, daemon=True); thread.start()
        try:
            with mock.patch.object(server, 'PORT', http.server_address[1]):
                body = json.dumps({'audio_base64': __import__('base64').b64encode(wav()).decode()}).encode()
                with self.assertRaises(urllib.error.HTTPError) as denied:
                    urlopen(Request('http://127.0.0.1:%d/transcribe' % http.server_address[1], data=body,
                                    headers={'Content-Type': 'application/json'}), timeout=3)
                self.assertEqual(403, denied.exception.code)
                with mock.patch.object(server, 'transcribe_audio', return_value={'ok': True, 'text': '你好', 'model': 'fixture'}):
                    request = Request('http://127.0.0.1:%d/transcribe' % http.server_address[1], data=body,
                                      headers={'Content-Type': 'application/json',
                                               'X-RealtimeDictionary-Token': server.TOKEN})
                    with urlopen(request, timeout=3) as response:
                        self.assertEqual('你好', json.load(response)['text'])
        finally:
            http.shutdown(); http.server_close(); thread.join()

    def test_transcription_strips_model_tokens(self):
        response = mock.MagicMock()
        response.__enter__.return_value.read.return_value = json.dumps(
            {"text": "<|zh|><|NEUTRAL|> 你好 OneAPI "}).encode()
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "BASE_URL", "https://api.siliconflow.cn/v1"), \
             mock.patch.object(server, "provider_urlopen", return_value=response) as opened:
            result = server.transcribe_audio(wav())
        self.assertEqual("你好 OneAPI", result["text"])
        request = opened.call_args.args[0]
        self.assertIn(b'FunAudioLLM/SenseVoiceSmall', request.data)
        self.assertIn(b'speech.wav', request.data)

    def test_silence_is_discarded_before_provider_request(self):
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "BASE_URL", "https://api.siliconflow.cn/v1"), \
             mock.patch.object(server, "provider_urlopen") as opened:
            result = server.transcribe_audio(silent_wav())
        opened.assert_not_called()
        self.assertEqual("", result["text"])
        self.assertEqual("silence", result["discarded"])

    def test_rejects_wrong_provider_and_invalid_wav(self):
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "BASE_URL", "https://api.deepseek.com"):
            with self.assertRaisesRegex(RuntimeError, "硅基流动"):
                server.transcribe_audio(wav())
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "BASE_URL", "https://api.siliconflow.cn/v1"):
            with self.assertRaisesRegex(ValueError, "WAV"):
                server.transcribe_audio(b"x" * 44)

    def test_provider_error_does_not_expose_body(self):
        failure = urllib.error.HTTPError("url", 401, "bad", {}, io.BytesIO(b"secret body"))
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "BASE_URL", "https://api.siliconflow.cn/v1"), \
             mock.patch.object(server, "provider_urlopen", side_effect=failure):
            with self.assertRaisesRegex(RuntimeError, "密钥无效") as caught:
                server.transcribe_audio(wav())
        self.assertNotIn("secret", str(caught.exception))
        self.assertFalse(caught.exception.retryable)

    def test_insufficient_balance_is_named_and_not_retryable(self):
        failure = urllib.error.HTTPError("url", 402, "payment", {}, io.BytesIO(b"private provider body"))
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "BASE_URL", "https://api.siliconflow.cn/v1"), \
             mock.patch.object(server, "provider_urlopen", side_effect=failure):
            with self.assertRaisesRegex(server.SpeechProviderError, "余额不足") as caught:
                server.transcribe_audio(wav())
        self.assertFalse(caught.exception.retryable)
        self.assertNotIn("private", str(caught.exception))

    def test_network_error_is_retryable_without_exposing_details(self):
        with mock.patch.object(server, "API_KEY", "test-key"), \
             mock.patch.object(server, "BASE_URL", "https://api.siliconflow.cn/v1"), \
             mock.patch.object(server, "provider_urlopen",
                               side_effect=urllib.error.URLError("private network detail")):
            with self.assertRaisesRegex(server.SpeechProviderError, "暂时不可用") as caught:
                server.transcribe_audio(wav())
        self.assertTrue(caught.exception.retryable)
        self.assertNotIn("private", str(caught.exception))
