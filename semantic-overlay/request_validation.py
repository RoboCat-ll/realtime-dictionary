"""Bounded, unambiguous JSON request parsing for the loopback service."""

import json
import re
import socket

MAX_BODY_BYTES = 2 * 1024 * 1024


class RequestInputError(ValueError):
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status


def read_json_object(headers, stream):
    lengths = headers.get_all("Content-Length", [])
    if len(lengths) != 1 or headers.get("Transfer-Encoding"):
        raise RequestInputError("请求必须提供唯一的 Content-Length，不支持分块传输")
    value = lengths[0].strip()
    if not re.fullmatch(r"[0-9]{1,10}", value):
        raise RequestInputError("Content-Length 无效")
    length = int(value)
    if length < 1:
        raise RequestInputError("请求体不能为空")
    if length > MAX_BODY_BYTES:
        raise RequestInputError("请求体超过 2 MB 限制", 413)
    try:
        raw = stream.read(length)
    except (socket.timeout, TimeoutError):
        raise RequestInputError("读取请求体超时", 408) from None
    if len(raw) != length:
        raise RequestInputError("请求体不完整")
    try:
        result = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise RequestInputError("请求体不是合法 UTF-8 JSON") from None
    if not isinstance(result, dict):
        raise RequestInputError("请求体必须是 JSON 对象")
    return result
