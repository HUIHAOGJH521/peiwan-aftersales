from fastapi import FastAPI

from app.aftersales_api import router as aftersales_router
from app.errors import install_error_handlers

app = FastAPI(title="Peiwan Aftersales API", version="0.1.0")
install_error_handlers(app)
app.include_router(aftersales_router)


@app.get("/healthz")
async def healthz() -> dict[str, str]:
    return {"status": "ok"}
