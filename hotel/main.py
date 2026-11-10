
from fastapi import FastAPI

# Khoi tao Hotel Resource Manager
app = FastAPI(
    title="Hotel Resource Manager",
    description="Hotel Database Server for Distributed 2PC",
    version="1.0.0"
)


@app.get("/")
def root():
    return {
        "service": "Hotel Resource Manager",
        "message": "Hotel Server is running"
    }


@app.get("/health")
def health_check():
    return {
        "service": "hotel",
        "status": "healthy"
    }
