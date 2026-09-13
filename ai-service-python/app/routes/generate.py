from fastapi import APIRouter, Depends, HTTPException

from app.auth.api_key import require_internal_key
from app.schemas import GenerateRequest, GenerateResponse
from app.services.llm_client import UpstreamError, generate_reply

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
