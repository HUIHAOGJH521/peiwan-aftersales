"""Transcribe social manifests and optionally run stub evidence analysis."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
from dataclasses import asdict
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

from app.match_analysis import Interval, Utterance, analyze_match
from app.match_evidence import ChatClassifier
from app.match_recording import LocalWhisper, Recording, transcribe_match
from app.audio_clip import resolve_ffmpeg
import shutil


def stub_classify(ctx: dict) -> dict:
    decisions = []
    for target in ctx["targets"]:
        utter = next(row for row in ctx["utterances"] if row["id"] == target)
        text = utter["text"]
        if any(word in text for word in ("骂", "罵", "脑瘫", "腦殘", "傻逼", "滾", "滚")):
            label = "candidate"
            reason = "命中辱骂关键词"
        elif any(
            word in text
            for word in (
                "投诉", "投訴", "对赌", "對賭", "人头", "人頭", "矛盾", "偷竖",
                "老板投诉", "老闆投訴", "押金", "押金", "曝光", "证据不足", "證據不足",
                "开庭", "開庭", "伪证据", "偽證據",
            )
        ):
            label = "review"
            reason = "纠纷语气待复核"
        else:
            label = "excluded"
            reason = "无辱骂或明确纠纷"
        decisions.append({
            "id": target,
            "label": label,
            "reason": reason,
            "target_evidence_ids": [target],
            "continues_from": None,
        })
    return {"decisions": decisions}


class StubHandler(BaseHTTPRequestHandler):
    def do_POST(self) -> None:
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        context = json.loads(request["messages"][1]["content"])
        payload = stub_classify(context)
        body = json.dumps(
            {"choices": [{"message": {"content": json.dumps(payload, ensure_ascii=False)}}]}
        ).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args) -> None:
        return


def transcribe_manifest(manifest: Path, model: str) -> dict:
    payload = json.loads(manifest.read_text(encoding="utf-8-sig"))
    match = Interval(payload["start_ms"], payload["end_ms"])
    layout = payload.get("audio_layout", "dual")
    mono_policy = payload.get("mono_speaker_policy", "mixed_guess")
    recordings = [
        Recording(item["start_ms"], item["end_ms"], manifest.parent / item["path"])
        for item in payload["recordings"]
    ]
    ffmpeg = shutil.which(resolve_ffmpeg())
    if not ffmpeg:
        raise RuntimeError("FFmpeg not found")
    transcriber = LocalWhisper(model, "zh")
    coverage, utterances, issues = transcribe_match(
        match,
        recordings,
        transcriber,
        ffmpeg=ffmpeg,
        audio_layout=layout,
        mono_speaker_policy=mono_policy,
    )
    return {
        **asdict(match),
        "audio_layout": layout,
        "mono_speaker_policy": mono_policy,
        "coverage": [asdict(part) for part in coverage],
        "utterances": [asdict(item) for item in utterances],
        "media_issues": issues,
    }


def analyze_stub(transcript: dict) -> dict:
    match = Interval(transcript["start_ms"], transcript["end_ms"])
    coverage = [Interval(**part) for part in transcript["coverage"]]
    utterances = [Utterance(**row) for row in transcript["utterances"]]
    with HTTPServer(("127.0.0.1", 0), StubHandler) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            port = server.server_address[1]
            classifier = ChatClassifier(
                f"http://127.0.0.1:{port}/v1/chat/completions",
                "social-stub",
                "",
            )
            return analyze_match(match, coverage, utterances, classifier)
        finally:
            server.shutdown()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--model", default="small")
    parser.add_argument("--analyze", action="store_true")
    parser.add_argument(
        "--out",
        type=Path,
        help="defaults to <manifest_stem>_transcript.json beside manifest",
    )
    args = parser.parse_args()
    out = args.out or args.manifest.with_name(f"{args.manifest.stem}_transcript.json")
    transcript = transcribe_manifest(args.manifest, args.model)
    if args.analyze:
        transcript["analysis"] = analyze_stub(transcript)
    out.write_text(json.dumps(transcript, ensure_ascii=False, indent=2), encoding="utf-8")
    print(out)
    if args.analyze and "analysis" in transcript:
        print(json.dumps(transcript["analysis"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())