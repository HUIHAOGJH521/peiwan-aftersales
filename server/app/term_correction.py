"""ASR 领域词同音/近音纠偏(纯函数)。

策略:仅做无声调拼音的(模糊)等价匹配,不做语义判断。
模糊规则: zh/z、ch/c、sh/s、n/l 声母,in/ing、en/eng 韵母。
例:电脑(dian nao)与店长(dian zhang)不等价,故不改。
"""
from __future__ import annotations

from typing import Iterable, Sequence

from pypinyin import Style, lazy_pinyin

DEFAULT_LEXICON: tuple[str, ...] = (
    "老板", "投诉", "押金", "陪玩", "打手", "纠纷", "护航", "对赌",
    "证据不足", "工作室", "订单", "退款", "变声器", "店长", "曝光",
    "未成年", "人头", "三角洲", "绝密航天", "见仁见智",
)

_MIN_LEN = 2


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


def correct_terms(
    text: str, lexicon: Iterable[str] | None = None
) -> tuple[str, list[dict]]:
    """返回 (新文本, 纠正记录[{start,end,original,replacement}])。

    lexicon 为 None 时用默认词表;空词表则原样返回。
    """
    if not text:
        return text, []
    words = sorted(
        {w for w in (DEFAULT_LEXICON if lexicon is None else lexicon)
         if len(w) >= _MIN_LEN and all(_is_cjk(c) for c in w)},
        key=lambda w: (-len(w), w),
    )
    if not words:
        return text, []

    # 已正确出现的词所占位置:不允许被改动
    protected = [False] * len(text)
    for w in words:
        i = text.find(w)
        while i != -1:
            for k in range(i, i + len(w)):
                protected[k] = True
            i = text.find(w, i + 1)

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
    taken = [False] * len(text)
    chosen: list[tuple[int, int, str]] = []
    for _, _, s, e, w in cand:
        if any(taken[s:e]):
            continue
        for k in range(s, e):
            taken[k] = True
        chosen.append((s, e, w))
    chosen.sort()

    out: list[str] = []
    records: list[dict] = []
    pos = 0
    for s, e, w in chosen:
        out.append(text[pos:s])
        out.append(w)
        records.append({"start": s, "end": e, "original": text[s:e], "replacement": w})
        pos = e
    out.append(text[pos:])
    return "".join(out), records
