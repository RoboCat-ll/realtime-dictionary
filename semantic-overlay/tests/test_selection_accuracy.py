import json
import unittest

import server


class SelectionAccuracyTests(unittest.TestCase):
    def test_ocr_does_not_swallow_words_next_to_a_complete_known_term(self):
        for text in ('GitHub is useful', 'a GitLab project', 'bootcamp is tomorrow',
                     'we use OAuth for login', 'DeepSeek can explain this'):
            with self.subTest(text=text):
                self.assertEqual((text, False), server.repair_selection_ocr_locally(text))

    def test_short_ordinary_words_and_numeric_identifiers_remain_unchanged(self):
        for text in ('lord of the rings', 'open the door', 'oneAPI2 是内部编号',
                     'OAuth3 是测试标签', 'gitlab_prod', 'https://github.com/openai/project',
                     'contact github@example.com', 'docs/gitlab/config.py'):
            with self.subTest(text=text):
                self.assertEqual((text, False), server.repair_selection_ocr_locally(text))

    def test_real_split_tokens_are_still_repaired(self):
        for original, expected in (('使用 one a pi 进行调用', '使用 oneAPI 进行调用'),
                                   ('b。。tcamp 讨论会', 'bootcamp 讨论会'),
                                   ('deep seek 模型', 'DeepSeek 模型'),
                                   ('g RPC 协议', 'gRPC 协议')):
            with self.subTest(text=original):
                self.assertEqual((expected, True), server.repair_selection_ocr_locally(original))

    def test_model_ocr_correction_cannot_reverse_intent_with_a_small_edit(self):
        for original, corrected in (
            ('我们明天下午三点开会讨论项目方案', '我们明天下午三点不开会讨论项目方案'),
            ('会议安排在明天下午三点请大家参加', '会议取消安排在明天下午三点请大家参加'),
            ('we will attend the meeting tomorrow', 'we will not attend the meeting tomorrow'),
            ('会议安排在明天下午三点请大家参加', '会议延期安排在明天下午三点请大家参加'),
            ('we will attend this scheduled team meeting tomorrow',
             "we won't attend this scheduled team meeting tomorrow"),
        ):
            with self.subTest(text=original):
                self.assertEqual((original, False), server.accept_selection_correction(original, corrected))
        unchanged = '明天不用参加 bootcamp 讨论会'
        self.assertEqual((unchanged, False), server.accept_selection_correction(unchanged, unchanged))

    def parsed(self, passage, terms):
        return server.extract_selection_json(json.dumps({
            'explanation': '说明这些技术概念。',
            'terms': [{'text': term, 'explanation': term + '的说明'} for term in terms]
        }, ensure_ascii=False), passage)

    def test_nested_only_terms_yield_to_the_complete_concept(self):
        parsed = self.parsed('我们使用卷积神经网络识别图片。', ['神经网络', '卷积神经网络'])
        self.assertEqual(['卷积神经网络'], [term['text'] for term in parsed['terms']])

    def test_short_term_with_independent_occurrence_is_retained(self):
        parsed = self.parsed('卷积神经网络是一类神经网络。', ['神经网络', '卷积神经网络'])
        self.assertEqual({'神经网络', '卷积神经网络'}, {term['text'] for term in parsed['terms']})

    def test_case_and_punctuation_are_source_exact_without_truncating_identifiers(self):
        parsed = self.parsed('用 Bootcamp 集训，不是 mybootcamp 项目。', ['bootcamp', 'camp'])
        self.assertEqual(['Bootcamp'], [term['text'] for term in parsed['terms']])

    def test_local_merge_does_not_reintroduce_a_nested_only_term(self):
        result = server.merge_selection_terms(
            [{'text': '卷积神经网络', 'explanation': '完整概念'}],
            [{'text': '神经网络', 'explanation': '较泛的概念'}],
            '这里采用卷积神经网络识别图片')
        self.assertEqual(['卷积神经网络'], [term['text'] for term in result])

    def test_missing_annotation_uses_available_context_guarded_local_definition(self):
        model = [{'text': 'GitHub', 'explanation': ''}]
        local = [{'text': 'github', 'explanation': '用于托管代码并协作的平台。'}]
        result = server.merge_selection_terms(model, local, '代码上传 GitHub')
        self.assertEqual([{'text': 'GitHub', 'explanation': local[0]['explanation']}], result)
        self.assertEqual('', model[0]['explanation'])
        model[0]['explanation'] = '这里指我们存放代码的协作平台。'
        result = server.merge_selection_terms(model, local, '代码上传 GitHub')
        self.assertEqual(model[0]['explanation'], result[0]['explanation'])
