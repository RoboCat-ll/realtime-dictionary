"""Confirmed calendar file export. No provider calls, persistence, or logging."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
import re


def _date(value, label):
    if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}", value):
        raise ValueError(label + "需填写完整日期和时间（例如 2026-09-20T14:00）")
    try:
        return datetime.strptime(value, "%Y-%m-%dT%H:%M")
    except ValueError:
        raise ValueError(label + "不是有效日期或时间") from None


def _escape(value):
    return value.replace("\\", "\\\\").replace("\n", "\\n").replace(";", "\\;").replace(",", "\\,")


def _fold(line):
    # RFC 5545: 75 octets, not characters; never split a UTF-8 code point.
    lines, current, size = [], "", 0
    for character in line:
        width = len(character.encode("utf-8"))
        if size + width > 75:
            lines.append(current)
            current, size = " ", 1
        current += character
        size += width
    lines.append(current)
    return "\r\n".join(lines)


def export_calendar(payload):
    if not isinstance(payload, dict) or payload.get("confirmed") is not True:
        raise ValueError("请先核对日程并点击确认导出")
    title = payload.get("title")
    if not isinstance(title, str) or not title.strip() or len(title.strip()) > 120:
        raise ValueError("事项需填写 1～120 个字符")
    title = title.strip()
    if any(ord(ch) < 32 or ord(ch) == 127 for ch in title):
        raise ValueError("事项必须为单行文字")
    start = _date(payload.get("start"), "开始时间")
    end = _date(payload.get("end"), "结束时间")
    if end <= start:
        raise ValueError("结束时间必须晚于开始时间")
    offset = payload.get("utc_offset")
    if not isinstance(offset, str) or not re.fullmatch(r"[+-]\d{2}:\d{2}", offset):
        raise ValueError("请选择或填写 UTC 时差，例如 +08:00")
    hours, minutes = int(offset[1:3]), int(offset[4:6])
    if minutes >= 60 or hours > 14 or (hours == 14 and minutes != 0):
        raise ValueError("UTC 时差超出范围")
    zone = timezone(timedelta(minutes=(hours * 60 + minutes) * (-1 if offset[0] == "-" else 1)))
    try:
        start_utc = start.replace(tzinfo=zone).astimezone(timezone.utc)
        end_utc = end.replace(tzinfo=zone).astimezone(timezone.utc)
    except (OverflowError, ValueError):
        raise ValueError("日期超出支持范围") from None
    stamp = lambda dt: dt.strftime("%Y%m%dT%H%M%SZ")
    canonical = json.dumps([title, stamp(start_utc), stamp(end_utc)], ensure_ascii=False)
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    uid = digest + "@realtime-dictionary.local"
    lines = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Realtime Dictionary//Calendar Export//ZH",
             "CALSCALE:GREGORIAN", "BEGIN:VEVENT", "UID:" + uid,
             "DTSTAMP:" + stamp(datetime.now(timezone.utc)),
             "DTSTART:" + stamp(start_utc), "DTEND:" + stamp(end_utc),
             "SUMMARY:" + _escape(title), "END:VEVENT", "END:VCALENDAR"]
    return {"ok": True, "filename": "schedule-" + digest[:16] + ".ics", "uid": uid,
            "ics": "\r\n".join(_fold(line) for line in lines) + "\r\n",
            "message": "已生成日历文件；请在日历软件中导入并确认。重复导入如何处理取决于日历软件。"}


def check_calendar(payload):
    """Read-only, conservative check against a user-selected ICS snapshot."""
    if not isinstance(payload, dict):
        raise ValueError("日程请求必须为对象")
    draft = dict(payload, confirmed=True)
    event = export_calendar(draft)  # Reuse all date, offset and title validation.
    source = payload.get("calendar_ics")
    if not isinstance(source, str) or not source.strip():
        raise ValueError("请先导入需要检查的 .ics 日历文件")
    if len(source.encode("utf-8")) > 1024 * 1024:
        raise ValueError("日历文件不能超过 1 MiB")
    unfolded = re.sub(r"\r?\n[ \t]", "", source.lstrip("\ufeff"))
    lines = [line.strip() for line in unfolded.splitlines() if line.strip()]
    if not lines or lines[0] != "BEGIN:VCALENDAR" or lines[-1] != "END:VCALENDAR":
        raise ValueError("文件不是完整的 VCALENDAR 日历")
    stack = []
    for line in lines:
        if line.startswith('BEGIN:'):
            name = line[6:]
            if name == 'VEVENT' and stack != ['VCALENDAR']:
                raise ValueError('日历事件嵌套位置无效')
            if name == 'VCALENDAR' and stack:
                raise ValueError('日历不能嵌套')
            stack.append(name)
        elif line.startswith('END:'):
            if not stack or stack.pop() != line[4:]:
                raise ValueError('日历组件结构不完整')
    if stack:
        raise ValueError('日历组件尚未结束')

    def utc(value):
        if not re.fullmatch(r"\d{8}T\d{6}Z", value):
            raise ValueError("unsupported date")
        return datetime.strptime(value, "%Y%m%dT%H%M%SZ")

    def prop(text, key):
        return re.search(r"^" + key + r":(.+)$", text, re.MULTILINE).group(1).strip()

    start = utc(prop(event["ics"], "DTSTART"))
    end = utc(prop(event["ics"], "DTEND"))
    blocks, current = [], None
    for line in lines[1:-1]:
        if line == "BEGIN:VEVENT":
            if current is not None:
                raise ValueError("日历事件结构不完整")
            current = []
        elif line == "END:VEVENT":
            if current is None:
                raise ValueError("日历事件结构不完整")
            blocks.append(current)
            current = None
        elif current is not None:
            current.append(line)
    if current is not None:
        raise ValueError("日历事件尚未结束")
    conflicts, duplicates, unsupported = [], [], 0
    for block in blocks:
        values = {}
        depth = 0
        uncertain = False
        for line in block:
            if line.startswith("BEGIN:"):
                depth += 1
                continue
            if line.startswith("END:"):
                depth -= 1
                continue
            if depth:
                continue
            if ":" not in line:
                uncertain = True
                continue
            key, value = line.split(":", 1)
            base = key.split(";", 1)[0].upper()
            if base in values:
                uncertain = True
            values[base] = value
            if base in ("RRULE", "RDATE", "EXDATE", "RECURRENCE-ID", "DURATION"):
                uncertain = True
            if base in ("DTSTART", "DTEND") and ";" in key:
                uncertain = True
        if values.get("STATUS", "").upper() == "CANCELLED" or values.get("TRANSP", "").upper() == "TRANSPARENT":
            continue
        try:
            existing_start = utc(values.get("DTSTART", ""))
            existing_end = utc(values.get("DTEND", ""))
            if existing_end <= existing_start or uncertain:
                raise ValueError("unsupported event")
        except ValueError:
            unsupported += 1
            continue
        label = values.get("SUMMARY", "未命名日程")[:200]
        label = re.sub(r"\\([,;\\])", r"\1", label).replace(r"\n", " ")
        identical = values.get("UID") == event["uid"] or (
            existing_start == start and existing_end == end and label == payload["title"].strip())
        if identical:
            duplicates.append(label)
        elif existing_start < end and start < existing_end:
            conflicts.append(label)
    if duplicates:
        state, message = "duplicate", "已有相同日程，请勿重复导出。"
    elif unsupported:
        state, message = "incomplete", "检查不完整：部分事件包含重复规则、非 UTC 时间或不支持的格式，请先在原日历核对。"
    elif conflicts:
        state, message = "conflict", "发现时间冲突，请调整时间或核对后明确确认。"
    else:
        state, message = "clear", "导入的日历快照中未发现时间冲突；尚未创建事件。"
    return {"ok": True, "state": state, "message": message,
            "conflicts": conflicts, "duplicates": duplicates,
            "unsupported": unsupported, "checked_events": len(blocks)}


def clarify_calendar(payload):
    """Extract explicitly supplied Chinese schedule details into an editable draft."""
    if not isinstance(payload, dict):
        raise ValueError("补充说明必须为对象")
    original = payload.get('time_text', '')
    supplement = payload.get('supplement', '')
    if not isinstance(original, str) or not isinstance(supplement, str) or len(original) + len(supplement) > 2000:
        raise ValueError("补充说明过长或格式不正确")
    # Prefer user correction over the source phrase, retaining only explicit fields.
    def find(pattern):
        return re.search(pattern, supplement) or re.search(pattern, original)
    year = find(r'(?<!\d)(\d{4})\s*年')
    day = find(r'(?<!\d)(\d{1,2})\s*月\s*(\d{1,2})\s*[日号]')
    # A UTC offset is not a replacement meeting start time.
    clock_pattern = r'(凌晨|早上|上午|中午|下午|晚上)?\s*(\d{1,2})\s*[:：点时]\s*(\d{1,2})?(?:分)?'
    clock_sources = [re.sub(r'UTC\s*[+-]\d{2}:\d{2}', '', value, flags=re.IGNORECASE)
                     for value in (supplement, original)]
    clock = re.search(clock_pattern, clock_sources[0]) or re.search(clock_pattern, clock_sources[1])
    duration = find(r'(?:持续|时长|开|共)?\s*(\d+(?:\.\d+)?)\s*(小时|分钟)')
    result = {'ok': True, 'start': '', 'end': '', 'utc_offset': '', 'missing': []}
    if year and day and clock:
        hour, minute = int(clock[2]), int(clock[3] or 0)
        period = clock[1]
        if period in ('下午', '晚上') and 1 <= hour < 12:
            hour += 12
        elif period in ('凌晨', '上午', '早上') and hour == 12:
            hour = 0
        elif period == '中午' and hour != 12:
            result['missing'].append('请用24小时制明确中午的开始时间')
        if not result['missing']:
            value = '%04d-%02d-%02dT%02d:%02d' % (int(year[1]), int(day[1]), int(day[2]), hour, minute)
            start = _date(value, '开始时间')
            result['start'] = value
            if duration:
                minutes = float(duration[1]) * (60 if duration[2] == '小时' else 1)
                if not minutes.is_integer() or not 0 < minutes <= 7 * 24 * 60:
                    raise ValueError('请提供1分钟至7天的明确会议时长')
                try:
                    result['end'] = (start + timedelta(minutes=minutes)).strftime('%Y-%m-%dT%H:%M')
                except OverflowError:
                    raise ValueError('结束日期超出范围') from None
    if not year:
        result['missing'].append('会议是哪一年？')
    if not day:
        result['missing'].append('会议是几月几日？')
    if not clock:
        result['missing'].append('会议几点开始？请使用24小时制')
    if not duration:
        result['missing'].append('会议持续多久？例如1小时')
    zone_text = supplement if re.search(r'UTC|北京时间|中国时间', supplement, re.IGNORECASE) else original
    zone = re.search(r'UTC\s*([+-]\d{2}:\d{2})', zone_text, re.IGNORECASE)
    if zone:
        # Reuse export validation for the numeric offset.
        export_calendar({'confirmed': True, 'title': '校验', 'start': '2026-01-01T12:00',
                         'end': '2026-01-01T13:00', 'utc_offset': zone[1]})
        result['utc_offset'] = zone[1]
    elif re.search(r'北京时间|中国时间', zone_text):
        result['utc_offset'] = '+08:00'
    else:
        result['missing'].append('使用哪个时区？例如北京时间或UTC+08:00')
    result['message'] = '；'.join(result['missing']) if result['missing'] else '信息已整理完整，请核对后创建提醒。'
    return result
