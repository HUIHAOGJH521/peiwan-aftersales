"""In-memory tenant directory and multipart upload bookkeeping for the M2 client API."""

from __future__ import annotations

import hashlib
import math
import uuid
from dataclasses import dataclass, field
from datetime import datetime

from app.errors import ApiError

PART_SIZE = 5 * 1024 * 1024
PILOT_STUDIO = "11111111-1111-4111-8111-111111111111"
PILOT_USER = "22222222-2222-4222-8222-222222222222"


def hash_password(password: str, salt: str) -> str:
    digest = hashlib.pbkdf2_hmac("sha256", password.encode(), salt.encode(), 120_000)
    return f"pbkdf2${salt}${digest.hex()}"


def verify_password(password: str, stored: str) -> bool:
    try:
        scheme, salt, digest = stored.split("$", 2)
    except ValueError:
        return False
    if scheme != "pbkdf2":
        return False
    return hash_password(password, salt) == stored and digest


@dataclass
class Studio:
    id: str
    code: str
    name: str


@dataclass
class User:
    id: str
    studio_id: str
    username: str
    display_name: str
    role: str
    password_hash: str


@dataclass
class Device:
    id: str
    studio_id: str
    player_id: str
    device_fp: str
    gpu_encoder: str
    client_version: str


@dataclass
class Session:
    id: str
    studio_id: str
    player_id: str
    device_id: str
    order_ref: str | None
    note: str | None
    is_manual_backfill: bool
    started_at: datetime
    ended_at: datetime | None
    status: str = "recording"


@dataclass
class Segment:
    id: str
    studio_id: str
    player_id: str
    session_id: str
    seq_no: int
    codec_mode: str
    storage_key: str
    upload_id: str | None
    file_size: int
    sha256: str
    duration_ms: int
    started_at: datetime
    ended_at: datetime
    upload_status: str = "init"
    asr_status: str = "pending"
    parts: dict[int, str] = field(default_factory=dict)


class ClientState:
    def __init__(self, pilot_password: str = "pilot-dev-pass") -> None:
        self.studios = {
            "pilot_01": Studio(PILOT_STUDIO, "pilot_01", "试点工作室"),
        }
        self.users = {
            (PILOT_STUDIO, "pw001"): User(
                PILOT_USER,
                PILOT_STUDIO,
                "pw001",
                "阿泽",
                "player",
                hash_password(pilot_password, "pilot"),
            ),
        }
        self.devices: dict[tuple[str, str, str], Device] = {}
        self.sessions: dict[str, Session] = {}
        self.segments: dict[str, Segment] = {}
        self.enqueued: list[str] = []

    def authenticate(self, studio_code: str, username: str, password: str, device_fp: str,
                     gpu_encoder: str, client_version: str) -> tuple[User, Device]:
        studio = self.studios.get(studio_code)
        if studio is None:
            raise ApiError(401, "ERR_UNAUTHORIZED", "工作室或账号不正确")
        user = self.users.get((studio.id, username))
        if user is None or user.role != "player" or not verify_password(password, user.password_hash):
            raise ApiError(401, "ERR_UNAUTHORIZED", "工作室或账号不正确")
        key = (studio.id, user.id, device_fp)
        device = self.devices.get(key)
        if device is None:
            device = Device(
                str(uuid.uuid5(uuid.NAMESPACE_URL, "|".join(key))),
                studio.id,
                user.id,
                device_fp,
                gpu_encoder,
                client_version,
            )
            self.devices[key] = device
        else:
            device.gpu_encoder = gpu_encoder
            device.client_version = client_version
        return user, device

    def upsert_session(self, session: Session) -> Session:
        current = self.sessions.get(session.id)
        if current is None:
            self.sessions[session.id] = session
            return session
        if current.studio_id != session.studio_id or current.player_id != session.player_id:
            raise ApiError(409, "ERR_CONFLICT", "会话标识已被其他账号占用")
        current.order_ref = session.order_ref
        current.note = session.note
        current.is_manual_backfill = session.is_manual_backfill
        if session.ended_at is not None:
            current.ended_at = session.ended_at
            current.status = "uploaded" if current.status == "uploaded" else "recorded"
        return current

    def finish_session(self, session_id: str, studio_id: str, player_id: str, ended_at: datetime) -> Session:
        session = self.sessions.get(session_id)
        if session is None or session.studio_id != studio_id or session.player_id != player_id:
            raise ApiError(404, "ERR_NOT_FOUND", "接单会话不存在")
        if session.ended_at is not None and session.ended_at != ended_at:
            raise ApiError(409, "ERR_CONFLICT", "会话结束时间已确定")
        session.ended_at = ended_at
        if session.status == "recording":
            session.status = "recorded"
        return session

    def init_segment(self, segment: Segment, player_id: str) -> tuple[Segment, bool]:
        session = self.sessions.get(segment.session_id)
        if session is None or session.studio_id != segment.studio_id or session.player_id != player_id:
            raise ApiError(404, "ERR_NOT_FOUND", "接单会话不存在")
        current = self.segments.get(segment.id)
        if current is None:
            segment.upload_id = f"upload-{segment.id}"
            self.segments[segment.id] = segment
            return segment, False
        if current.studio_id != segment.studio_id or current.session_id != segment.session_id:
            raise ApiError(409, "ERR_CONFLICT", "切片标识已被占用")
        if current.sha256 != segment.sha256 or current.file_size != segment.file_size:
            raise ApiError(409, "ERR_CONFLICT", "同一切片的文件摘要不一致")
        return current, current.upload_status == "completed"

    def record_part(self, segment_id: str, part_number: int, etag: str) -> None:
        segment = self.segments[segment_id]
        segment.parts[part_number] = etag

    def complete_segment(
        self, segment_id: str, studio_id: str, player_id: str, parts: list[tuple[int, str]],
    ) -> Segment:
        segment = self.segments.get(segment_id)
        session = self.sessions.get(segment.session_id) if segment is not None else None
        if (
            segment is None or session is None
            or segment.studio_id != studio_id or session.player_id != player_id
        ):
            raise ApiError(404, "ERR_NOT_FOUND", "切片不存在")
        if segment.upload_status == "completed":
            return segment
        expected = part_count(segment.file_size)
        numbers = [number for number, _ in parts]
        if sorted(numbers) != list(range(1, expected + 1)):
            raise ApiError(409, "ERR_CONFLICT", "分片不完整")
        for number, etag in parts:
            known = segment.parts.get(number)
            if known is not None and known != etag:
                raise ApiError(409, "ERR_CONFLICT", "分片校验值不一致")
            segment.parts[number] = etag
        segment.upload_status = "completed"
        segment.asr_status = "pending"
        self.enqueued.append(segment.id)
        return segment


def part_count(file_size: int) -> int:
    if file_size <= 0:
        return 1
    return math.ceil(file_size / PART_SIZE)


def pending_part_numbers(file_size: int, uploaded: dict[int, str]) -> list[int]:
    return [number for number in range(1, part_count(file_size) + 1) if number not in uploaded]
