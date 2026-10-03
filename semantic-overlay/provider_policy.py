"""Free-only SiliconFlow admission and content-free provider accounting."""
import datetime as dt
import decimal
import json
import os
import re
import threading
import time
import urllib.parse
import urllib.request
import uuid

PRICING_URL = "https://siliconflow.cn/pricing"
_price_lock = threading.Lock()
_prices = {}
_checked_at = 0.0
_ledger_lock = threading.Lock()


class PolicyBlocked(RuntimeError):
    pass


def siliconflow_host(host):
    host = str(host or "").casefold().rstrip(".")
    return any(host == domain or host.endswith("." + domain)
               for domain in ("siliconflow.cn", "siliconflow.com"))


def parse_free_prices(page):
    """Resolve the official page's React records; unknown schema stays blocked."""
    stream = "".join(json.loads(part)[1] for part in re.findall(
        r'self\.__next_f\.push\((\[1,.*?\])\)</script>', page))
    rows = {}
    for line in stream.splitlines():
        key, sep, value = line.partition(":")
        if sep:
            try:
                rows[key] = json.loads(value)
            except (ValueError, TypeError):
                pass

    def resolve(value, depth=0):
        if depth > 12:
            raise ValueError("pricing reference depth")
        if isinstance(value, str) and value.startswith("$"):
            return resolve(rows[value[1:]], depth + 1)
        if isinstance(value, list):
            return [resolve(item, depth + 1) for item in value]
        if isinstance(value, dict):
            return {key: resolve(item, depth + 1) for key, item in value.items()}
        return value

    result = {}
    for row in rows.values():
        if not isinstance(row, dict) or not row.get("modelName"):
            continue
        model = row["modelName"]
        if "deepseek" in model.casefold() or row.get("status") != "normal":
            continue
        try:
            prices = resolve(row["pricing"])
            values = [row["price"]]
            if "inputPrice" in row:
                values.append(row["inputPrice"])
            if not isinstance(prices, list):
                continue
            values.extend(item["price"] for item in prices)
            if not all(decimal.Decimal(str(value)).is_finite() and
                       decimal.Decimal(str(value)) == 0 for value in values):
                continue
            if row.get("subType") == "chat":
                if not {"prompt", "completion"}.issubset(
                        {item.get("specification") for item in prices}):
                    continue
                operation = "/chat/completions"
            elif row.get("subType") == "speech-to-text":
                operation = "/audio/transcriptions"
            else:
                continue
            result[model] = {"operation": operation, "prices": values}
        except (KeyError, ValueError, TypeError, decimal.InvalidOperation):
            continue
    if not result:
        raise ValueError("no verified free models")
    return result


def refresh_free_prices():
    global _prices, _checked_at
    now = time.time()
    with _price_lock:
        # Short process-only cache; never trust an editable disk allowlist.
        if (0 <= now - _checked_at < 900 and _prices and
                dt.date.fromtimestamp(_checked_at) == dt.date.fromtimestamp(now)):
            return _prices
        try:
            request = urllib.request.Request(PRICING_URL,
                headers={"User-Agent": "RealtimeDictionary-price-check"})
            with urllib.request.urlopen(request, timeout=5) as response:
                if urllib.parse.urlsplit(response.geturl()).hostname not in (
                        "siliconflow.cn", "www.siliconflow.cn"):
                    raise ValueError("pricing redirect")
                page = response.read(2 * 1024 * 1024 + 1)
            if len(page) > 2 * 1024 * 1024:
                raise ValueError("pricing page too large")
            verified = parse_free_prices(page.decode("utf-8"))
        except Exception:
            _prices = {}
            _checked_at = 0
            raise PolicyBlocked("无法核实硅基流动当前免费价格，请稍后重试；请求未发送。") from None
        _prices, _checked_at = verified, now
        return _prices


def enforce_free_only(endpoint, model, operation):
    if not siliconflow_host(urllib.parse.urlsplit(endpoint).hostname):
        return
    if "deepseek" in str(model).casefold():
        raise PolicyBlocked("费用规则禁止硅基流动的全部 DeepSeek 模型；请求未发送。")
    record = refresh_free_prices().get(model)
    if record is None or record["operation"] != operation:
        raise PolicyBlocked("该硅基流动模型或接口未核实为免费；请求未发送。")


