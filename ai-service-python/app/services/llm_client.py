"""Thin wrapper around the LLM call. DeepInfra exposes an OpenAI-compatible API
(https://api.deepinfra.com/v1/openai), so the standard `openai` SDK works
unmodified - just pointed at a different base_url with a DeepInfra key.
"""
from openai import APIConnectionError, APIStatusError, APITimeoutError, OpenAI, RateLimitError

from app.config import DEEPINFRA_API_KEY, DEEPINFRA_BASE_URL, DEEPINFRA_MODEL
from app.schemas import HistoryMessage

_client = OpenAI(api_key=DEEPINFRA_API_KEY, base_url=DEEPINFRA_BASE_URL, timeout=30.0)


class UpstreamError(Exception):
    """Raised for any LLM-call failure. `status_code` is what the route should
    respond with - 503 for transient (timeout/rate-limit), 502 for everything
    else - so a Python failure never surfaces as a bare, crashed 500."""

    def __init__(self, status_code: int, code: str, message: str):
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.message = message


def generate_reply(history: list[HistoryMessage], prompt: str) -> tuple[str, int]:
    messages = [{"role": m.role, "content": m.content} for m in history]
    messages.append({"role": "user", "content": prompt})

    try:
        completion = _client.chat.completions.create(model=DEEPINFRA_MODEL, messages=messages)
    except (APITimeoutError, APIConnectionError) as exc:
        raise UpstreamError(503, "llm_timeout", "The LLM provider timed out or was unreachable.") from exc
    except RateLimitError as exc:
        raise UpstreamError(
            503, "llm_rate_limited", "The LLM provider is rate-limiting requests. Try again shortly."
        ) from exc
    except APIStatusError as exc:
        raise UpstreamError(502, "llm_upstream_error", f"The LLM provider returned an error: {exc.message}") from exc
    except Exception as exc:  # noqa: BLE001 - last resort so this service never crashes with a bare 500
        raise UpstreamError(502, "llm_unexpected_error", "Unexpected error calling the LLM provider.") from exc

    choice = completion.choices[0] if completion.choices else None
    reply = choice.message.content if choice and choice.message and choice.message.content else ""
    tokens_used = completion.usage.total_tokens if completion.usage else 0
    return reply, tokens_used
