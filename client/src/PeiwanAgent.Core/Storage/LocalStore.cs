using Microsoft.Data.Sqlite;
using PeiwanAgent.Core.Models;

namespace PeiwanAgent.Core.Storage;

public sealed class LocalStore
{
    private readonly string _connectionString;

    public LocalStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id          TEXT PRIMARY KEY,
                order_ref           TEXT NULL,
                note                TEXT NULL,
                is_manual_backfill  INTEGER NOT NULL DEFAULT 0,
                started_at          TEXT NOT NULL,
                ended_at            TEXT NULL,
                status              TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS segments (
                segment_id          TEXT PRIMARY KEY,
                session_id          TEXT NULL,
                seq_no              INTEGER NOT NULL,
                is_order            INTEGER NOT NULL DEFAULT 0,
                is_manual_backfill  INTEGER NOT NULL DEFAULT 0,
                codec_mode          TEXT NOT NULL,
                file_path           TEXT NOT NULL,
                file_size           INTEGER NOT NULL,
                sha256              TEXT NOT NULL,
                duration_ms         INTEGER NOT NULL,
                upload_status       TEXT NOT NULL,
                upload_id           TEXT NULL,
                started_at          TEXT NOT NULL,
                ended_at            TEXT NOT NULL,
                expires_at          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS upload_parts (
                segment_id          TEXT NOT NULL,
                part_number         INTEGER NOT NULL,
                etag                TEXT NOT NULL,
                PRIMARY KEY (segment_id, part_number)
            );

            CREATE INDEX IF NOT EXISTS idx_segments_upload_status ON segments(upload_status);
            CREATE INDEX IF NOT EXISTS idx_segments_expires_at ON segments(expires_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertSession(OrderSessionRecord session)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, order_ref, note, is_manual_backfill, started_at, ended_at, status)
            VALUES ($sid, $oref, $note, $backfill, $start, $end, $status)
            ON CONFLICT(session_id) DO UPDATE SET
                order_ref = COALESCE(excluded.order_ref, sessions.order_ref),
                note = COALESCE(excluded.note, sessions.note),
                ended_at = COALESCE(excluded.ended_at, sessions.ended_at),
                status = excluded.status;
            """;
        cmd.Parameters.AddWithValue("$sid", session.SessionId);
        cmd.Parameters.AddWithValue("$oref", (object?)session.OrderRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)session.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$backfill", session.IsManualBackfill ? 1 : 0);
        cmd.Parameters.AddWithValue("$start", session.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$end", (object?)session.EndedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", session.Status);
        cmd.ExecuteNonQuery();
    }

    public void FinishSession(string sessionId, DateTimeOffset endedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET ended_at = $end, status = 'ended'
            WHERE session_id = $sid;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$end", endedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void InsertSegment(MediaSegmentRecord seg)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO segments (
                segment_id, session_id, seq_no, is_order, is_manual_backfill, codec_mode,
                file_path, file_size, sha256, duration_ms, upload_status, upload_id,
                started_at, ended_at, expires_at
            ) VALUES (
                $seg_id, $sid, $seq, $is_order, $backfill, $codec,
                $path, $size, $sha, $dur, $ustatus, $uid,
                $start, $end, $exp
            )
            ON CONFLICT(segment_id) DO UPDATE SET
                file_size = excluded.file_size,
                sha256 = excluded.sha256,
                duration_ms = excluded.duration_ms,
                ended_at = excluded.ended_at;
            """;
        cmd.Parameters.AddWithValue("$seg_id", seg.SegmentId);
        cmd.Parameters.AddWithValue("$sid", (object?)seg.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seq", seg.SeqNo);
        cmd.Parameters.AddWithValue("$is_order", seg.IsOrder ? 1 : 0);
        cmd.Parameters.AddWithValue("$backfill", seg.IsManualBackfill ? 1 : 0);
        cmd.Parameters.AddWithValue("$codec", seg.CodecMode);
        cmd.Parameters.AddWithValue("$path", seg.FilePath);
        cmd.Parameters.AddWithValue("$size", seg.FileSize);
        cmd.Parameters.AddWithValue("$sha", seg.Sha256);
        cmd.Parameters.AddWithValue("$dur", seg.DurationMs);
        cmd.Parameters.AddWithValue("$ustatus", seg.UploadStatus.ToString());
        cmd.Parameters.AddWithValue("$uid", (object?)seg.UploadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", seg.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$end", seg.EndedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$exp", seg.ExpiresAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MediaSegmentRecord> GetRecentPrivateSegments(DateTimeOffset now)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT segment_id, session_id, seq_no, is_order, is_manual_backfill, codec_mode,
                   file_path, file_size, sha256, duration_ms, upload_status, upload_id,
                   started_at, ended_at, expires_at
            FROM segments
            WHERE is_order = 0
              AND upload_status = 'LocalOnly'
              AND expires_at > $now
            ORDER BY started_at DESC;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        return ReadSegments(cmd);
    }

    public IReadOnlyList<MediaSegmentRecord> GetUploadQueue()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT segment_id, session_id, seq_no, is_order, is_manual_backfill, codec_mode,
                   file_path, file_size, sha256, duration_ms, upload_status, upload_id,
                   started_at, ended_at, expires_at
            FROM segments
            WHERE upload_status IN ('Pending', 'Uploading', 'Paused')
            ORDER BY started_at ASC;
            """;
        return ReadSegments(cmd);
    }

    public void MarkSegmentsForManualBackfill(
        IReadOnlyList<string> segmentIds,
        string sessionId,
        string? orderRef,
        string? note,
        DateTimeOffset now)
    {
        if (segmentIds.Count == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // Compute session bounds from selected segments
        var placeholders = string.Join(",", segmentIds.Select((_, idx) => $"$id{idx}"));
        DateTimeOffset minStart = now;
        DateTimeOffset maxEnd = now;

        using (var boundCmd = conn.CreateCommand())
        {
            boundCmd.Transaction = tx;
            boundCmd.CommandText = $"SELECT MIN(started_at), MAX(ended_at) FROM segments WHERE segment_id IN ({placeholders});";
            for (var i = 0; i < segmentIds.Count; i++)
            {
                boundCmd.Parameters.AddWithValue($"$id{i}", segmentIds[i]);
            }
            using var reader = boundCmd.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0) && !reader.IsDBNull(1))
            {
                minStart = DateTimeOffset.Parse(reader.GetString(0));
                maxEnd = DateTimeOffset.Parse(reader.GetString(1));
            }
        }

        using (var sessCmd = conn.CreateCommand())
        {
            sessCmd.Transaction = tx;
            sessCmd.CommandText = """
                INSERT INTO sessions (session_id, order_ref, note, is_manual_backfill, started_at, ended_at, status)
                VALUES ($sid, $oref, $note, 1, $start, $end, 'ended');
                """;
            sessCmd.Parameters.AddWithValue("$sid", sessionId);
            sessCmd.Parameters.AddWithValue("$oref", (object?)orderRef ?? DBNull.Value);
            sessCmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            sessCmd.Parameters.AddWithValue("$start", minStart.ToString("O"));
            sessCmd.Parameters.AddWithValue("$end", maxEnd.ToString("O"));
            sessCmd.ExecuteNonQuery();
        }

        for (var i = 0; i < segmentIds.Count; i++)
        {
            using var updCmd = conn.CreateCommand();
            updCmd.Transaction = tx;
            updCmd.CommandText = """
                UPDATE segments
                SET session_id = $sid,
                    seq_no = $seq,
                    is_order = 1,
                    is_manual_backfill = 1,
                    upload_status = 'Pending'
                WHERE segment_id = $seg_id AND is_order = 0;
                """;
            updCmd.Parameters.AddWithValue("$sid", sessionId);
            updCmd.Parameters.AddWithValue("$seq", i + 1);
            updCmd.Parameters.AddWithValue("$seg_id", segmentIds[i]);
            updCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<MediaSegmentRecord> GetExpiredSegments(DateTimeOffset now)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT segment_id, session_id, seq_no, is_order, is_manual_backfill, codec_mode,
                   file_path, file_size, sha256, duration_ms, upload_status, upload_id,
                   started_at, ended_at, expires_at
            FROM segments
            WHERE expires_at <= $now
              AND upload_status IN ('LocalOnly', 'Completed');
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        return ReadSegments(cmd);
    }

    public void DeleteSegment(string segmentId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM upload_parts WHERE segment_id = $id;
            DELETE FROM segments WHERE segment_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", segmentId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MediaSegmentRecord> GetAllSegments(int limit = 200)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT segment_id, session_id, seq_no, is_order, is_manual_backfill, codec_mode,
                   file_path, file_size, sha256, duration_ms, upload_status, upload_id,
                   started_at, ended_at, expires_at
            FROM segments
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadSegments(cmd);
    }

    private static IReadOnlyList<MediaSegmentRecord> ReadSegments(SqliteCommand cmd)
    {
        var list = new List<MediaSegmentRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MediaSegmentRecord(
                SegmentId: reader.GetString(0),
                SessionId: reader.IsDBNull(1) ? null : reader.GetString(1),
                SeqNo: reader.GetInt32(2),
                IsOrder: reader.GetInt32(3) == 1,
                IsManualBackfill: reader.GetInt32(4) == 1,
                CodecMode: reader.GetString(5),
                FilePath: reader.GetString(6),
                FileSize: reader.GetInt64(7),
                Sha256: reader.GetString(8),
                DurationMs: reader.GetInt32(9),
                UploadStatus: Enum.Parse<UploadStatus>(reader.GetString(10)),
                UploadId: reader.IsDBNull(11) ? null : reader.GetString(11),
                StartedAt: DateTimeOffset.Parse(reader.GetString(12)),
                EndedAt: DateTimeOffset.Parse(reader.GetString(13)),
                ExpiresAt: DateTimeOffset.Parse(reader.GetString(14))
            ));
        }
        return list;
    }
}
