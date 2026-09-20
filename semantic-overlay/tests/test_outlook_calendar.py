import unittest
from unittest.mock import patch
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from outlook_calendar import OutlookCalendar, draft_event, GRAPH


DRAFT = {'title': 'OneAPI bootcamp', 'start': '2026-09-20T14:00',
         'end': '2026-09-20T15:00', 'utc_offset': '+08:00'}


class OutlookTests(unittest.TestCase):
    def setUp(self):
        self.events = []
        self.writes = []
        self.calendar_id = 'calendar-a'
        self.next_link = None
        def transport(url, payload=None, token=None, form=False):
            if 'me/calendar?' in url:
                return {'id': self.calendar_id, 'name': 'Calendar', 'owner': {'address': 'fixture@example.invalid'}}
            if '/calendarView?' in url:
                result = {'value': self.events}
                if self.next_link:
                    result['@odata.nextLink'] = self.next_link
                return result
            if url.endswith('/events'):
                self.writes.append(payload)
                return {'id': 'created-event', 'webLink': 'https://outlook.live.com/calendar/fixture'}
            raise AssertionError(url)
        self.api = OutlookCalendar(transport)
        self.api.token = 'fixture-token'
        self.api.expires = float('inf')

    def check(self):
        return self.api.handle(dict(DRAFT, operation='outlook_check'))

    def create(self, ticket, **overrides):
        data = dict(DRAFT, operation='outlook_create', confirmed=True, check_token=ticket)
        data.update(overrides)
        return self.api.handle(data)

    def fixture_event(self, subject='Other meeting'):
        event, _, _ = draft_event(DRAFT)
        return dict(event, id='existing', subject=subject, showAs='busy')

    def test_check_is_read_only_and_create_returns_real_id(self):
        checked = self.check()
        self.assertEqual(checked['state'], 'clear')
        self.assertIn('fixture@example.invalid', checked['message'])
        self.assertFalse(self.writes)
        result = self.create(checked['check_token'])
        self.assertEqual(result['event_id'], 'created-event')
        self.assertNotIn('attendees', self.writes[0])
        self.assertEqual(self.writes[0]['start']['dateTime'], '2026-09-20T06:00:00')

    def test_confirmation_and_unchanged_draft_required(self):
        ticket = self.check()['check_token']
        for changes in ({'confirmed': 'true'}, {'title': 'changed'}, {'check_token': 'invalid'}):
            with self.assertRaises(ValueError):
                self.create(ticket, **changes)
        self.assertFalse(self.writes)

    def test_new_conflict_requires_new_review(self):
        ticket = self.check()['check_token']
        self.events = [self.fixture_event()]
        with self.assertRaisesRegex(ValueError, '安排已变化'):
            self.create(ticket)
        self.assertFalse(self.writes)
        new_check = self.check()
        self.assertEqual(new_check['state'], 'conflict')
        self.assertEqual(self.create(new_check['check_token'])['state'], 'created')

    def test_duplicate_never_posts(self):
        ticket = self.check()['check_token']
        self.events = [self.fixture_event(DRAFT['title'])]
        self.assertEqual(self.create(ticket)['state'], 'duplicate')
        self.assertFalse(self.writes)

    def test_changed_calendar_requires_review(self):
        ticket = self.check()['check_token']
        self.calendar_id = 'calendar-b'
        with self.assertRaisesRegex(ValueError, '默认日历已变化'):
            self.create(ticket)
        self.assertFalse(self.writes)

    def test_pagination_never_leaks_token_or_claims_clear(self):
        self.next_link = 'https://example.invalid/steal'
        with self.assertRaisesRegex(ValueError, '分页地址'):
            self.check()

    def test_retry_transaction_id_is_stable(self):
        ticket = self.check()['check_token']
        self.create(ticket)
        self.create(ticket)
        self.assertEqual(self.writes[0]['transactionId'], self.writes[1]['transactionId'])

    def test_login_disconnect_invalidates_tickets(self):
        ticket = self.check()['check_token']
        self.api.handle({'operation': 'outlook_disconnect'})
        self.assertFalse(self.api.token)
        self.assertFalse(self.api.tickets)
        with self.assertRaises(ValueError):
            self.create(ticket)

    def test_device_poll_rate_and_token_not_returned(self):
        calls = []
        def oauth(url, payload=None, **kwargs):
            calls.append(url)
            if url.endswith('devicecode'):
                return {'device_code': 'secret-device-code', 'user_code': 'USERCODE',
                        'expires_in': 900, 'interval': 5}
            return {'access_token': 'secret-access-token', 'expires_in': 3600}
        api = OutlookCalendar(oauth)
        response = api.handle({'operation': 'outlook_login', 'client_id': '12345678-1234-1234-1234-123456789abc'})
        self.assertNotIn('secret-device-code', str(response))
        self.assertEqual(api.handle({'operation': 'outlook_poll'})['state'], 'pending')
        self.assertEqual(len(calls), 1)
        api.pending['next_poll'] = 0
        response = api.handle({'operation': 'outlook_poll'})
        self.assertEqual(response['state'], 'connected')
        self.assertNotIn('secret-access-token', str(response))

    def test_http_calendar_requires_auth_before_provider_call(self):
        import server
        from threading import Thread
        from urllib.request import Request, urlopen
        from urllib.error import HTTPError
        http = server.ThreadingHTTPServer(('127.0.0.1', 0), server.Handler)
        thread = Thread(target=http.serve_forever, daemon=True)
        thread.start()
        try:
            with patch.object(server, 'PORT', http.server_address[1]), patch.object(server.outlook, 'handle') as handler:
                url = 'http://127.0.0.1:%d/calendar/outlook' % http.server_address[1]
                body = json.dumps(dict(DRAFT, operation='outlook_check')).encode()
                with self.assertRaises(HTTPError) as denied:
                    urlopen(Request(url, data=body), timeout=3)
                self.assertEqual(denied.exception.code, 403)
                handler.assert_not_called()
                handler.return_value = {'ok': True, 'state': 'clear'}
                with urlopen(Request(url, data=body, headers={'X-RealtimeDictionary-Token': server.TOKEN}), timeout=3) as response:
                    self.assertEqual(json.load(response)['state'], 'clear')
                handler.assert_called_once()
        finally:
            http.shutdown()
            http.server_close()
            thread.join()


if __name__ == '__main__':
    unittest.main()
