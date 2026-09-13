"""Shared-secret auth: this service has no user concept and is never exposed to
the browser (see CLAUDE.md). Every caller (only backend-dotnet, in practice)
must present the same INTERNAL_API_KEY the two sides agree on out of band.
"""
from fastapi import Header, HTTPException, status

from app.config import INTERNAL_API_KEY


async def require_internal_key(x_internal_key: str | None = Header(default=None)) -> None:
    if x_internal_key is None or x_internal_key != INTERNAL_API_KEY:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={"error": {"code": "unauthorized", "message": "Missing or invalid X-Internal-Key header."}},
        )
