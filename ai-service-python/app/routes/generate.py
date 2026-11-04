import json
from collections.abc import Iterator

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import StreamingResponse

from app.auth.api_key import require_internal_key
from app.schemas import GenerateRequest, GenerateResponse
from app.services.llm_client import UpstreamError, generate_reply, open_reply_stream, read_reply_stream

router = APIRouter()


@router.post("/generate", response_model=GenerateResponse, dependencies=[Depends(require_internal_key)])
async def generate(request: GenerateRequest) -> GenerateResponse:
    try:
        reply, tokens_used = generate_reply(request.history, request.prompt)
    except UpstreamError as exc:
        raise HTTPException(
            status_code=exc.status_code,
            detail={"error": {"code": exc.code, "message": exc.message}},
        ) from exc

    return GenerateResponse(reply=reply, tokens_used=tokens_used)


def _event(name: str, data: dict) -> str:
    return f"event: {name}\ndata: {json.dumps(data)}\n\n"


def _events(stream) -> Iterator[str]:
    try:
        for chunk in read_reply_stream(stream):
            if chunk.tokens_used is not None:
                yield _event("done", {"tokens_used": chunk.tokens_used})
            else:
                yield _event("delta", {"text": chunk.text})
    except UpstreamError as exc:
        # Headers are already sent, so a failure now is reported as an event, not a status code.
        yield _event("error", {"error": {"code": exc.code, "message": exc.message}})


@router.post("/generate/stream", dependencies=[Depends(require_internal_key)])
def generate_stream(request: GenerateRequest) -> StreamingResponse:
    """Server-sent events: `delta` {text} while the reply is written, then `done` {tokens_used}, or `error`.
    A failure before the first byte is an ordinary 503/502 like `/generate`."""
    try:
        stream = open_reply_stream(request.history, request.prompt)
    except UpstreamError as exc:
        raise HTTPException(
            status_code=exc.status_code,
            detail={"error": {"code": exc.code, "message": exc.message}},
        ) from exc

    return StreamingResponse(
        _events(stream),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )
