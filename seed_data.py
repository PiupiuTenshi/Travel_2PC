
import sqlite3
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent
DATA_DIR = BASE_DIR / "data"

AIRLINE_RESOURCES = [
    ("FL001", "Flight Hanoi - HCMC", 10),
    ("FL002", "Flight HCMC - Da Nang", 8),
    ("FL003", "Flight Da Nang - Hanoi", 12),
]

HOTEL_RESOURCES = [
    ("ROOM_STANDARD", "Standard Room", 5),
    ("ROOM_DELUXE", "Deluxe Room", 3),
    ("ROOM_SUITE", "Suite Room", 2),
]


def seed_database(db_path, resources):
    if not db_path.is_file():
        raise FileNotFoundError(
            f"Database not found: {db_path}. Run init_db.py first."
        )

    with sqlite3.connect(db_path) as conn:
        conn.execute("PRAGMA foreign_keys = ON")

        # Insert sample resources without creating duplicates.
        conn.executemany(
            """
            INSERT OR IGNORE INTO resources
                (resource_id, resource_name, total_capacity)
            VALUES (?, ?, ?)
            """,
            resources,
        )

        # Read all resources to verify the result.
        rows = conn.execute(
            """
            SELECT resource_id, resource_name, total_capacity
            FROM resources
            ORDER BY resource_id
            """
        ).fetchall()

    return rows


def main():
    databases = [
        ("Airline", DATA_DIR / "airline.db", AIRLINE_RESOURCES),
        ("Hotel", DATA_DIR / "hotel.db", HOTEL_RESOURCES),
    ]

    for name, db_path, resources in databases:
        rows = seed_database(db_path, resources)

        print(f"\n[OK] {name}: {len(rows)} resources")
        for resource_id, resource_name, capacity in rows:
            print(f"  {resource_id}: {resource_name} ({capacity})")

    print("\nSample data initialized successfully!")


if __name__ == "__main__":
    main()
