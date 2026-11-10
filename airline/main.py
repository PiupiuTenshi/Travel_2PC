
import sqlite3
from pathlib import Path

from fastapi import FastAPI

# Thu muc goc cua du an
BASE_DIR = Path(__file__).resolve().parent.parent

# Database rieng cua Airline Server
DB_PATH = BASE_DIR / "data" / "airline.db"

app = FastAPI(
    title="Airline Resource Manager",
    description="Airline Database Server for Distributed 2PC",
    version="1.0.0",
)


@app.get("/")
def root():
    return {
        "service": "Airline Resource Manager",
        "message": "Airline Server is running",
    }


@app.get("/health")
def health_check():
    return {
        "service": "airline",
        "status": "healthy",
    }


@app.get("/resources")
def get_resources():
    if not DB_PATH.is_file():
        return {
            "service": "airline",
            "error": "Database not found",
        }

    with sqlite3.connect(DB_PATH) as conn:
        conn.row_factory = sqlite3.Row

        cursor = conn.execute(
            """
            SELECT resource_id, resource_name, total_capacity
            FROM resources
            ORDER BY resource_id
            """
        )

        resources = [
            dict(row)
            for row in cursor.fetchall()
        ]

    return {
        "service": "airline",
        "resources": resources,
        "count": len(resources),
    }
