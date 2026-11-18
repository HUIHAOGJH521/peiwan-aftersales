CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- 1. 工作室租户表
CREATE TABLE IF NOT EXISTS studios (
    id              UUID PRIMARY KEY,
    code            VARCHAR(64) NOT NULL UNIQUE,
    name            VARCHAR(100) NOT NULL,
    retention_days  INT NOT NULL DEFAULT 30,
    status          VARCHAR(20) NOT NULL DEFAULT 'active',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 2. 用户表（陪玩师 / 售后 / 管理员）
CREATE TABLE IF NOT EXISTS users (
    id              UUID PRIMARY KEY,
    studio_id       UUID NOT NULL REFERENCES studios(id),
    username        VARCHAR(64) NOT NULL,
    display_name    VARCHAR(64) NOT NULL,
    role            VARCHAR(20) NOT NULL,
    password_hash   TEXT NOT NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'active',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (studio_id, username)
);

-- 3. 客户端设备表
CREATE TABLE IF NOT EXISTS devices (
    id              UUID PRIMARY KEY,
    studio_id       UUID NOT NULL REFERENCES studios(id),
    player_id       UUID NOT NULL REFERENCES users(id),
    device_fp       VARCHAR(128) NOT NULL,
    gpu_encoder     VARCHAR(32) NOT NULL DEFAULT 'unknown',
    client_version  VARCHAR(32) NOT NULL,
    last_seen_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (studio_id, player_id, device_fp)
);

-- 4. 接单会话表
CREATE TABLE IF NOT EXISTS order_sessions (
    id                  UUID PRIMARY KEY,
    studio_id           UUID NOT NULL REFERENCES studios(id),
    player_id           UUID NOT NULL REFERENCES users(id),
    device_id           UUID NOT NULL REFERENCES devices(id),
    order_ref           VARCHAR(128),
    note                VARCHAR(500),
    status              VARCHAR(24) NOT NULL DEFAULT 'recording',
    is_manual_backfill  BOOLEAN NOT NULL DEFAULT FALSE,
    started_at          TIMESTAMPTZ NOT NULL,
    ended_at            TIMESTAMPTZ,
    expires_at          TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_sessions_studio_player_time
    ON order_sessions (studio_id, player_id, started_at DESC);
CREATE INDEX IF NOT EXISTS idx_sessions_studio_order_ref
    ON order_sessions (studio_id, order_ref) WHERE order_ref IS NOT NULL;

-- 5. 录像切片表
CREATE TABLE IF NOT EXISTS media_segments (
    id              UUID PRIMARY KEY,
    studio_id       UUID NOT NULL REFERENCES studios(id),
    session_id      UUID NOT NULL REFERENCES order_sessions(id) ON DELETE CASCADE,
    seq_no          INT NOT NULL,
    codec_mode      VARCHAR(24) NOT NULL,
    storage_key     TEXT NOT NULL,
    s3_upload_id    TEXT,
    file_size       BIGINT NOT NULL,
    sha256          CHAR(64) NOT NULL,
    duration_ms     INT NOT NULL,
    upload_status   VARCHAR(24) NOT NULL DEFAULT 'init',
    asr_status      VARCHAR(24) NOT NULL DEFAULT 'pending',
    started_at      TIMESTAMPTZ NOT NULL,
    ended_at        TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (session_id, seq_no)
);
CREATE INDEX IF NOT EXISTS idx_segments_studio_session
    ON media_segments (studio_id, session_id, seq_no);

-- 6. 双音轨转写句子表
CREATE TABLE IF NOT EXISTS transcript_utterances (
    id              BIGSERIAL PRIMARY KEY,
    studio_id       UUID NOT NULL,
    session_id      UUID NOT NULL REFERENCES order_sessions(id) ON DELETE CASCADE,
    segment_id      UUID NOT NULL REFERENCES media_segments(id) ON DELETE CASCADE,
    speaker         VARCHAR(16) NOT NULL,
    start_ms        INT NOT NULL,
    end_ms          INT NOT NULL,
    raw_text        TEXT NOT NULL,
    normalized_text TEXT NOT NULL,
    pinyin_text     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_utterances_studio_session_time
    ON transcript_utterances (studio_id, session_id, start_ms);
CREATE INDEX IF NOT EXISTS idx_utterances_pinyin_trgm
    ON transcript_utterances USING gin (pinyin_text gin_trgm_ops);

-- 7. 语义检索窗口切片表
CREATE TABLE IF NOT EXISTS transcript_chunks (
    id              BIGSERIAL PRIMARY KEY,
    studio_id       UUID NOT NULL,
    session_id      UUID NOT NULL REFERENCES order_sessions(id) ON DELETE CASCADE,
    segment_id      UUID NOT NULL REFERENCES media_segments(id) ON DELETE CASCADE,
    start_ms        INT NOT NULL,
    end_ms          INT NOT NULL,
    combined_text   TEXT NOT NULL,
    thumbnail_key   TEXT,
    embedding       vector(1024) NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_chunks_studio_session
    ON transcript_chunks (studio_id, session_id);
CREATE INDEX IF NOT EXISTS idx_chunks_embedding_hnsw
    ON transcript_chunks USING hnsw (embedding vector_cosine_ops);

-- 8. 售后核对记录表
CREATE TABLE IF NOT EXISTS aftersales_cases (
    id              UUID PRIMARY KEY,
    studio_id       UUID NOT NULL REFERENCES studios(id),
    session_id      UUID NOT NULL REFERENCES order_sessions(id),
    handler_id      UUID NOT NULL REFERENCES users(id),
    complaint_text  TEXT NOT NULL,
    matched_points  JSONB NOT NULL,
    verdict_note    TEXT,
    status          VARCHAR(24) NOT NULL DEFAULT 'open',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_cases_studio_session
    ON aftersales_cases (studio_id, session_id, created_at DESC);

-- 初始化默认试点工作室与本地开发账号（密码占位仅供本地联调）
INSERT INTO studios (id, code, name, retention_days)
VALUES ('11111111-1111-1111-1111-111111111111', 'pilot_01', '试点工作室 01', 30)
ON CONFLICT (id) DO NOTHING;

INSERT INTO users (id, studio_id, username, display_name, role, password_hash)
VALUES
    ('22222222-2222-2222-2222-222222222201', '11111111-1111-1111-1111-111111111111', 'pw001', '陪玩-阿泽', 'player', 'dev-placeholder'),
    ('22222222-2222-2222-2222-222222222202', '11111111-1111-1111-1111-111111111111', 'as001', '售后-小安', 'aftersales', 'dev-placeholder'),
    ('22222222-2222-2222-2222-222222222203', '11111111-1111-1111-1111-111111111111', 'admin01', '管理员', 'admin', 'dev-placeholder')
ON CONFLICT (studio_id, username) DO NOTHING;