def pricing_status():
    return {"siliconflow_free_only": True, "source": PRICING_URL,
            "verified_at": dt.datetime.fromtimestamp(_checked_at, dt.timezone.utc).isoformat()
                if _checked_at else None,
            "verification_max_age_seconds": 900}


def ledger_path():
    appdata = os.environ.get("APPDATA")
    if not appdata:
        raise PolicyBlocked("无法定位本机用量记录目录；请求未发送。")
    return os.path.join(appdata, "RealtimeDictionary", "provider-usage",
                        dt.date.today().isoformat() + ".jsonl")


def write_usage(record):
    with _ledger_lock:
        path = ledger_path()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        if os.path.exists(path) and os.path.getsize(path) > 8 * 1024 * 1024:
            raise PolicyBlocked("今日用量记录已达安全上限；请求已停止。")
        with open(path, "a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, ensure_ascii=False) + "\n")


def start_usage(endpoint, model, operation):
    record = {"id": uuid.uuid4().hex, "timestamp": dt.datetime.now().isoformat(),
              "provider": urllib.parse.urlsplit(endpoint).hostname,
              "model": str(model)[:160], "operation": operation,
              "status": "submitted", "usage": None}
    if siliconflow_host(record["provider"]):
        record["price_source"] = PRICING_URL
        record["price_verified_at"] = pricing_status()["verified_at"]
    try:
        write_usage(record)
    except Exception:
        raise PolicyBlocked("用量记录无法写入；请求未发送，请检查磁盘权限或空间。") from None
    return record


def reported_usage(payload):
    usage = payload.get("usage") if isinstance(payload, dict) else None
    if not isinstance(usage, dict):
        return None
    result = {}
    for key in ("prompt_tokens", "completion_tokens", "total_tokens",
                "prompt_cache_hit_tokens", "prompt_cache_miss_tokens"):
        value = usage.get(key)
        if type(value) is int and value >= 0:
            result[key] = value
    details = usage.get("prompt_tokens_details")
    if isinstance(details, dict) and type(details.get("cached_tokens")) is int:
        result["cached_tokens"] = max(0, details["cached_tokens"])
    return result or None


class AccountedResponse:
    def __init__(self, response, record, started):
        self.response, self.record, self.started = response, record, started
        self.finished = False

    def __enter__(self):
        return self

    def finish(self, status, payload=None):
        if self.finished:
            return
        self.finished = True
        self.record.update(status=status, elapsed_ms=round((time.monotonic() - self.started) * 1000),
                           usage=reported_usage(payload))
        write_usage(self.record)

    def read(self, *args):
        try:
            data = self.response.read(*args)
            try:
                payload = json.loads(data)
            except (ValueError, UnicodeDecodeError):
                payload = None
            self.finish("completed", payload)
            return data
        except Exception:
            self.finish("failed_unknown_usage")
            raise

    def __exit__(self, kind, value, traceback):
        try:
            if not self.finished:
                self.finish("failed_unknown_usage" if kind else "unknown_usage")
        finally:
            self.response.close()


def usage_summary():
    records = {}
    path = ledger_path()
    with _ledger_lock:
        if os.path.exists(path):
            if os.path.getsize(path) > 9 * 1024 * 1024:
                raise ValueError("usage file too large")
            with open(path, encoding="utf-8") as stream:
                for line in stream:
                    try:
                        row = json.loads(line)
                        records[row["id"]] = row
                    except (ValueError, KeyError, TypeError):
                        pass
    groups = {}
    for row in records.values():
        key = (row["provider"], row["model"])
        group = groups.setdefault(key, {"provider": key[0], "model": key[1], "requests": 0,
                                       "unknown_usage_requests": 0, "reported_tokens": {}})
        group["requests"] += 1
        usage = row.get("usage")
        if usage is None:
            group["unknown_usage_requests"] += 1
        else:
            for name, value in usage.items():
                group["reported_tokens"][name] = group["reported_tokens"].get(name, 0) + value
    return {"date": dt.date.today().isoformat(), "groups": list(groups.values()),
            "billing_notice": "只统计本机请求及服务商返回用量；缺失用量未知，最终费用以服务商账单为准。"}
