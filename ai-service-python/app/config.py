"""Environment-driven settings. Loaded once at import time via python-dotenv.

Mirrors backend-dotnet's approach: no secrets hardcoded, everything from the
environment (.env locally, real env vars in any other deployment).
"""
import os

from dotenv import load_dotenv

load_dotenv()


def _require(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(
            f"{name} is not set. Copy env.example to .env and fill it in "
            "(see CLAUDE.md's Configuration section)."
        )
    return value


DEEPINFRA_API_KEY = _require("DEEPINFRA_API_KEY")
DEEPINFRA_BASE_URL = os.environ.get("DEEPINFRA_BASE_URL", "https://api.deepinfra.com/v1/openai")
DEEPINFRA_MODEL = os.environ.get("DEEPINFRA_MODEL", "mistralai/Mistral-Small-24B-Instruct-2501")
# Caps reply length so one long answer can't outlast the timeouts in llm_client.py.
DEEPINFRA_MAX_TOKENS = int(os.environ.get("DEEPINFRA_MAX_TOKENS", "600"))
INTERNAL_API_KEY = _require("INTERNAL_API_KEY")
