#《三角洲行动》陪玩售后录屏与定位系统 (peiwan-aftersales)

轻量客户端（自动本地录屏 + 双音轨 + 接单物理切段 + 闲置断点续传）+ 云端异步分析（双音轨 VAD/ASR + 混合语义检索 + 关键帧缩略图）+ 售后网页工作台。

## 文档索引
- 需求文档：[docs/requirements.md](./docs/requirements.md)
- 架构设计与任务拆解：[docs/architecture.md](./docs/architecture.md)
- 架构决策记录（ADR）：[docs/decisions/](./docs/decisions/)

## 仓库目录
- `client/`：Windows 陪玩客户端（`C# .NET 8` 托盘主控 + `WGC/WASAPI/FFmpeg` 独立录制子进程）
- `server/`：云端 API 网关与异步分析 Worker（`Python 3.12 + FastAPI + ARQ`）
- `web/`：售后网页工作台（`Next.js 15 + TypeScript`）
- `infra/`：本地基础设施编排（`PostgreSQL 16 + pgvector + Redis + MinIO`）

## 本地基础设施启动（M0）
```bash
cd infra
copy .env.example .env
docker compose up -d
```

- PostgreSQL：`localhost:5432`（库 `peiwan`，含 `vector` 与 `pg_trgm` 扩展）
- Redis：`localhost:6379`
- MinIO S3 API：`http://localhost:9000` / 控制台：`http://localhost:9001`（默认桶 `peiwan-media`）
