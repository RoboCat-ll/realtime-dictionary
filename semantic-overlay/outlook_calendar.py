"""Microsoft default calendar. Delegated login and tokens are session-memory only."""
from datetime import datetime, timezone
import hashlib
import json
import secrets
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from calendar_export import export_calendar

GRAPH = 'https://graph.microsoft.com/v1.0/'
AUTH = 'https://login.microsoftonline.com/common/oauth2/v2.0/'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request_json(url, payload=None, token=None, form=False):
    headers = {'Accept': 'application/json'}
    data = None
    if payload is not None:
        data = (urllib.parse.urlencode(payload) if form else json.dumps(payload)).encode('utf-8')
        headers['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
    if token:
        headers['Authorization'] = 'Bearer ' + token
        headers['Prefer'] = 'outlook.timezone="UTC"'
    try:
        with urllib.request.build_opener(NoRedirect).open(
                urllib.request.Request(url, data=data, headers=headers), timeout=6) as response:
            return json.loads(response.read(2 * 1024 * 1024))
    except urllib.error.HTTPError as error:
        # Never expose response bodies, tokens, calendar titles, or OAuth internals in logs.
        if not token:
            try:
                code = json.loads(error.read(65536)).get('error')
                if code in ('authorization_pending', 'slow_down', 'authorization_declined',
                            'expired_token', 'invalid_grant'):
                    return {'oauth_error': code}
            except (ValueError, AttributeError):
                pass
        if error.code == 401:
            raise ValueError('微软登录已失效，请重新连接日历。') from None
        if error.code == 403:
            raise ValueError('微软拒绝日历访问，请检查 Calendars.ReadWrite 授权或联系组织管理员。') from None
        raise ValueError('微软服务未完成请求（HTTP %d），请检查应用注册、账户权限或稍后重试。' % error.code) from None
    except (OSError, ValueError):
        raise ValueError('微软服务连接失败或返回无效数据；若正在创建日程，结果可能未知，请重试同一份日程。') from None


def draft_event(payload):
    checked = export_calendar(dict(payload, confirmed=True))
    # Use the already validated, timezone-normalized ICS values.
    fields = dict(line.split(':', 1) for line in checked['ics'].splitlines()
                  if line.startswith(('DTSTART:', 'DTEND:')))
    stamp = lambda value: datetime.strptime(value, '%Y%m%dT%H%M%SZ').replace(tzinfo=timezone.utc)
    start, end = stamp(fields['DTSTART']), stamp(fields['DTEND'])
    return {'subject': payload['title'].strip(),
            'start': {'dateTime': start.strftime('%Y-%m-%dT%H:%M:%S'), 'timeZone': 'UTC'},
            'end': {'dateTime': end.strftime('%Y-%m-%dT%H:%M:%S'), 'timeZone': 'UTC'},
            'transactionId': str(uuid.uuid5(uuid.NAMESPACE_URL, checked['uid']))}, start, end


class OutlookCalendar:
    def __init__(self, transport=request_json):
        self.transport = transport
        self.lock = threading.RLock()
        self.client_id = ''
        self.token = ''
        self.refresh = ''
        self.expires = 0
        self.pending = None
        self.tickets = {}

    def _token_result(self, result):
        if not isinstance(result.get('access_token'), str) or not result['access_token']:
            raise ValueError('微软未返回有效授权，请重新连接。')
        self.token = result['access_token']
        self.refresh = result.get('refresh_token', self.refresh)
        self.expires = time.monotonic() + int(result.get('expires_in', 3600)) - 60

    def _graph(self, path, payload=None):
        if not self.token:
            raise ValueError('请先连接并登录微软日历。')
        if time.monotonic() >= self.expires:
            if not self.refresh:
                raise ValueError('微软登录已过期，请重新连接。')
            result = self.transport(AUTH + 'token', {'client_id': self.client_id,
                'grant_type': 'refresh_token', 'refresh_token': self.refresh}, form=True)
            self._token_result(result)
        # Pagination URLs are untrusted: never forward the bearer token off-host.
        url = GRAPH + path if not path.startswith('https://') else path
        if not url.startswith(GRAPH) or urllib.parse.urlsplit(url).netloc != 'graph.microsoft.com':
            raise ValueError('微软返回了不受支持的分页地址，检查未完成。')
        return self.transport(url, payload, token=self.token)

    def _inspect(self, payload):
        event, start, end = draft_event(payload)
        calendar = self._graph('me/calendar?$select=id,name,owner')
        if not isinstance(calendar.get('id'), str) or not calendar['id']:
            raise ValueError('无法确认默认日历，检查未完成。')
        query = urllib.parse.urlencode({'startDateTime': start.isoformat(), 'endDateTime': end.isoformat(),
            '$top': '100', '$select': 'id,subject,start,end,isCancelled,showAs,webLink'})
        url = 'me/calendars/' + urllib.parse.quote(calendar['id'], safe='') + '/calendarView?' + query
        conflicts, duplicate, signature = [], None, []
        for page in range(5):
            result = self._graph(url)
            if not isinstance(result.get('value'), list):
                raise ValueError('微软日历响应不完整，无法确认是否冲突。')
            for existing in result['value']:
                if existing.get('isCancelled'):
                    continue
                try:
                    def parse(field):
                        value = existing[field]
                        if value.get('timeZone') not in ('UTC', 'Etc/UTC'):
                            raise ValueError()
                        return datetime.fromisoformat(value['dateTime'].replace('Z', '+00:00')).replace(tzinfo=timezone.utc)
                    a, b = parse('start'), parse('end')
                    if not existing.get('id') or b < a:
                        raise ValueError()
                except (KeyError, TypeError, ValueError):
                    raise ValueError('日历包含无法可靠解析的时间，检查未完成。') from None
                if a >= end or b <= start:
                    continue
                if existing.get('subject') == event['subject'] and a == start and b == end:
                    duplicate = existing
                if existing.get('showAs') == 'free':
                    continue
                conflicts.append(existing.get('subject') or '未命名安排')
                signature.append([existing['id'], existing.get('subject'), a.isoformat(), b.isoformat()])
            url = result.get('@odata.nextLink')
            if not url:
                break
        else:
            raise ValueError('该时段日程过多，未能完成所有分页检查；请缩小时间范围。')
        fingerprint = hashlib.sha256(json.dumps(sorted(signature), ensure_ascii=False).encode()).hexdigest()
        return event, conflicts, duplicate, fingerprint, calendar

    def handle(self, payload):
        if not isinstance(payload, dict):
            raise ValueError('日历请求必须为对象。')
        with self.lock:
            op = payload.get('operation')
            if op == 'outlook_disconnect':
                self.token = self.refresh = self.client_id = ''
                self.pending = None
                self.tickets.clear()
                return {'ok': True, 'message': '已断开本次日历连接。'}
            if op == 'outlook_login':
                try:
                    client_id = str(uuid.UUID(str(payload.get('client_id', ''))))
                except ValueError:
                    raise ValueError('请填写你注册的微软应用程序（客户端）ID，格式为 GUID。') from None
                result = self.transport(AUTH + 'devicecode', {'client_id': client_id,
                    'scope': 'https://graph.microsoft.com/Calendars.ReadWrite offline_access'}, form=True)
                if not all(result.get(k) for k in ('device_code', 'user_code', 'expires_in')):
                    raise ValueError('无法启动微软登录，请检查应用注册中的公共客户端设置。')
                self.client_id, self.token, self.refresh = client_id, '', ''
                self.tickets.clear()
                self.pending = dict(result, deadline=time.monotonic() + int(result['expires_in']),
                                    next_poll=time.monotonic() + max(5, int(result.get('interval', 5))))
                return {'ok': True, 'state': 'pending', 'user_code': result['user_code'],
                        'message': '在微软登录页面输入此代码：' + result['user_code']}
            if op == 'outlook_poll':
                if not self.pending:
                    raise ValueError('请先点击连接微软日历。')
                pending = self.pending
                if time.monotonic() >= pending['deadline']:
                    self.pending = None
                    raise ValueError('登录代码已过期，请重新连接。')
                if time.monotonic() < pending['next_poll']:
                    return {'ok': True, 'state': 'pending', 'message': '等待微软登录授权…'}
                result = self.transport(AUTH + 'token', {'client_id': self.client_id,
                    'grant_type': 'urn:ietf:params:oauth:grant-type:device_code',
                    'device_code': pending['device_code']}, form=True)
                if result.get('oauth_error') in ('authorization_pending', 'slow_down'):
                    if result['oauth_error'] == 'slow_down':
                        pending['interval'] = int(pending.get('interval', 5)) + 5
                    pending['next_poll'] = time.monotonic() + max(5, int(pending.get('interval', 5)))
                    return {'ok': True, 'state': 'pending', 'message': '等待微软登录授权…'}
                self.pending = None
                self._token_result(result)
                return {'ok': True, 'state': 'connected', 'message': '微软授权已连接；检查时将读取此账户的默认日历。'}
            if op == 'outlook_check':
                event, conflicts, duplicate, fingerprint, calendar = self._inspect(payload)
                now = time.monotonic()
                self.tickets = {k: v for k, v in self.tickets.items() if v['expires'] > now}
                if len(self.tickets) >= 64:
                    self.tickets.pop(next(iter(self.tickets)))
                ticket = secrets.token_urlsafe(24)
                self.tickets[ticket] = {'event': event, 'fingerprint': fingerprint,
                                        'calendar_id': calendar['id'], 'expires': now + 300}
                identity = (calendar.get('owner') or {}).get('address', '')
                return {'ok': True, 'state': 'duplicate' if duplicate else 'conflict' if conflicts else 'clear',
                        'check_token': ticket, 'conflicts': conflicts,
                        'message': '日历：' + str(calendar.get('name', '默认日历')) + ' ' + identity + '\n' +
                                   ('默认日历已存在相同日程，请勿重复创建。' if duplicate else
                                    '已检查默认日历：有时间冲突，确认创建将仍然保留冲突。' if conflicts else
                                    '已检查默认日历：此时段未发现冲突。')}
            if op == 'outlook_create':
                if payload.get('confirmed') is not True:
                    raise ValueError('请先核对日程并明确确认创建。')
                event, _, _ = draft_event(payload)
                ticket = self.tickets.get(payload.get('check_token', ''))
                if not ticket or ticket['expires'] <= time.monotonic() or ticket['event'] != event:
                    raise ValueError('日程已修改或检查已过期，请重新检查后确认。')
                event, conflicts, duplicate, fingerprint, calendar = self._inspect(payload)
                if calendar['id'] != ticket['calendar_id']:
                    raise ValueError('默认日历已变化，请重新检查后确认。')
                if duplicate:
                    return {'ok': True, 'state': 'duplicate', 'event_id': duplicate['id'],
                            'web_url': duplicate.get('webLink', ''), 'message': '默认日历已有相同日程，未重复创建。'}
                if fingerprint != ticket['fingerprint']:
                    raise ValueError('日历安排已变化，请重新检查并确认新的冲突情况。')
                result = self._graph('me/calendars/' + urllib.parse.quote(calendar['id'], safe='') + '/events', event)
                if not isinstance(result.get('id'), str) or not result['id']:
                    raise ValueError('未收到事件 ID，创建结果未知；请重试同一日程，不要另建副本。')
                return {'ok': True, 'state': 'created', 'event_id': result['id'],
                        'web_url': result.get('webLink', ''), 'message': '已写入微软默认日历；未发送会议邀请。'}
            raise ValueError('不支持的微软日历操作。')


outlook = OutlookCalendar()
