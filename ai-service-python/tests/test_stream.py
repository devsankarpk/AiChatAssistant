"""Streaming endpoint (Phase 25). The provider's stream is faked with plain objects; no network, no real LLM."""

import json
from types import SimpleNamespace

import httpx
import pytest
from fastapi.testclient import TestClient
from openai import APIConnectionError, APIStatusError, RateLimitError

from app.main import app
from app.services import llm_client as llm
from app.services.llm_client import StreamChunk, UpstreamError, read_reply_stream

client = TestClient(app)
AUTH = {"X-Internal-Key": "test-internal-key"}
BODY = {"session_id": "s1", "history": [{"role": "user", "content": "Hi"}], "prompt": "Tell me more"}


def _event(text=None, total_tokens=None, prompt=0, completion=0):
    choices = [SimpleNamespace(delta=SimpleNamespace(content=text))] if text is not None else []
    usage = SimpleNamespace(total_tokens=total_tokens, prompt_tokens=prompt, completion_tokens=completion) if total_tokens is not None else None
    return SimpleNamespace(choices=choices, usage=usage)


class FakeStream:
    """Iterable like the provider's stream; can raise part-way and records whether it was closed."""

    def __init__(self, events, fail_after=None, error=None):
        self.events = events
        self.fail_after = fail_after
        self.error = error
        self.closed = False

    def __iter__(self):
        for i, e in enumerate(self.events):
            if self.fail_after is not None and i == self.fail_after:
                raise self.error
            yield e

    def close(self):
        self.closed = True


def _parse(text):
    out = []
    for block in text.strip().split("\n\n"):
        name, data = None, None
        for line in block.splitlines():
            if line.startswith("event: "):
                name = line[7:]
            elif line.startswith("data: "):
                data = json.loads(line[6:])
        out.append((name, data))
    return out


def _use(monkeypatch, stream=None, error=None):
    def create(**kwargs):
        create.kwargs = kwargs
        if error:
            raise error
        return stream

    monkeypatch.setattr(llm._client.chat.completions, "create", create)
    return create


def _resp(code):
    return httpx.Response(code, request=httpx.Request("POST", "https://x.example"))


def test_stream_requires_the_internal_key():
    assert client.post("/generate/stream", json=BODY).status_code == 401


def test_stream_sends_deltas_in_order_then_done(monkeypatch):
    stream = FakeStream([_event("Hel"), _event("lo"), _event(" there"), _event(None, 12)])
    create = _use(monkeypatch, stream)

    r = client.post("/generate/stream", json=BODY, headers=AUTH)

    assert r.status_code == 200
    assert r.headers["content-type"].startswith("text/event-stream")
    assert r.headers["cache-control"] == "no-cache"
    events = _parse(r.text)
    assert [e[0] for e in events] == ["delta", "delta", "delta", "done"]
    assert "".join(e[1]["text"] for e in events[:-1]) == "Hello there"
    assert events[-1][1]["tokens_used"] == 12
    assert create.kwargs["stream"] is True
    assert create.kwargs["stream_options"] == {"include_usage": True}
    assert create.kwargs["messages"][-1] == {"role": "user", "content": "Tell me more"}
    assert stream.closed


def test_stream_estimates_tokens_when_the_provider_sends_no_usage(monkeypatch):
    _use(monkeypatch, FakeStream([_event("x" * 40)]))
    events = _parse(client.post("/generate/stream", json=BODY, headers=AUTH).text)
    assert events[-1][0] == "done" and events[-1][1]["tokens_used"] == 10


def test_stream_skips_empty_deltas_and_role_only_events(monkeypatch):
    _use(monkeypatch, FakeStream([_event(None), _event(""), _event("ok"), _event(None, 3)]))
    events = _parse(client.post("/generate/stream", json=BODY, headers=AUTH).text)
    assert [e[0] for e in events] == ["delta", "done"]


@pytest.mark.parametrize(
    "error,status,code",
    [
        (APIConnectionError(request=httpx.Request("POST", "https://x.example")), 503, "llm_timeout"),
        (RateLimitError("slow down", response=_resp(429), body=None), 503, "llm_rate_limited"),
        (APIStatusError("bad", response=_resp(500), body=None), 502, "llm_upstream_error"),
        (RuntimeError("boom"), 502, "llm_unexpected_error"),
    ],
)
def test_failure_before_the_first_byte_is_a_normal_http_error(monkeypatch, error, status, code):
    _use(monkeypatch, error=error)
    r = client.post("/generate/stream", json=BODY, headers=AUTH)
    assert r.status_code == status
    assert r.json()["detail"]["error"]["code"] == code


def test_failure_mid_stream_is_an_error_event_after_the_text_already_sent(monkeypatch):
    stream = FakeStream(
        [_event("par"), _event("tial")], fail_after=1,
        error=APIConnectionError(request=httpx.Request("POST", "https://x.example")),
    )
    _use(monkeypatch, stream)

    r = client.post("/generate/stream", json=BODY, headers=AUTH)

    assert r.status_code == 200
    events = _parse(r.text)
    assert [e[0] for e in events] == ["delta", "error"]
    assert events[-1][1]["error"]["code"] == "llm_timeout"
    assert stream.closed


def test_a_stream_that_runs_past_the_deadline_is_cut_with_a_timeout():
    stream = FakeStream([_event("a"), _event("b")])
    with pytest.raises(UpstreamError) as caught:
        list(read_reply_stream(stream, deadline_seconds=-1))
    assert caught.value.code == "llm_timeout"
    assert stream.closed


def test_stopping_early_closes_the_provider_stream():
    stream = FakeStream([_event("a"), _event("b"), _event("c")])
    gen = read_reply_stream(stream)
    assert next(gen) == StreamChunk(text="a")
    gen.close()  # what happens when the browser presses Stop and the connection drops
    assert stream.closed


def test_stream_deadline_stays_inside_the_dotnet_budget():
    # .NET gives the whole stream 60 s (PythonAiClient). Python must finish, and say so, before that.
    assert llm.STREAM_DEADLINE_SECONDS < 60


def test_validation_is_the_same_as_generate():
    assert client.post("/generate/stream", json={"session_id": "s"}, headers=AUTH).status_code == 422
