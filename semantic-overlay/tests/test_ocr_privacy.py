import importlib.util
import json
from pathlib import Path
import sys
import threading
import types
import unittest
from unittest import mock
from http.server import ThreadingHTTPServer
from urllib.request import Request, build_opener, ProxyHandler
from urllib.error import HTTPError


class OcrBoundaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        pil = types.ModuleType("PIL")
        pil.Image = mock.Mock()
        pil.ImageGrab = mock.Mock()
        paddle = types.ModuleType("paddleocr")
        paddle.PaddleOCR = mock.Mock()
        numpy = types.ModuleType("numpy")
        numpy.asarray = mock.Mock(return_value="memory-only-pixels")
        with mock.patch.dict(sys.modules, {"PIL": pil, "paddleocr": paddle, "numpy": numpy}):
            spec = importlib.util.spec_from_file_location("ocr_fixture_module",
                Path(__file__).resolve().parents[1] / "ocr_service.py")
            cls.ocr = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(cls.ocr)

    def test_ocr_uses_memory_image_without_screenshot_file(self):
        image = mock.Mock()
        image.size = (200, 100)
        engine = mock.Mock()
        engine.predict.return_value = [{"rec_texts": [], "rec_boxes": [], "rec_scores": []}]
        with mock.patch.object(self.ocr.ImageGrab, "grab", return_value=image), \
                mock.patch.object(self.ocr, "get_ocr", return_value=engine), \
                mock.patch.object(self.ocr, "log"):
            result = self.ocr.scan_region(0, 0, 200, 100)
        engine.predict.assert_called_once_with("memory-only-pixels")
        image.save.assert_not_called()
        self.assertEqual(0, result["text_blocks"])

    def test_scan_requires_token_local_host_and_small_body(self):
        # A forged Host header must reach the fixture server, not a system proxy.
        urlopen = build_opener(ProxyHandler({})).open
        http = ThreadingHTTPServer(("127.0.0.1", 0), self.ocr.Handler)
        thread = threading.Thread(target=http.serve_forever, daemon=True)
        thread.start()
        try:
            with mock.patch.object(self.ocr, "session_token", return_value="fixture-token"), \
                    mock.patch.object(self.ocr, "scan_region", return_value={"ok": True}) as scan:
                for token, host in (("", "127.0.0.1:8878"), ("wrong", "127.0.0.1:8878"),
                                    ("fixture-token", "foreign.test:8878")):
                    request = Request("http://127.0.0.1:%s/scan" % http.server_port,
                        data=b'{"x":0,"y":0,"w":50,"h":50}',
                        headers={"Host": host, "X-RealtimeDictionary-Token": token})
                    with self.assertRaises(HTTPError) as error: urlopen(request, timeout=2)
                    self.assertEqual(403, error.exception.code)
                scan.assert_not_called()
                request = Request("http://127.0.0.1:%s/scan" % http.server_port,
                    data=b'{"x":0,"y":0,"w":50,"h":50}',
                    headers={"Host": "127.0.0.1:8878", "X-RealtimeDictionary-Token": "fixture-token"})
                with urlopen(request, timeout=2) as response:
                    self.assertTrue(json.load(response)["ok"])
                scan.assert_called_once_with(0, 0, 50, 50)
        finally:
            http.shutdown()
            http.server_close()
            thread.join(2)
