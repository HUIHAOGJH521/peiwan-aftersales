"""Download segment objects from S3-compatible storage."""

from __future__ import annotations

import urllib.request
from pathlib import Path

import boto3
from botocore.client import Config

from app.config import Settings, settings


def s3_client(cfg: Settings | None = None):
    cfg = cfg or settings
    return boto3.client(
        "s3",
        endpoint_url=cfg.s3_endpoint,
        aws_access_key_id=cfg.s3_access_key,
        aws_secret_access_key=cfg.s3_secret_key,
        config=Config(signature_version="s3v4"),
    )


def download_object(key: str, dest: Path, *, cfg: Settings | None = None) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    s3_client(cfg).download_file((cfg or settings).s3_bucket, key, str(dest))
    return dest


def presigned_get_url(key: str, *, expires_sec: int = 3600, cfg: Settings | None = None) -> str:
    cfg = cfg or settings
    return s3_client(cfg).generate_presigned_url(
        "get_object",
        Params={"Bucket": cfg.s3_bucket, "Key": key},
        ExpiresIn=expires_sec,
    )


def download_presigned(url: str, dest: Path) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url, timeout=120) as response:
        dest.write_bytes(response.read())
    return dest