"""Per CLAUDE.md's testing expectations: mocks the LLM SDK, never calls a real LLM. Everything
here patches app.services.llm_client's module-level OpenAI client, then drives the real FastAPI
app through TestClient - so the auth dependency, request/response schemas, and error mapping are
all exercised for real, only the network boundary to DeepInfra is faked.
"""

from unittest.mock import MagicMock

import httpx
from fastapi.testclient import TestClient
from openai import APIConnectionError, APIStatusError, APITimeoutError, RateLimitError

from app.main import app
from app.services import llm_client as llm_client_module

client = TestClient(app)
INTERNAL_KEY = "test-internal-key"  # matches conftest.py
AUTH_HEADER = {"X-Internal-Key": INTERNAL_KEY}


def _mock_completion(content: str, total_tokens: int) -> MagicMock:
    message = MagicMock(content=content)
    choice = MagicMock(message=message)
    return MagicMock(choices=[choice], usage=MagicMock(total_tokens=total_tokens))


def _fake_response(status_code: int) -> httpx.Response:
    return httpx.Response(status_code=status_code, request=httpx.Request("POST", "https://deepinfra.example/x"))


def test_missing_internal_key_returns_401():
    response = client.post("/generate", json={"session_id": "s1", "history": [], "prompt": "hi"})

    assert response.status_code == 401
    assert response.json()["detail"]["error"]["code"] == "unauthorized"


def test_wrong_internal_key_returns_401():
    response = client.post(
        "/generate",
        json={"session_id": "s1", "history": [], "prompt": "hi"},
        headers={"X-Internal-Key": "not-the-real-key"},
    )

    assert response.status_code == 401


def test_returns_reply(monkeypatch):
    monkeypatch.setattr(
        llm_client_module._client.chat.completions,
        "create",
        lambda **kwargs: _mock_completion("Hello there!", 42),
    )

    response = client.post(
        "/generate",
        json={
            "session_id": "s1",
            "history": [{"role": "user", "content": "Hi"}, {"role": "assistant", "content": "Hello"}],
            "prompt": "How are you?",
        },
        headers=AUTH_HEADER,
    )

    assert response.status_code == 200
    assert response.json() == {"reply": "Hello there!", "tokens_used": 42}


def test_sends_full_history_plus_prompt_as_the_final_user_message(monkeypatch):
    captured: dict = {}

    def fake_create(**kwargs):
        captured.update(kwargs)
        return _mock_completion("ok", 1)

    monkeypatch.setattr(llm_client_module._client.chat.completions, "create", fake_create)

    client.post(
        "/generate",
        json={
            "session_id": "s1",
            "history": [{"role": "user", "content": "My name is Sankar."}],
            "prompt": "What is my name?",
        },
        headers=AUTH_HEADER,
    )

    assert captured["messages"] == [
        {"role": "user", "content": "My name is Sankar."},
        {"role": "user", "content": "What is my name?"},
    ]


def test_empty_reply_from_the_llm_becomes_an_empty_string_not_an_error(monkeypatch):
    choice = MagicMock(message=MagicMock(content=None))
    monkeypatch.setattr(
        llm_client_module._client.chat.completions,
        "create",
        lambda **kwargs: MagicMock(choices=[choice], usage=MagicMock(total_tokens=5)),
    )

    response = client.post(
        "/generate", json={"session_id": "s1", "history": [], "prompt": "hi"}, headers=AUTH_HEADER
    )

    assert response.status_code == 200
    assert response.json()["reply"] == ""


def test_timeout_maps_to_503(monkeypatch):
    def raise_timeout(**kwargs):
        raise APITimeoutError(request=httpx.Request("POST", "https://deepinfra.example/x"))

    monkeypatch.setattr(llm_client_module._client.chat.completions, "create", raise_timeout)

    response = client.post(
        "/generate", json={"session_id": "s1", "history": [], "prompt": "hi"}, headers=AUTH_HEADER
    )

    assert response.status_code == 503
    assert response.json()["detail"]["error"]["code"] == "llm_timeout"


def test_connection_error_maps_to_503(monkeypatch):
    def raise_connection_error(**kwargs):
        raise APIConnectionError(request=httpx.Request("POST", "https://deepinfra.example/x"))

    monkeypatch.setattr(llm_client_module._client.chat.completions, "create", raise_connection_error)

    response = client.post(
        "/generate", json={"session_id": "s1", "history": [], "prompt": "hi"}, headers=AUTH_HEADER
    )

    assert response.status_code == 503
    assert response.json()["detail"]["error"]["code"] == "llm_timeout"


def test_rate_limit_maps_to_503(monkeypatch):
    def raise_rate_limit(**kwargs):
        raise RateLimitError("slow down", response=_fake_response(429), body=None)

    monkeypatch.setattr(llm_client_module._client.chat.completions, "create", raise_rate_limit)

    response = client.post(
        "/generate", json={"session_id": "s1", "history": [], "prompt": "hi"}, headers=AUTH_HEADER
    )

    assert response.status_code == 503
    assert response.json()["detail"]["error"]["code"] == "llm_rate_limited"


def test_other_upstream_error_maps_to_502_not_a_crash(monkeypatch):
    def raise_upstream_error(**kwargs):
        raise APIStatusError("model not found", response=_fake_response(404), body=None)

    monkeypatch.setattr(llm_client_module._client.chat.completions, "create", raise_upstream_error)

    response = client.post(
        "/generate", json={"session_id": "s1", "history": [], "prompt": "hi"}, headers=AUTH_HEADER
    )

    assert response.status_code == 502
    assert response.json()["detail"]["error"]["code"] == "llm_upstream_error"


def test_unexpected_exception_maps_to_502_never_a_bare_500(monkeypatch):
    def raise_unexpected(**kwargs):
        raise ValueError("something nobody anticipated")

    monkeypatch.setattr(llm_client_module._client.chat.completions, "create", raise_unexpected)

    response = client.post(
        "/generate", json={"session_id": "s1", "history": [], "prompt": "hi"}, headers=AUTH_HEADER
    )

    assert response.status_code == 502
    assert response.json()["detail"]["error"]["code"] == "llm_unexpected_error"
