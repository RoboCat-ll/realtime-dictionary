import unittest
from datetime import datetime
from unittest.mock import patch
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from calendar_export import export_calendar, check_calendar, clarify_calendar


class CalendarExportTests(unittest.TestCase):
    def test_utc_offset_does_not_replace_meeting_clock(self):
        result = clarify_calendar({'time_text': '9月3号下午14:00',
                                   'supplement': '2027年，持续1小时，UTC+08:00'})
        self.assertEqual('2027-09-03T14:00', result['start'])
        self.assertEqual('2027-09-03T15:00', result['end'])

    def test_past_month_day_defaults_to_next_year(self):
        result = clarify_calendar({'time_text': '9月3号下午14:00', 'supplement': ''},
                                  now=datetime(2026, 9, 30, 12))
        self.assertEqual('2027-09-03T14:00', result['start'])
        self.assertTrue(result['year_inferred'])
        self.assertFalse(any('哪一年' in item for item in result['missing']))

    def test_dotted_meeting_date_defaults_year_but_retains_explicit_day(self):
        result = clarify_calendar({'time_text': '9.30 14:30', 'supplement': ''},
                                  now=datetime(2026, 9, 30, 12))
        self.assertEqual('2026-09-30T14:30', result['start'])
        self.assertFalse(any('哪一年' in item for item in result['missing']))
        self.assertFalse(any('几月几日' in item for item in result['missing']))
        self.assertFalse(any('几点开始' in item for item in result['missing']))
        completed = clarify_calendar({
            'time_text': '9.30 14:30',
            'supplement': '2027年，持续1小时，北京时间'})
        self.assertEqual('2027-09-30T14:30', completed['start'])
        self.assertEqual('2027-09-30T15:30', completed['end'])
        self.assertEqual('+08:00', completed['utc_offset'])
        corrected = clarify_calendar({
            'time_text': '9月3号下午14:00',
            'supplement': '改为2027年9.30 14:30，持续1小时，北京时间'})
        self.assertEqual('2027-09-30T14:30', corrected['start'])

    def test_split_meeting_phrase_defaults_year_without_inventing_length(self):
        phrase = '10月8号导员要开班会，到时候下午5点'
        unresolved = clarify_calendar({'time_text': phrase, 'supplement': ''}, now=datetime(2026, 9, 30))
        self.assertEqual('2026-10-08T17:00', unresolved['start'])
        self.assertEqual('', unresolved['end'])
        self.assertEqual('', unresolved['utc_offset'])
        self.assertFalse(any('几点开始' in item for item in unresolved['missing']))
        completed = clarify_calendar({
            'time_text': phrase,
            'supplement': '2027年，持续1小时，北京时间'})
        self.assertEqual('2027-10-08T17:00', completed['start'])
        self.assertEqual('2027-10-08T18:00', completed['end'])

    def test_today_past_clock_stays_today_and_warns(self):
        result = clarify_calendar({'time_text': '10月8号下午5点开会'}, now=datetime(2026, 10, 8, 18))
        self.assertEqual('2026-10-08T17:00', result['start'])
        self.assertIn('已过去', result['message'])

    def test_future_and_year_boundary(self):
        for phrase, now, expected in [
            ('10月8号17:00', datetime(2026, 9, 30), '2026-10-08T17:00'),
            ('10月8号17:00', datetime(2026, 10, 9), '2027-10-08T17:00'),
            ('1月1号09:00', datetime(2026, 12, 31), '2027-01-01T09:00')]:
            with self.subTest(phrase=phrase, now=now):
                self.assertEqual(expected, clarify_calendar({'time_text': phrase}, now=now)['start'])

    def test_explicit_relative_years_override_future_default(self):
        for prefix, expected in [('2025年', 2025), ('去年', 2025), ('今年', 2026),
                                 ('明年', 2027), ('前年', 2024), ('后年', 2028)]:
            with self.subTest(prefix=prefix):
                result = clarify_calendar({'time_text': prefix + '10月8号17:00'}, now=datetime(2026, 10, 9))
                self.assertEqual(f'{expected}-10-08T17:00', result['start'])
                self.assertFalse(result['year_inferred'])
        corrected = clarify_calendar({'time_text': '2027年10月8号17:00', 'supplement': '改为今年'},
                                     now=datetime(2026, 9, 30))
        self.assertEqual('2026-10-08T17:00', corrected['start'])

    def test_historical_date_does_not_become_future_default(self):
        result = clarify_calendar({'time_text': '以前10月8号下午5点开过班会'}, now=datetime(2026, 9, 30))
        self.assertEqual('', result['start'])
        self.assertIn('历史安排', result['message'])

    def test_leap_day_and_invalid_unqualified_date(self):
        for reference, expected in [(datetime(2026, 1, 1), '2028-02-29T09:00'),
                                    (datetime(2028, 2, 29, 12), '2028-02-29T09:00'),
                                    (datetime(2028, 3, 1), '2032-02-29T09:00')]:
            with self.subTest(reference=reference):
                self.assertEqual(expected, clarify_calendar({'time_text': '2月29号09:00'}, now=reference)['start'])
        for phrase in ['2月30号09:00', '13月1号09:00', '10月8号25:00']:
            with self.subTest(phrase=phrase), self.assertRaises(ValueError):
                clarify_calendar({'time_text': phrase}, now=datetime(2026, 1, 1))

    def test_boss_sentence_explicit_year_duration_zone(self):
        result = clarify_calendar({'time_text': '9月3号下午14:00',
                                   'supplement': '2027年，持续1小时，北京时间'})
        self.assertEqual('2027-09-03T14:00', result['start'])
        self.assertEqual('2027-09-03T15:00', result['end'])
        self.assertEqual('+08:00', result['utc_offset'])
        self.assertEqual([], result['missing'])

    def test_clarification_corrects_date_and_crosses_midnight(self):
        result = clarify_calendar({'time_text': '9月3号下午14:00',
                                   'supplement': '2027年9月4日23:30，持续1.5小时，UTC+08:00'})
        self.assertEqual('2027-09-04T23:30', result['start'])
        self.assertEqual('2027-09-05T01:00', result['end'])

    def test_clarification_rejects_invalid_date(self):
        with self.assertRaises(ValueError):
            clarify_calendar({'time_text': '2月30日14:00',
                              'supplement': '2027年，1小时，北京时间'})

    def test_explicit_timezone_correction_overrides_original(self):
        result = clarify_calendar({'time_text': '2027年9月3日14:00，UTC+00:00',
                                   'supplement': '1小时，北京时间'})
        self.assertEqual('+08:00', result['utc_offset'])

    def test_unclosed_alarm_is_not_misreported_as_available(self):
        source = export_calendar(self.payload())['ics'].replace('END:VEVENT', 'BEGIN:VALARM\r\nEND:VEVENT')
        with self.assertRaises(ValueError):
            check_calendar(self.payload(calendar_ics=source))

    def payload(self, **changes):
        value = dict(title='与 oneAPI 同事讨论 bootcamp', start='2026-09-20T14:00',
                     end='2026-09-20T15:00', utc_offset='+08:00', confirmed=True)
        value.update(changes)
        return value

    def test_china_time_becomes_utc(self):
        result = export_calendar(self.payload())
        self.assertIn('DTSTART:20260920T060000Z\r\n', result['ics'])
        self.assertIn('DTEND:20260920T070000Z\r\n', result['ics'])
        self.assertEqual(1, result['ics'].count('BEGIN:VEVENT'))

    def test_check_reports_duplicate_without_writing(self):
        draft = self.payload(calendar_ics=export_calendar(self.payload())['ics'])
        self.assertEqual('duplicate', check_calendar(draft)['state'])

    def test_overlap_and_adjacent_boundary(self):
        source = export_calendar(self.payload(title='已有会议'))['ics']
        for start, end, expected in [
            ('2026-09-20T14:30', '2026-09-20T15:30', 'conflict'),
            ('2026-09-20T15:00', '2026-09-20T16:00', 'clear'),
        ]:
            self.assertEqual(expected, check_calendar(self.payload(
                start=start, end=end, calendar_ics=source))['state'])

    def test_recurrence_and_local_times_are_incomplete(self):
        source = export_calendar(self.payload())['ics']
        for changed in [source.replace('END:VEVENT', 'RRULE:FREQ=DAILY\r\nEND:VEVENT'),
                        source.replace('DTSTART:', 'DTSTART;TZID=Asia/Shanghai:')]:
            self.assertEqual('incomplete', check_calendar(self.payload(calendar_ics=changed))['state'])

    def test_empty_calendar_and_invalid_calendar(self):
        self.assertEqual('clear', check_calendar(self.payload(
            calendar_ics='BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR'))['state'])
        for source in ['', 'not a calendar', 'BEGIN:VCALENDAR\nBEGIN:VEVENT\nEND:VCALENDAR', 'x' * (1024 * 1024 + 1)]:
            with self.assertRaises(ValueError):
                check_calendar(self.payload(calendar_ics=source))

    def test_check_requires_complete_dates_but_not_confirmation(self):
        source = export_calendar(self.payload())['ics']
        self.assertTrue(check_calendar(self.payload(confirmed=False, calendar_ics=source))['ok'])
        with self.assertRaises(ValueError):
            check_calendar(self.payload(start='9月3号14点', calendar_ics=source))

    def test_cancelled_event_does_not_block_time(self):
        source = export_calendar(self.payload())['ics'].replace('END:VEVENT', 'STATUS:CANCELLED\r\nEND:VEVENT')
        self.assertEqual('clear', check_calendar(self.payload(calendar_ics=source))['state'])

    def test_confirmation_is_strict_and_dates_must_be_complete(self):
        for value in [False, 'true', 1, None]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                export_calendar(self.payload(confirmed=value))
        for date in ['', '9月3日14:00', '2026-02-30T14:00', '2026-09-20T24:00']:
            with self.subTest(date=date), self.assertRaises(ValueError):
                export_calendar(self.payload(start=date))

    def test_end_before_start_and_invalid_offsets_rejected(self):
        for changes in [dict(end='2026-09-20T14:00'), dict(end='2026-09-19T14:00'),
                        dict(utc_offset='+14:30'), dict(utc_offset='+08:99'), dict(utc_offset='')]:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                export_calendar(self.payload(**changes))

    def test_negative_and_half_hour_offsets_cross_day(self):
        result = export_calendar(self.payload(start='2026-09-20T23:00',
                                             end='2026-09-21T00:30', utc_offset='-03:30'))
        self.assertIn('DTSTART:20260921T023000Z', result['ics'])
        self.assertIn('DTEND:20260921T040000Z', result['ics'])

    def test_uid_stable_for_equivalent_event_and_changes_with_edits(self):
        first = export_calendar(self.payload())
        self.assertEqual(first['uid'], export_calendar(self.payload())['uid'])
        equivalent = self.payload(start='2026-09-20T06:00', end='2026-09-20T07:00', utc_offset='+00:00')
        self.assertEqual(first['uid'], export_calendar(equivalent)['uid'])
        self.assertNotEqual(first['uid'], export_calendar(self.payload(title='另一次会议'))['uid'])

    def test_escapes_and_folds_chinese_without_new_properties(self):
        result = export_calendar(self.payload(title=('中文研讨会' * 20) + ',;\\'))
        lines = result['ics'].split('\r\n')
        self.assertTrue(all(len(line.encode('utf-8')) <= 75 for line in lines))
        self.assertIn('\\,\\;\\\\', result['ics'].replace('\r\n ', ''))
        for title in ['会议\r\nBEGIN:VEVENT', '会议\x00']:
            with self.assertRaises(ValueError):
                export_calendar(self.payload(title=title))

    def test_endpoint_token_and_invalid_payload(self):
        import server
        from threading import Thread
        from urllib.request import Request, urlopen
        from urllib.error import HTTPError
        import json
        http = server.ThreadingHTTPServer(('127.0.0.1', 0), server.Handler)
        port = http.server_address[1]
        thread = Thread(target=http.serve_forever, daemon=True)
        thread.start()
        try:
            with patch.object(server, 'PORT', port), patch.object(server, 'log'):
                def request(data, token=None):
                    headers = {'Content-Type': 'application/json'}
                    if token: headers['X-RealtimeDictionary-Token'] = token
                    return urlopen(Request(f'http://127.0.0.1:{port}/calendar/export',
                                           data=json.dumps(data).encode(), headers=headers), timeout=3)
                with self.assertRaises(HTTPError) as caught:
                    request(self.payload())
                self.assertEqual(403, caught.exception.code)
                with self.assertRaises(HTTPError) as invalid:
                    request([], server.TOKEN)
                self.assertEqual(400, invalid.exception.code)
                self.assertIn('JSON', json.load(invalid.exception)['error'])
                with request(self.payload(), server.TOKEN) as response:
                    self.assertTrue(json.load(response)['ok'])
                check_body = self.payload(calendar_ics=export_calendar(self.payload())['ics'])
                for authorized in (False, True):
                    headers = {'Content-Type': 'application/json'}
                    if authorized:
                        headers['X-RealtimeDictionary-Token'] = server.TOKEN
                    req = Request(f'http://127.0.0.1:{port}/calendar/check',
                                  data=json.dumps(check_body).encode(), headers=headers)
                    if authorized:
                        with urlopen(req, timeout=3) as response:
                            self.assertEqual('duplicate', json.load(response)['state'])
                    else:
                        with self.assertRaises(HTTPError) as caught:
                            urlopen(req, timeout=3)
                        self.assertEqual(403, caught.exception.code)
        finally:
            http.shutdown()
            http.server_close()
            thread.join()



if __name__ == '__main__':
    unittest.main()
