# -*- coding: utf-8 -*-
"""Long-lived OCR worker for the native Windows host.

The service receives a physical-pixel screen rectangle, captures it, runs one
shared PaddleOCR instance, asks server.py to identify terms, and returns small
term rectangles relative to the captured window. It never creates UI.
"""

import io
import json
import os
import sys
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from PIL import Image, ImageGrab
from paddleocr import PaddleOCR

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = 8878
ANALYZE_URL = "http://127.0.0.1:8877/analyze"
SCAN_IMAGE = os.path.join(HERE, "_native_scan.png")
MAX_OCR_WIDTH = 1280
MAX_OCR_HEIGHT = 800

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
sys.stderr = sys.stdout

_ocr = None
_ocr_lock = threading.Lock()
_scan_lock = threading.Lock()


def log(message):
    line = "[%s] %s" % (time.strftime("%H:%M:%S"), message)
    try:
        print(line, flush=True)
    except Exception:
        pass
    try:
        with open(os.path.join(HERE, "_ocr_service.log"), "a", encoding="utf-8") as stream:
            stream.write(line + "\n")
    except Exception:
        pass


def get_ocr():
    global _ocr
    if _ocr is None:
        with _ocr_lock:
            if _ocr is None:
                started = time.time()
                log("Loading PaddleOCR models")
                _ocr = PaddleOCR(enable_mkldnn=False, lang="ch")
                log("PaddleOCR loaded in %.1fs" % (time.time() - started))
    return _ocr


def post_json(url, payload, timeout=70):
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json; charset=utf-8"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def normalize_box(box):
    if hasattr(box, "tolist"):
        box = box.tolist()
    if isinstance(box[0], (list, tuple)):
        xs = [point[0] for point in box]
        ys = [point[1] for point in box]
    else:
        xs = [box[0], box[2]]
        ys = [box[1], box[3]]
    x = float(min(xs))
    y = float(min(ys))
    return x, y, float(max(xs)) - x, float(max(ys)) - y


def match_entities(entities, texts, boxes):
    spans = []
    cursor = 0
    for text, box in zip(texts, boxes):
        spans.append((cursor, cursor + len(text), text, box))
        cursor += len(text)

    highlights = []
    seen = set()
    for entity in entities:
        start = entity.get("start")
        end = entity.get("end")
        term = entity.get("text", "")
        if not isinstance(start, int) or not isinstance(end, int) or end <= start:
            continue
        for span_start, span_end, text, box in spans:
            overlap_start = max(start, span_start)
            overlap_end = min(end, span_end)
            if overlap_start >= overlap_end:
                continue
            x, y, width, height = normalize_box(box)
            char_count = max(len(text), 1)
            left = (overlap_start - span_start) / char_count
            right = (overlap_end - span_start) / char_count
            term_x = x + width * left
            term_width = max(3.0, width * (right - left))
            item = {
                "term": term,
                "x": int(round(term_x)) - 1,
                "y": int(round(y)) - 1,
                "w": int(round(term_width)) + 2,
                "h": int(round(height)) + 2,
            }
            signature = (item["term"], item["x"], item["y"], item["w"], item["h"])
            if signature not in seen:
                seen.add(signature)
                highlights.append(item)
    return highlights


def scan_region(x, y, width, height):
    if width < 20 or height < 20:
        raise ValueError("capture rectangle is too small")
    started = time.time()
    with _scan_lock:
        image = ImageGrab.grab(
            bbox=(x, y, x + width, y + height),
            all_screens=True,
        )
        scale = min(
            1.0,
            MAX_OCR_WIDTH / float(width),
            MAX_OCR_HEIGHT / float(height),
        )
        if scale < 1.0:
            resized = image.resize(
                (max(1, int(round(width * scale))), max(1, int(round(height * scale)))),
                Image.Resampling.LANCZOS,
            )
        else:
            resized = image
        resized.save(SCAN_IMAGE)
        result = get_ocr().predict(SCAN_IMAGE)

    page = result[0] if isinstance(result, list) else result
    raw_texts = list(page.get("rec_texts", []))
    raw_boxes = list(page.get("rec_boxes", []))
    raw_scores = list(page.get("rec_scores", []))

    texts = []
    boxes = []
    for index, text in enumerate(raw_texts):
        score = float(raw_scores[index]) if index < len(raw_scores) else 1.0
        if text and text.strip() and score >= 0.72 and index < len(raw_boxes):
            texts.append(text.strip())
            box = raw_boxes[index]
            if scale < 1.0:
                if hasattr(box, "tolist"):
                    box = box.tolist()
                if isinstance(box[0], (list, tuple)):
                    box = [[point[0] / scale, point[1] / scale] for point in box]
                else:
                    box = [coordinate / scale for coordinate in box]
            boxes.append(box)

    full_text = "".join(texts)
    if full_text:
        try:
            analysis = post_json(ANALYZE_URL, {"text": full_text})
        except Exception as error:
            # Highlighting must remain useful even if the optional analysis
            # service is restarting. Reuse the exact local rules from server.py.
            from server import local_analyze
            analysis = local_analyze(full_text)
            log("Analyzer unavailable; used local rules: %s" % error)
    else:
        analysis = {"entities": []}
    highlights = match_entities(analysis.get("entities", []), texts, boxes)
    duration = time.time() - started
    log(
        "Scan %dx%d at %.2fx: %d OCR blocks, %d highlights, %.1fs"
        % (width, height, scale, len(texts), len(highlights), duration)
    )
    return {
        "ok": True,
        "text_blocks": len(texts),
        "highlights": highlights,
        "duration_ms": int(round(duration * 1000)),
        "analysis_mode": analysis.get("analysis_mode", "unknown"),
    }


class Handler(BaseHTTPRequestHandler):
    def send_json(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self.send_json({"ok": True, "ocr_loaded": _ocr is not None})
        else:
            self.send_json({"error": "not found"}, 404)

    def do_POST(self):
        if self.path != "/scan":
            self.send_json({"error": "not found"}, 404)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            result = scan_region(
                int(payload["x"]),
                int(payload["y"]),
                int(payload["w"]),
                int(payload["h"]),
            )
            self.send_json(result)
        except Exception as error:
            log("Scan failed: %s" % error)
            self.send_json({"ok": False, "error": str(error)}, 500)

    def log_message(self, _format, *_args):
        return


if __name__ == "__main__":
    log("OCR service listening on http://127.0.0.1:%d" % PORT)
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
