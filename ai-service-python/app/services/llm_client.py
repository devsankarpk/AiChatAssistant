"""Thin wrapper around the LLM call. DeepInfra exposes an OpenAI-compatible API
(https://api.deepinfra.com/v1/openai), so the standard `openai` SDK works
unmodified - just pointed at a different base_url with a DeepInfra key.
"""
import time
from collections.abc import Iterator
from dataclasses import dataclass

from openai import APIConnectionError, APIStatusError, APITimeoutError, OpenAI, RateLimitError

from app.config import DEEPINFRA_API_KEY, DEEPINFRA_BASE_URL, DEEPINFRA_MAX_TOKENS, DEEPINFRA_MODEL
from app.schemas import HistoryMessage

# Worst case (timeout x (1 + max_retries), plus backoff) must stay under the .NET caller's 60s
# HttpClient.Timeout. Otherwise .NET gives up first and this service's clean 503 is never seen.
# The SDK's default max_retries=2 at 30s allowed ~90s.
_client = OpenAI(
    api_key=DEEPINFRA_API_KEY,
    base_url=DEEPINFRA_BASE_URL,
    timeout=25.0,
    max_retries=1,
)


class UpstreamError(Exception):
    """Raised for any LLM-call failure. `status_code` is what the route should
    respond with - 503 for transient (timeout/rate-limit), 502 for everything
    else - so a Python failure never surfaces as a bare, crashed 500."""

    def __init__(self, status_code: int, code: str, message: str):
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.message = message


def to_upstream_error(exc: Exception) -> UpstreamError:
    """Maps any failure of the LLM call (before or during a stream) to the error this service reports."""
    if isinstance(exc, UpstreamError):
        return exc
    if isinstance(exc, (APITimeoutError, APIConnectionError)):
        return UpstreamError(503, "llm_timeout", "The LLM provider timed out or was unreachable.")
    if isinstance(exc, RateLimitError):
        return UpstreamError(503, "llm_rate_limited", "The LLM provider is rate-limiting requests. Try again shortly.")
    if isinstance(exc, APIStatusError):
        return UpstreamError(502, "llm_upstream_error", f"The LLM provider returned an error: {exc.message}")
    return UpstreamError(502, "llm_unexpected_error", "Unexpected error calling the LLM provider.")


def _count(usage, field: str) -> int:
    """A token count from the provider's usage block; 0 when it is missing or not a whole number."""
    value = getattr(usage, field, 0) if usage is not None else 0
    return value if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else 0


@dataclass
class Completion:
    reply: str
    tokens_used: int
    prompt_tokens: int = 0
    completion_tokens: int = 0
    model: str = ""


def generate_reply(history: list[HistoryMessage], prompt: str) -> tuple[str, int]:
    """The reply and its total token count. Kept for callers that do not need the split; see `generate_completion`."""
    completion = generate_completion(history, prompt)
    return completion.reply, completion.tokens_used


def generate_completion(history: list[HistoryMessage], prompt: str) -> Completion:
    messages = [{"role": m.role, "content": m.content} for m in history]
    messages.append({"role": "user", "content": prompt})

    try:
        completion = _client.chat.completions.create(
            model=DEEPINFRA_MODEL, messages=messages, max_tokens=DEEPINFRA_MAX_TOKENS
        )
    except Exception as exc:  # noqa: BLE001 - mapped below; this service never crashes with a bare 500
        raise to_upstream_error(exc) from exc

    choice = completion.choices[0] if completion.choices else None
    reply = choice.message.content if choice and choice.message and choice.message.content else ""
    usage = completion.usage
    return Completion(
        reply=reply,
        tokens_used=usage.total_tokens if usage else 0,
        prompt_tokens=_count(usage, "prompt_tokens"),
        completion_tokens=_count(usage, "completion_tokens"),
        model=DEEPINFRA_MODEL,
    )


# Whole-stream budget. The per-request timeout above only bounds the wait between chunks, so a stream that keeps
# trickling could run forever. Python gives up after this long; .NET's own stream deadline (60s) is the outer one,
# so Python's clean error event is the one the user sees. Must stay below 60.
STREAM_DEADLINE_SECONDS = 50.0


@dataclass
class StreamChunk:
    """One piece of a streamed reply: `text` while it is being written, then a last chunk with `tokens_used`."""

    text: str = ""
    tokens_used: int | None = None
    prompt_tokens: int = 0
    completion_tokens: int = 0
    model: str = ""


def open_reply_stream(history: list[HistoryMessage], prompt: str):
    """Starts the streamed LLM call and returns the provider's stream. Failures that happen before the first byte
    (bad key, provider down, rate limit) raise `UpstreamError` here, so the route can still answer with a normal
    HTTP status instead of a half-open event stream."""
    messages = [{"role": m.role, "content": m.content} for m in history]
    messages.append({"role": "user", "content": prompt})
    try:
        return _client.chat.completions.create(
            model=DEEPINFRA_MODEL,
            messages=messages,
            max_tokens=DEEPINFRA_MAX_TOKENS,
            stream=True,
            stream_options={"include_usage": True},
        )
    except Exception as exc:  # noqa: BLE001
        raise to_upstream_error(exc) from exc


def read_reply_stream(stream, deadline_seconds: float = STREAM_DEADLINE_SECONDS) -> Iterator[StreamChunk]:
    """Yields the text as it arrives, then a final chunk with the token count. Raises `UpstreamError` if the
    provider fails mid-stream or the whole-stream deadline passes. Always closes the provider's stream, including
    when the caller stops iterating (the browser pressed Stop)."""
    started = time.monotonic()
    written = 0
    tokens_used: int | None = None
    prompt_tokens = 0
    completion_tokens = 0
    try:
        for event in stream:
            if time.monotonic() - started > deadline_seconds:
                raise UpstreamError(503, "llm_timeout", "The reply took too long.")
            if getattr(event, "usage", None) is not None:
                tokens_used = event.usage.total_tokens
                prompt_tokens = _count(event.usage, "prompt_tokens")
                completion_tokens = _count(event.usage, "completion_tokens")
            choices = getattr(event, "choices", None) or []
            delta = choices[0].delta.content if choices and choices[0].delta else None
            if delta:
                written += len(delta)
                yield StreamChunk(text=delta)
    except UpstreamError:
        raise
    except Exception as exc:  # noqa: BLE001
        raise to_upstream_error(exc) from exc
    finally:
        close = getattr(stream, "close", None)
        if callable(close):
            try:
                close()
            except Exception:  # noqa: BLE001 - closing a dead connection must not hide the real outcome
                pass
    # A provider that sends no usage block still gets a rough count (about four characters a token).
    yield StreamChunk(
        tokens_used=tokens_used if tokens_used is not None else max(1, written // 4),
        prompt_tokens=prompt_tokens,
        completion_tokens=completion_tokens,
        model=DEEPINFRA_MODEL,
    )
