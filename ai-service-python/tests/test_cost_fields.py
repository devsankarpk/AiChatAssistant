"""Phase 26: the response carries the token split and the model, so .NET can price a request. Provider faked, no network."""

from types import SimpleNamespace
from unittest.mock import MagicMock

from fastapi.testclient import TestClient

from app.config import DEEPINFRA_MODEL
from app.main import app
from app.services import llm_client as llm

client = TestClient(app)
AUTH = {"X-Internal-Key": "test-internal-key"}
BODY = {"session_id": "s1", "history": [], "prompt": "hi"}


def _completion(prompt, completion):
    usage = SimpleNamespace(total_tokens=prompt + completion, prompt_tokens=prompt, completion_tokens=completion)
    return MagicMock(choices=[MagicMock(message=MagicMock(content="ok"))], usage=usage)


def test_generate_reports_prompt_and_completion_tokens_and_the_model(monkeypatch):
    monkeypatch.setattr(llm._client.chat.completions, "create", lambda **kw: _completion(120, 30))

    body = client.post("/generate", json=BODY, headers=AUTH).json()

    assert body["tokens_used"] == 150
    assert body["prompt_tokens"] == 120
    assert body["completion_tokens"] == 30
    assert body["model"] == DEEPINFRA_MODEL


def test_a_usage_block_without_the_split_gives_zeros_not_an_error(monkeypatch):
    usage = SimpleNamespace(total_tokens=50)  # an older or different provider
    completion = MagicMock(choices=[MagicMock(message=MagicMock(content="ok"))], usage=usage)
    monkeypatch.setattr(llm._client.chat.completions, "create", lambda **kw: completion)

    body = client.post("/generate", json=BODY, headers=AUTH).json()

    assert (body["tokens_used"], body["prompt_tokens"], body["completion_tokens"]) == (50, 0, 0)


def test_garbage_counts_become_zero(monkeypatch):
    usage = SimpleNamespace(total_tokens=5, prompt_tokens="many", completion_tokens=-3)
    completion = MagicMock(choices=[MagicMock(message=MagicMock(content="ok"))], usage=usage)
    monkeypatch.setattr(llm._client.chat.completions, "create", lambda **kw: completion)

    body = client.post("/generate", json=BODY, headers=AUTH).json()

    assert (body["prompt_tokens"], body["completion_tokens"]) == (0, 0)


class _Stream:
    def __init__(self, events):
        self.events = events

    def __iter__(self):
        return iter(self.events)

    def close(self):
        pass


def _delta(text):
    return SimpleNamespace(choices=[SimpleNamespace(delta=SimpleNamespace(content=text))], usage=None)


def test_the_stream_done_event_carries_the_split_and_the_model(monkeypatch):
    usage = SimpleNamespace(total_tokens=90, prompt_tokens=70, completion_tokens=20)
    final = SimpleNamespace(choices=[], usage=usage)
    monkeypatch.setattr(llm._client.chat.completions, "create", lambda **kw: _Stream([_delta("a"), final]))

    text = client.post("/generate/stream", json=BODY, headers=AUTH).text

    done = [b for b in text.strip().split("\n\n") if b.startswith("event: done")][0]
    import json
    data = json.loads(done.split("data: ", 1)[1])
    assert data == {"tokens_used": 90, "prompt_tokens": 70, "completion_tokens": 20, "model": DEEPINFRA_MODEL}
