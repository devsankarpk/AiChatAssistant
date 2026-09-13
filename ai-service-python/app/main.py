from fastapi import FastAPI

from app.routes.generate import router as generate_router

app = FastAPI(
    title="AI Chat Assistant - LLM service",
    description="The only component in this project that talks to an LLM provider. "
    "Stateless, no DB, no user concept - authenticated by a shared X-Internal-Key header. "
    "Never exposed to the browser (see CLAUDE.md).",
)

app.include_router(generate_router)


@app.get("/")
async def health() -> dict:
    return {"status": "ok"}
