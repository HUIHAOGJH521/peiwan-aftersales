"""Bearer JWT parsing. Tenant identity always comes from the token, never the request body."""

import time
from dataclasses import dataclass

import jwt
from fastapi import Depends
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from app.config import settings
from app.errors import ApiError

_bearer = HTTPBearer(auto_error=False)


@dataclass(frozen=True)
class Principal:
    user_id: str
    studio_id: str
    role: str
    device_id: str | None = None


def get_principal(
    credentials: HTTPAuthorizationCredentials | None = Depends(_bearer),
) -> Principal:
    if credentials is None:
        raise ApiError(401, "ERR_UNAUTHORIZED", "缺少访问令牌")
    try:
        claims = jwt.decode(
            credentials.credentials,
            settings.jwt_secret,
            algorithms=[settings.jwt_algorithm],
            options={"require": ["exp", "sub", "studio_id", "role"]},
        )
    except jwt.PyJWTError as error:
        raise ApiError(401, "ERR_UNAUTHORIZED", "访问令牌无效或已过期") from error
    values = (claims["sub"], claims["studio_id"], claims["role"])
    if not all(isinstance(value, str) and value for value in values):
        raise ApiError(401, "ERR_UNAUTHORIZED", "访问令牌缺少身份信息")
    device_id = claims.get("device_id")
    return Principal(*values, device_id if isinstance(device_id, str) else None)


def require_aftersales(principal: Principal = Depends(get_principal)) -> Principal:
    if principal.role not in ("aftersales", "admin"):
        raise ApiError(403, "ERR_FORBIDDEN", "仅售后或管理员可使用该接口")
    return principal


def require_player(principal: Principal = Depends(get_principal)) -> Principal:
    if principal.role not in ("player", "admin"):
        raise ApiError(403, "ERR_FORBIDDEN", "仅陪玩客户端可使用该接口")
    if not principal.device_id:
        raise ApiError(401, "ERR_UNAUTHORIZED", "访问令牌缺少设备信息")
    return principal


def issue_token(principal: Principal, *, kind: str, ttl_sec: int) -> str:
    now = int(time.time())
    claims = {
        "sub": principal.user_id,
        "studio_id": principal.studio_id,
        "role": principal.role,
        "device_id": principal.device_id,
        "typ": kind,
        "iat": now,
        "exp": now + ttl_sec,
    }
    return jwt.encode(claims, settings.jwt_secret, algorithm=settings.jwt_algorithm)
