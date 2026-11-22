"""Bearer JWT parsing. Tenant identity always comes from the token, never the request body."""

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
