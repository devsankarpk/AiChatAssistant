"""Request/response models for the .NET <-> Python contract (see CLAUDE.md's
'Cross-service contracts' section - changing these means updating both sides
and README.md's schema section).
"""
from typing import Literal

from pydantic import BaseModel, Field


class HistoryMessage(BaseModel):
    role: Literal["user", "assistant"]
    content: str


class GenerateRequest(BaseModel):
    session_id: str = Field(..., description="Guid, as a string - opaque to this service.")
    history: list[HistoryMessage] = Field(default_factory=list)
    prompt: str


class GenerateResponse(BaseModel):
    reply: str
    tokens_used: int
