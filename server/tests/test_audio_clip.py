import os
import unittest
from pathlib import Path

from app.audio_clip import extract_utterance_clip

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "smoke_dual.mp4"
FFMPEG = os.environ.get("FFMPEG_PATH", "").strip() or None


@unittest.skipUnless(FIXTURE.is_file(), "smoke_dual.mp4 fixture missing (run client smoke test)")
class AudioClipExtractionTests(unittest.TestCase):
    def test_extract_player_mic_two_second_window(self) -> None:
        out = FIXTURE.with_name("smoke_dual_clip_test.wav")
        if out.is_file():
            out.unlink()
        clip = extract_utterance_clip(
            FIXTURE, 1000, 3000, track="player_mic", output_path=out, ffmpeg=FFMPEG
        )
        self.assertTrue(clip.is_file())
        self.assertGreater(clip.stat().st_size, 10_000)
        duration = clip.stat().st_size / (16000 * 2)  # 16 kHz mono s16le
        self.assertGreater(duration, 1.8)
        self.assertLess(duration, 2.3)


if __name__ == "__main__":
    unittest.main()