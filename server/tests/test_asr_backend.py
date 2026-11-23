import contextlib
import io
import json
import shutil
import sys
import types
import unittest
import wave
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

from app import match_recording
from app.asr_funasr import FunASRTranscriber
from app.audio_clip import resolve_ffmpeg
from app.match_recording import TermCorrectingTranscriber, build_transcriber

FFMPEG = shutil.which(resolve_ffmpeg())


class FakeAutoModel:
    def __init__(self, result):
        self.result = result
        self.calls = []

    def generate(self, **kwargs):
        self.calls.append(kwargs)
        return self.result


class FunASRTranscriberTests(unittest.TestCase):
    def test_sentences_in_seconds(self):
        fake = FakeAutoModel([{"text": "a", "sentence_info": [
            {"start": 500, "end": 2250, "text": "你好。"},
            {"start": 2500, "end": 3000, "text": "  "},
            {"start": 3000, "end": 4000, "text": "再见"},
        ]}])
        spans = list(FunASRTranscriber(automodel=fake, hotword="老板")(Path("x.wav")))
        self.assertEqual(spans, [(0.5, 2.25, "你好。"), (3.0, 4.0, "再见")])
        self.assertTrue(fake.calls[0]["sentence_timestamp"])
        self.assertEqual(fake.calls[0]["hotword"], "老板")

    def test_fallback_without_sentence_info(self):
        fake = FakeAutoModel([{"text": "整段", "timestamp": [[100, 200], [300, 900]]}])
        self.assertEqual(list(FunASRTranscriber(automodel=fake)(Path("x.wav"))), [(0.1, 0.9, "整段")])

    def test_missing_funasr_reports_clear_error(self):
        with mock.patch.dict(sys.modules, {"funasr": None}):
            with self.assertRaisesRegex(ImportError, "funasr-asr"):
                FunASRTranscriber()


class TermCorrectingTests(unittest.TestCase):
    def test_corrects_text_keeps_timestamps_and_records(self):
        inner = lambda audio: [(1.0, 2.5, "我的定单有问题"), (3.0, 4.0, "没问题")]
        wrapper = TermCorrectingTranscriber(inner)
        spans = list(wrapper(Path("x.wav")))
        self.assertEqual(spans, [(1.0, 2.5, "我的订单有问题"), (3.0, 4.0, "没问题")])
        self.assertEqual(len(wrapper.corrections), 1)
        record = wrapper.corrections[0]
        self.assertEqual((record["original"], record["replacement"]), ("定单", "订单"))
        self.assertEqual((record["start_s"], record["end_s"]), (1.0, 2.5))
        self.assertEqual(record["sentence"], "我的定单有问题")


class BackendSelectionTests(unittest.TestCase):
    def args(self, backend):
        return types.SimpleNamespace(
            asr_backend=backend, model="small", language="zh", initial_prompt=None,
            no_vad=False, cpu_threads=0,
        )

    def test_funasr_backend(self):
        with mock.patch("app.asr_funasr.FunASRTranscriber") as funasr, \
                mock.patch.object(match_recording, "LocalWhisper") as whisper:
            self.assertIs(build_transcriber(self.args("funasr")), funasr.return_value)
            whisper.assert_not_called()

    def test_whisper_backend(self):
        with mock.patch("app.asr_funasr.FunASRTranscriber") as funasr, \
                mock.patch.object(match_recording, "LocalWhisper") as whisper:
            self.assertIs(build_transcriber(self.args("whisper")), whisper.return_value)
            funasr.assert_not_called()
            self.assertEqual(whisper.call_args.args, ("small", "zh"))


@unittest.skipUnless(FFMPEG, "FFmpeg is required")
class MainOutputTests(unittest.TestCase):
    def run_main(self, *extra):
        fake = lambda audio: [(0.1, 0.5, "我的定单有问题")]
        with TemporaryDirectory() as temp:
            wav = Path(temp) / "a.wav"
            with wave.open(str(wav), "wb") as out:
                out.setnchannels(1)
                out.setsampwidth(2)
                out.setframerate(16000)
                out.writeframes(b"\0\0" * 16000)
            manifest = Path(temp) / "m.json"
            manifest.write_text(json.dumps({
                "start_ms": 0, "end_ms": 1000, "audio_layout": "mono_mixed",
                "recordings": [{"path": "a.wav", "start_ms": 0, "end_ms": 1000}],
            }), encoding="utf-8")
            argv = ["match_recording", str(manifest), "--transcript-only", *extra]
            buffer = io.StringIO()
            with mock.patch.object(sys, "argv", argv), \
                    mock.patch.object(match_recording, "build_transcriber", return_value=fake) as build, \
                    contextlib.redirect_stdout(buffer):
                match_recording.main()
        return json.loads(buffer.getvalue()), build

    def test_default_backend_and_term_corrections_in_json(self):
        result, build = self.run_main()
        self.assertEqual(build.call_args.args[0].asr_backend, "funasr")
        self.assertEqual(result["utterances"][0]["text"], "我的订单有问题")
        self.assertEqual((result["utterances"][0]["start_ms"], result["utterances"][0]["end_ms"]), (100, 500))
        self.assertEqual(result["term_corrections"][0]["replacement"], "订单")

    def test_no_term_correction_and_whisper_flag(self):
        result, build = self.run_main("--no-term-correction", "--asr-backend", "whisper")
        self.assertEqual(build.call_args.args[0].asr_backend, "whisper")
        self.assertEqual(result["utterances"][0]["text"], "我的定单有问题")
        self.assertNotIn("term_corrections", result)


if __name__ == "__main__":
    unittest.main()
