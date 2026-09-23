import os

# app.config._require() raises at *import* time if these aren't set, before any test gets a
# chance to run. Fake values here mean tests never depend on (or accidentally use) a real
# DeepInfra key - every LLM call in test_generate.py is mocked, per CLAUDE.md's testing
# expectations ("Do not make real LLM calls in tests").
#
# setdefault, not direct assignment: if these are already set in the environment (e.g. someone
# running pytest with a real .env sourced into their shell), that value wins - these are only a
# fallback so tests work standalone with no .env file present at all.
os.environ.setdefault("DEEPINFRA_API_KEY", "test-deepinfra-key")
os.environ.setdefault("INTERNAL_API_KEY", "test-internal-key")
