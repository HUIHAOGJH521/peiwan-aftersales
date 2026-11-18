from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    database_url: str = "postgresql+asyncpg://peiwan:peiwan_dev_pass@localhost:5432/peiwan"
    redis_url: str = "redis://localhost:6379/0"
    s3_endpoint: str = "http://localhost:9000"
    s3_access_key: str = "minioadmin"
    s3_secret_key: str = "minioadmin123"
    s3_bucket: str = "peiwan-media"
    jwt_secret: str = "dev-change-me-in-production"
    jwt_algorithm: str = "HS256"

    model_config = {"env_file": ".env", "extra": "ignore"}


settings = Settings()
