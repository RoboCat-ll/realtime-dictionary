import json
import os
import threading
import time
import unittest
from unittest import mock

import server


class AnalysisLatencyTests(unittest.TestCase):
    def setUp(self):
        self.api_patch = mock.patch.object(server, 'API_KEY', '')
        self.api_patch.start()
        self.addCleanup(self.api_patch.stop)
        # Offline tests must never inherit the user's persisted TypeSafe key.
        self.typesafe_patch = mock.patch.object(server, 'TYPESAFE_API_KEY', '')
        self.typesafe_patch.start()
        self.addCleanup(self.typesafe_patch.stop)
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
        for raw, expected in [('2', 2), ('nan', 10), ('inf', 10), ('0', 10), ('bad', 10), ('20', 10)]:
            with self.subTest(raw=raw), mock.patch.dict(os.environ, {
                'REALTIME_DICTIONARY_ANALYSIS_TIMEOUT_SECONDS': raw
            }):
                self.assertEqual(expected, server.analysis_timeout_seconds())

    def test_model_switch_separates_analysis_cache(self):
        with mock.patch.object(server, 'API_KEY', 'fixture'), mock.patch.object(server, 'ANALYSIS_MODEL', 'first'):
            first = server.analysis_cache_key('RAG', 'standard')
        with mock.patch.object(server, 'API_KEY', 'fixture'), mock.patch.object(server, 'ANALYSIS_MODEL', 'second'):
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

    def test_sentence_discovery_without_candidates_prefers_single_generator(self):
        text = '通过低秩适配降低训练开销。'
        term = '低秩适配'
        reply = json.dumps({'entities': [{'text': term, 'type': 'concept',
                            'start': 2, 'end': 6}], 'actions': []})
        with mock.patch.object(server, 'API_KEY', 'fixture'), \
             mock.patch.object(server, 'TYPESAFE_API_KEY', 'fixture'), \
             mock.patch.object(server, 'extract_candidates', return_value=[]), \
             mock.patch.object(server, 'call_llm_with_deadline', return_value=reply) as generate, \
             mock.patch.object(server, 'analyze_with_jev') as jev:
            result = server.analyze(text, context_id='sentence-fixture')
            self.assertEqual([term], [item['text'] for item in result['entities']])
            self.assertEqual('sentence_concepts', result['analysis_strategy'])
            self.assertEqual(text, generate.call_args.args[0][1]['content'])
            self.assertEqual(3, len(generate.call_args.args[0]))
            server.analyze(text, context_id='sentence-fixture')
            generate.assert_called_once()
            jev.assert_not_called()

    def test_sentence_discovery_rejects_invented_terms_and_fragments(self):
        reply = json.dumps({'entities': [{'text': 'PT', 'start': 1, 'end': 3},
                                        {'text': '不存在的概念'}], 'actions': []})
        with mock.patch.object(server, 'API_KEY', 'fixture'), \
             mock.patch.object(server, 'call_llm_with_deadline', return_value=reply):
            result = server.analyze('GPT 示例', context_id='invalid-sentence')
            self.assertEqual([], result['entities'])

    def test_compact_discovery_locates_repeated_terms_and_retains_local_actions(self):
        text = '背压保护系统，背压限制流量，明天下午14:00开会。'
        reply = json.dumps({'entities': [{'text': '背压'}, {'text': '背压'}]})
        with mock.patch.object(server, 'API_KEY', 'fixture'), \
             mock.patch.object(server, 'call_llm_with_deadline', return_value=reply):
            result = server.analyze(text, context_id='compact-spans')
        self.assertEqual([0, 7], [e['start'] for e in result['entities']])
        self.assertTrue(result['actions'])

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
