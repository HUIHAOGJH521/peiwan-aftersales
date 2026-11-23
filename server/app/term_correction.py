"""ASR 领域词同音/近音纠偏(纯函数)。

两层策略:
1. 已知误识别映射 KNOWN_MISRECOGNITIONS(kind='known'):精确子串替换。
2. 拼音(模糊)等价匹配(kind='phonetic'),不做语义判断。
模糊规则: zh/z、ch/c、sh/s、n/l 声母,in/ing、en/eng 韵母。
例:电脑(dian nao)与店长(dian zhang)不等价,故不改。
"""
from __future__ import annotations

from typing import Iterable, Mapping, Sequence

from pypinyin import Style, lazy_pinyin

DEFAULT_LEXICON: tuple[str, ...] = (
    "老板", "投诉", "押金", "陪玩", "打手", "纠纷", "护航", "对赌",
    "证据不足", "工作室", "订单", "退款", "变声器", "店长", "曝光",
    "未成年", "人头", "三角洲", "绝密航天", "见仁见智",
)

_MIN_LEN = 2

# 已知误识别映射: 误识别串 -> 正确词。调用方可整体覆盖或追加。
# 未收录(把握不足,不映射): 连麻(语义不明); 去蒙工/去猛攻/去猛工(未能确认
# "驱猛工"是三角洲干员名); 电脑挂朋友圈(上下文不明)。
KNOWN_MISRECOGNITIONS: Mapping[str, str] = {
    "负距离": "纠纷",
    "付距离": "纠纷",
    "赴据离": "纠纷",
    "复距离": "纠纷",
}

# 命中串若位于这些更长的合法词内部则不替换(如 反复距离、支付距离)。
KNOWN_GUARDS: tuple[str, ...] = (
    "正负距离", "往复距离", "反复距离", "重复距离", "来复距离",
    "支付距离", "预付距离", "先付距离", "代付距离",
)


def _is_cjk(ch: str) -> bool:
    return "\u4e00" <= ch <= "\u9fff"


def _syllables(s: str) -> list[str]:
    return lazy_pinyin(s, style=Style.NORMAL, errors=lambda x: list(x))


def _fuzzy(syl: str) -> str:
    for a, b in (("zh", "z"), ("ch", "c"), ("sh", "s")):
        if syl.startswith(a):
            syl = b + syl[len(a):]
            break
    if syl.startswith("l"):
        syl = "n" + syl[1:]
    if syl.endswith("ing"):
        syl = syl[:-1]
    elif syl.endswith("eng"):
        syl = syl[:-1]
    return syl


def _mark(text: str, needles: Iterable[str]) -> list[bool]:
    flags = [False] * len(text)
    for w in needles:
        if not w:
            continue
        i = text.find(w)
        while i != -1:
            for k in range(i, i + len(w)):
                flags[k] = True
            i = text.find(w, i + 1)
    return flags


def _known_matches(
    text: str,
    table: Mapping[str, str],
    guards: Sequence[str],
    blocked: Sequence[bool],
) -> list[tuple[int, int, str]]:
    """已知误识别表的非重叠匹配(长串优先),跳过守卫上下文与 blocked 位置。"""
    guarded = _mark(text, guards)
    taken = [False] * len(text)
    found: list[tuple[int, int, str]] = []
    for wrong in sorted((w for w in table if w), key=lambda w: (-len(w), w)):
        n = len(wrong)
        i = text.find(wrong)
        while i != -1:
            span = range(i, i + n)
            if not any(guarded[k] or blocked[k] or taken[k] for k in span):
                for k in span:
                    taken[k] = True
                found.append((i, i + n, table[wrong]))
            i = text.find(wrong, i + 1)
    found.sort()
    return found


def correct_terms(
    text: str,
    lexicon: Iterable[str] | None = None,
    known: Mapping[str, str] | None = None,
    known_extra: Mapping[str, str] | None = None,
    known_guards: Sequence[str] | None = None,
) -> tuple[str, list[dict]]:
    """返回 (新文本, 纠正记录[{start,end,original,replacement,kind}])。

    kind='known' 为已知误识别映射, kind='phonetic' 为拼音模糊纠偏。
    lexicon 为 None 时用默认词表;空词表只关闭拼音纠偏。
    known 覆盖默认映射(传 {} 关闭), known_extra 在其上追加(同键覆盖)。
    known_guards 覆盖默认守卫上下文。
    """
    if not text:
        return text, []
    words = sorted(
        {w for w in (DEFAULT_LEXICON if lexicon is None else lexicon)
         if len(w) >= _MIN_LEN and all(_is_cjk(c) for c in w)},
        key=lambda w: (-len(w), w),
    )

    # 已正确出现的词所占位置:不允许被改动
    protected = _mark(text, words)

    table = dict(KNOWN_MISRECOGNITIONS if known is None else known)
    if known_extra:
        table.update(known_extra)
    guards = KNOWN_GUARDS if known_guards is None else known_guards

    taken = [False] * len(text)
    chosen: list[tuple[int, int, str, str]] = []
    for s, e, w in _known_matches(text, table, guards, protected):
        for k in range(s, e):
            taken[k] = True
            protected[k] = True
        chosen.append((s, e, w, "known"))

    cand: list[tuple[int, int, int, int, str]] = []  # (exact?0:1, -len, start, end, word)
    for w in words:
        n = len(w)
        wsyl = _syllables(w)
        wfuz = [_fuzzy(s) for s in wsyl]
        for i in range(len(text) - n + 1):
            win = text[i:i + n]
            if win == w or not all(_is_cjk(c) for c in win):
                continue
            if any(protected[i:i + n]):
                continue
            # 每个字都要拼音(模糊)相同;至少一个字不同才替换(win != w 已保证)
            ssyl = _syllables(win)
            if len(ssyl) != n:
                continue
            if [_fuzzy(s) for s in ssyl] != wfuz:
                continue
            exact = 0 if ssyl == wsyl else 1
            cand.append((exact, -n, i, i + n, w))

    cand.sort()
    for _, _, s, e, w in cand:
        if any(taken[s:e]):
            continue
        for k in range(s, e):
            taken[k] = True
        chosen.append((s, e, w, "phonetic"))
    chosen.sort()

    out: list[str] = []
    records: list[dict] = []
    pos = 0
    for s, e, w, kind in chosen:
        out.append(text[pos:s])
        out.append(w)
        records.append({"start": s, "end": e, "original": text[s:e],
                        "replacement": w, "kind": kind})
        pos = e
    out.append(text[pos:])
    return "".join(out), records
