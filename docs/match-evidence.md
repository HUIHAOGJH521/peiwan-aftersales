# 单局辱骂证据分析

售后选定一局后，以接单会话起点为零点，提交本局起止时间、双轨转写和已完成双轨处理的时间范围。时间单位为毫秒，区间左闭右开。处理完成的静音时段也计入覆盖范围。缺录或转写失败的时段留空。调用方负责从同一工作室、同一会话获取输入。

## 切片与判定

- 子片段复用 ASR 发言及其稳定 ID。父片段按约 15–30 秒组织，在这个范围内优先选择至少 1 秒的停顿。完整发言优先于窗口时长。可借用相邻的两句作为上下文，最多延伸 10 秒。
- 原始时间戳可跨录像文件。上下文停在单局边界或覆盖缺口。跨这些边界的整句留待复核，避免把范围外的文字送给判定器。
- 每句打手发言恰好分配一次。判定器可以标为 excluded、candidate 或 review。candidate 表示存在打手辱骂老板的证据，等待人工确认，必须回传能支持对象判断的可见发言 ID。系统音可能来自老板、队友、游戏角色或回声。
- 同一次争执可以引用前一条候选证据，引用须在当前窗口可见，且指向同一连续录制范围内前一条候选。重复窗口通过发言 ID 消歧。模型漏答或给出非法引用时，相关批次或片段转待复核。
- 输出候选事件数、逐条证据、待复核发言、缺口以及成功判定的发言数。音频缺失时同时展示缺口。

## 本地验证

在 server 目录运行 `python -m unittest discover -s tests -v`。试跑入口为 `python -m app.match_evidence input.json`。配置 EVIDENCE_MODEL_URL（完整 Chat Completions 地址）和 EVIDENCE_MODEL，鉴权令牌通过可选的 EVIDENCE_MODEL_API_KEY 注入。该入口会把输入范围内的对话文字发给显式指定的模型。测试默认使用模拟判定器。

按发言时间从分段录像裁切双轨语音（16 kHz mono WAV，供人工试听或送 ASR）：

```powershell
$env:PYTHONPATH = ".\server"
$env:FFMPEG_PATH = "C:\path\to\ffmpeg.exe"
python -m app.audio_clip segment.mp4 --start-ms 1200 --end-ms 3800 --track player_mic --out clip.wav
```

`--track` 可选 `player_mic` 或 `system_audio`。裁音时间相对于分段文件起点，从单局发言的会话时间戳中减去该分段的起点即可得到。

社媒真实素材（B 站公开切片，本地测试不入库大文件）见 [`server/tests/fixtures/social/sources.json`](/C:/Users/Windows/Projects/peiwan-aftersales/server/tests/fixtures/social/sources.json)。用 `yt-dlp` 下载 mp4 后按表中 `test_clip` 时间裁切即可复现。

输入字段为 `start_ms`、`end_ms`、`coverage` 和 `utterances`。覆盖项含 `start_ms`、`end_ms`。发言含 `id`、`speaker`（player 或 system）、`start_ms`、`end_ms` 和 `text`。ID 为正整数。

### 从本地录像开始

在 server 目录运行。使用 FFmpeg 和本地 faster-whisper CPU 模型，`--model` 可以指定模型目录，默认读取缓存里的 small。

```powershell
python -m pip install ".[local-asr]"
python -m app.match_recording match.json --transcript-only
python -m app.match_recording match.json
```

`--transcript-only` 只在本地转写。省略此选项会调用已配置的证据模型，返回 `analysis`、转写和媒体问题。清单示例中的路径相对清单目录，时间为接单会话起点的毫秒偏移。

```json
{
  "start_ms": 280000,
  "end_ms": 425000,
  "recordings": [
    {"path": "segment-01.mp4", "start_ms": 0, "end_ms": 300000},
    {"path": "segment-02.mp4", "start_ms": 300000, "end_ms": 600000}
  ]
}
```

使用客户端顺序一致的双轨文件，第一条音轨是打手麦克风，第二条是系统音，文件起点对应清单里的 start_ms。只转写本局范围，缺失文件、音轨或处理失败的区间会反映在双轨覆盖中。试跑按每条轨道解码后的采样长度判断尾部缺口，轨道内部的时间戳断裂还需要在实拍对局中单独核对。

B 站等单轨混音素材试跑时设置 `"audio_layout": "mono_mixed"`（见 [`delta_escort_match.json`](/C:/Users/Windows/Projects/peiwan-aftersales/server/tests/fixtures/social/delta_escort_match.json)、[`peiwan_complaint_match.json`](/C:/Users/Windows/Projects/peiwan-aftersales/server/tests/fixtures/social/peiwan_complaint_match.json)、[`court_dispute_match.json`](/C:/Users/Windows/Projects/peiwan-aftersales/server/tests/fixtures/social/court_dispute_match.json)）。此时仅用第一条音轨转写。默认 `mono_speaker_policy` 为 `mixed_guess`（按关键词粗分 `player` / `system`）；售后主持念投诉清单时可设 `narrator_as_player`，整段记入打手轨待判定。`media_issues` 会带 `mono_mixed_no_separate_track`，**不能**替代正式双轨验收。

一键转写 + stub 判定试跑（需本地 mp4 与 faster-whisper）：

```powershell
$env:PYTHONPATH = ".\server"
python server/tests/fixtures/social/scripts/run_social_demo.py `
  server/tests/fixtures/social/peiwan_complaint_match.json --analyze
```

产物默认写入同目录 `*_transcript.json`（含 `analysis` 字段）。

## 验收

先覆盖跨文件、断录、静音、辱骂队友、重叠发言、短句丢上下文、模型漏答及多次争执等样本。真实对局双人独立标注并复核分歧。对同一测试集统计事件召回率、事件准确率、待复核比例及单局计数全对率，并与现有滑窗 Top 5 对比。事件按证据时间和对象一对一匹配。真实数据评测完成后再确定上线门槛。
