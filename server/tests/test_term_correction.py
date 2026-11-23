import unittest

from app.term_correction import DEFAULT_LEXICON, KNOWN_MISRECOGNITIONS, correct_terms


class TermCorrectionTests(unittest.TestCase):
    def test_homophone_order(self):
        t, r = correct_terms("我的定单有问题")
        self.assertEqual(t, "我的订单有问题")
        self.assertEqual(r, [{"start": 2, "end": 4, "original": "定单", "replacement": "订单", "kind": "phonetic"}])

    def test_deposit_already_correct(self):
        t, r = correct_terms("扣了押金")
        self.assertEqual(t, "扣了押金")
        self.assertEqual(r, [])

    def test_boss(self):
        t, r = correct_terms("老版说不退")
        self.assertEqual(t, "老板说不退")
        self.assertEqual(r[0]["original"], "老版")

    def test_fuzzy_nasal(self):
        # 投诉 tou su: 头(tou)速(su) 同音
        t, _ = correct_terms("我要头速", ["投诉"])
        self.assertEqual(t, "我要投诉")
        # 前后鼻音: 订单 ding->din 与 丁/定 等
        t, _ = correct_terms("新单", ["订单"])  # xin != ding, 不应改
        self.assertEqual(t, "新单")
        t, _ = correct_terms("鼎单", ["订单"])  # ding ding
        self.assertEqual(t, "订单")

    def test_no_overcorrect_fu_ju_li(self):
        text = "付距离"
        t, r = correct_terms(text, known={})  # 关闭已知映射,仅拼音层不改
        self.assertEqual(t, text)
        self.assertEqual(r, [])

    def test_dian_nao_not_dian_zhang(self):
        text = "电脑挂朋友圈"
        t, r = correct_terms(text)
        self.assertEqual(t, text)
        self.assertEqual(r, [])

    def test_existing_word_untouched_and_single_char(self):
        t, r = correct_terms("老板", ["老板", "板"])
        self.assertEqual((t, r), ("老板", []))
        t, r = correct_terms("版", ["板"])
        self.assertEqual((t, r), ("版", []))

    def test_repeated(self):
        t, r = correct_terms("定单和定单")
        self.assertEqual(t, "订单和订单")
        self.assertEqual([x["start"] for x in r], [0, 3])

    def test_correct_and_wrong_mixed(self):
        t, r = correct_terms("订单和定单")
        self.assertEqual(t, "订单和订单")
        self.assertEqual(len(r), 1)

    def test_boundaries(self):
        self.assertEqual(correct_terms("定单")[0], "订单")
        self.assertEqual(correct_terms("老版")[0], "老板")
        self.assertEqual(correct_terms("a老版b")[0], "a老板b")

    def test_empty_and_empty_lexicon(self):
        self.assertEqual(correct_terms("", None), ("", []))
        self.assertEqual(correct_terms("定单", []), ("定单", []))

    def test_other_chars_unchanged(self):
        src = "你好,123 abc 定单!"
        t, r = correct_terms(src)
        self.assertEqual(t, "你好,123 abc 订单!")
        self.assertEqual(len(t), len(src))

    def test_known_basic(self):
        for wrong in ("负距离", "付距离", "赴据离", "复距离"):
            t, r = correct_terms("他们有" + wrong + "了")
            self.assertEqual(t, "他们有纠纷了")
            self.assertEqual(r, [{"start": 3, "end": 6, "original": wrong,
                                  "replacement": "纠纷", "kind": "known"}])

    def test_known_and_phonetic_mixed(self):
        t, r = correct_terms("定单有付距离")
        self.assertEqual(t, "订单有纠纷")
        self.assertEqual([x["kind"] for x in r], ["phonetic", "known"])

    def test_known_guard_counterexamples(self):
        for s in ("反复距离测量", "往复距离", "正负距离", "支付距离"):
            self.assertEqual(correct_terms(s), (s, []))

    def test_known_override_and_extend(self):
        self.assertEqual(correct_terms("付距离", known={}), ("付距离", []))
        t, _ = correct_terms("付距离和甲乙", known={"甲乙": "纠纷"})
        self.assertEqual(t, "付距离和纠纷")
        t, r = correct_terms("付距离和甲乙", known_extra={"甲乙": "丙丁"})
        self.assertEqual(t, "纠纷和丙丁")
        self.assertEqual({x["kind"] for x in r}, {"known"})

    def test_known_untouched_chars(self):
        t, _ = correct_terms("你好,123 abc 负距离!")
        self.assertEqual(t, "你好,123 abc 纠纷!")
        self.assertEqual(KNOWN_MISRECOGNITIONS["负距离"], "纠纷")

    def test_not_mapped_by_design(self):
        # 连麻: 语义不明,不映射; 去蒙工/去猛攻/去猛工: 无法确认"驱猛工"是干员名,不映射
        for s in ("连麻", "去蒙工", "去猛攻", "去猛工"):
            self.assertEqual(correct_terms(s), (s, []))

    def test_default_lexicon(self):
        self.assertEqual(len(DEFAULT_LEXICON), 20)
        self.assertIn("三角洲", DEFAULT_LEXICON)


if __name__ == "__main__":
    unittest.main()
