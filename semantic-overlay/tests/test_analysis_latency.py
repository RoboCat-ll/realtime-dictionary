import json
import os
import threading
import time
import unittest
from unittest import mock

import server


class AnalysisLatencyTests(unittest.TestCase):
    def setUp(self):
        server.ANALYZE_CACHE.clear()
        server.MODEL_ANALYSIS_CALLS.clear()
        server.TERM_SELECTIONS.clear()

    def test_analysis_override_does_not_replace_lookup_model(self):
        response = mock.MagicMock()
        response.__enter__.return_value.read.return_value = json.dumps({
            'choices': [{'message': {'content': '{"entities":[],"actions":[]}'}}]
        }).encode()
        with mock.patch.object(server, 'API_KEY', 'fixture'), \
             mock.patch.object(server, 'MODEL', 'explanation-fixture'), \
             mock.patch.object(server, 'ANALYSIS_MODEL', 'analysis-fixture'), \
             mock.patch.object(server, 'provider_urlopen', return_value=response) as opened:
            server.analyze('RAG 用于检索', context_id='latency-test')
            self.assertEqual('analysis-fixture', json.loads(opened.call_args.args[0].data)['model'])
            server.call_llm([{'role': 'user', 'content': '解释'}])
            self.assertEqual('explanation-fixture', json.loads(opened.call_args.args[0].data)['model'])

    def test_slow_model_returns_local_result_and_late_reply_cannot_replace_cache(self):
        release = threading.Event()
        finished = threading.Event()

        def slow(*args, **kwargs):
            release.wait(2)
            finished.set()
            return '{"entities":[],"actions":[]}'

        with mock.patch.object(server, 'API_KEY', 'fixture'), \
             mock.patch.object(server, 'ANALYSIS_TIMEOUT_SECONDS', 0.05), \
             mock.patch.object(server, 'call_llm', side_effect=slow), \
             mock.patch.object(server, 'log'):
            started = time.monotonic()
            try:
                result = server.analyze('RAG 用于检索', context_id='slow-fixture')
                self.assertLess(time.monotonic() - started, 1)
                self.assertEqual('local_fallback', result['analysis_mode'])
                self.assertIn('RAG', [item['text'] for item in result['entities']])
            finally:
                release.set()
                self.assertTrue(finished.wait(1))
            cached = server.analyze('RAG 用于检索', context_id='slow-fixture')
            self.assertEqual('local_fallback', cached['analysis_mode'])

    def test_timeout_setting_is_bounded_and_tolerates_invalid_values(self):
        for raw, expected in [('2', 2), ('nan', 3), ('inf', 3), ('0', 3), ('bad', 3), ('20', 3)]:
            with self.subTest(raw=raw), mock.patch.dict(os.environ, {
                'REALTIME_DICTIONARY_ANALYSIS_TIMEOUT_SECONDS': raw
            }):
                self.assertEqual(expected, server.analysis_timeout_seconds())

    def test_model_switch_separates_analysis_cache(self):
        with mock.patch.object(server, 'ANALYSIS_MODEL', 'first'):
            first = server.analysis_cache_key('RAG', 'standard')
        with mock.patch.object(server, 'ANALYSIS_MODEL', 'second'):
            self.assertNotEqual(first, server.analysis_cache_key('RAG', 'standard'))

    def test_jev_filters_candidates_and_preserves_actions(self):
        text = 'RAG 与 ACID，明天下午14:00开会'
        answers = {'candidate_0': {'type': 'noul', 'noul': 0.91}}
        response = mock.MagicMock()
        response.__enter__.return_value.read.return_value = json.dumps({
            'model': 'jev-1.13.0', 'answers': answers
        }).encode()
        with mock.patch.object(server, 'TYPESAFE_API_KEY', 'fixture'), \
             mock.patch.object(server.urllib.request, 'urlopen', return_value=response) as opened:
            result = server.analyze(text, context_id='jev-test')
        self.assertEqual('jev', result['analysis_mode'])
        self.assertIn('RAG', [item['text'] for item in result['entities']])
        self.assertTrue(result['actions'])
        request = opened.call_args.args[0]
        body = json.loads(request.data)
        self.assertEqual('jev-latest', body['model'])
        self.assertTrue(body['questions'])
        self.assertTrue(request.get_header('Authorization').startswith('Bearer '))

    def test_jev_low_probability_returns_no_entities(self):
        response = mock.MagicMock()
        response.__enter__.return_value.read.return_value = json.dumps({
            'answers': {f'candidate_{i}': {'type': 'noul', 'noul': 0.2}
                        for i in range(2)}
        }).encode()
        with mock.patch.object(server, 'TYPESAFE_API_KEY', 'fixture'), \
             mock.patch.object(server.urllib.request, 'urlopen', return_value=response):
            result = server.analyze('ACID 用于检索', context_id='jev-low')
        self.assertEqual([], result['entities'])

    def test_jev_model_switch_separates_cache(self):
        with mock.patch.object(server, 'TYPESAFE_API_KEY', 'fixture'), \
             mock.patch.object(server, 'TYPESAFE_MODEL', 'jev-first'):
            first = server.analysis_cache_key('RAG', 'standard')
        with mock.patch.object(server, 'TYPESAFE_API_KEY', 'fixture'), \
             mock.patch.object(server, 'TYPESAFE_MODEL', 'jev-second'):
            self.assertNotEqual(first, server.analysis_cache_key('RAG', 'standard'))

    def test_jev_failure_does_not_start_second_provider_wait(self):
        for failure in (TimeoutError(), ValueError()):
            server.ANALYZE_CACHE.clear()
            with mock.patch.object(server, 'TYPESAFE_API_KEY', 'fixture'), \
                 mock.patch.object(server, 'analyze_with_jev', side_effect=failure), \
                 mock.patch.object(server, 'call_llm') as other, \
                 mock.patch.object(server, 'log'):
                result = server.analyze('RAG', context_id='jev-failure')
            self.assertEqual('local_fallback', result['analysis_mode'])
            other.assert_not_called()

    def test_jev_late_response_cannot_change_selection(self):
        release = threading.Event()
        finished = threading.Event()
        def slow(*args, **kwargs):
            release.wait(2)
            finished.set()
            raise TimeoutError()
        with mock.patch.object(server, 'TYPESAFE_API_KEY', 'fixture'), \
             mock.patch.object(server, 'ANALYSIS_TIMEOUT_SECONDS', 0.05), \
             mock.patch.object(server.urllib.request, 'urlopen', side_effect=slow), \
             mock.patch.object(server, 'log'):
            started = time.monotonic()
            try:
                result = server.analyze('ACID', context_id='jev-slow')
                self.assertLess(time.monotonic()-started, 1)
                self.assertEqual('local_fallback', result['analysis_mode'])
            finally:
                release.set()
                self.assertTrue(finished.wait(1))
            self.assertEqual('local_fallback', server.analyze('ACID', context_id='jev-slow')['analysis_mode'])
