"""FunASR transcriber (paraformer-zh + fsmn-vad + ct-punc) with sentence-level timestamps."""

import contextlib
import sys
from collections.abc import Iterable
from pathlib import Path

DEFAULT_ASR_MODEL = "paraformer-zh"
DEFAULT_VAD_MODEL = "fsmn-vad"
DEFAULT_PUNC_MODEL = "ct-punc"


class FunASRTranscriber:
    """Same call signature as LocalWhisper: ``__call__(audio) -> (start_s, end_s, text)``.

    Sentences are split by ct-punc punctuation inside VAD segments; times are in seconds.
    """

    def __init__(
        self,
        *,
        model: str = DEFAULT_ASR_MODEL,
        vad_model: str = DEFAULT_VAD_MODEL,
        punc_model: str = DEFAULT_PUNC_MODEL,
        hotword: str | None = None,
        automodel=None,
    ) -> None:
        if automodel is None:
            try:
                from funasr import AutoModel
            except ImportError as error:
                raise ImportError(
                    "funasr is not installed; run: pip install \".[funasr-asr]\""
                ) from error
            # funasr prints banners/progress to stdout; keep stdout clean for JSON output.
            with contextlib.redirect_stdout(sys.stderr):
                automodel = AutoModel(
                    model=model, vad_model=vad_model, punc_model=punc_model,
                    hub="ms", disable_update=True,
                )
        self.model = automodel
        self.hotword = hotword or None

    def __call__(self, audio: Path) -> Iterable[tuple[float, float, str]]:
        kwargs = {"input": str(audio), "sentence_timestamp": True}
        if self.hotword:
            kwargs["hotword"] = self.hotword
        with contextlib.redirect_stdout(sys.stderr):
            items = self.model.generate(**kwargs) or []
        for item in items:
            sentences = item.get("sentence_info") or []
            for sentence in sentences:
                text = (sentence.get("text") or "").strip()
                if text:
                    yield sentence["start"] / 1000, sentence["end"] / 1000, text
            if not sentences:
                # No sentence info: fall back to the whole text spanning word timestamps.
                text = (item.get("text") or "").strip()
                stamps = item.get("timestamp") or []
                if text and stamps:
                    yield stamps[0][0] / 1000, stamps[-1][1] / 1000, text
