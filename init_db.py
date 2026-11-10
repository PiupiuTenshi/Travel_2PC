
import sqlite3
from pathlib import Path

# Thu muc goc cua du an
BASE_DIR = Path(__file__).resolve().parent

# Duong dan schema va databases
SCHEMA_PATH = BASE_DIR / "common" / "schema.sql"
DATA_DIR = BASE_DIR / "data"

DATABASES = {
    "Airline": DATA_DIR / "airline.db",
    "Hotel": DATA_DIR / "hotel.db",
}


def initialize_database(name, db_path, schema):
    # Tao ket noi den database rieng
    with sqlite3.connect(db_path) as conn:
        conn.execute("PRAGMA foreign_keys = ON")
        conn.executescript(schema)

        # Kiem tra cac bang da duoc tao
        cursor = conn.execute("""
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name
        """)

        tables = [row[0] for row in cursor.fetchall()]

    expected = {"resources", "transactions", "reservations"}

    if not expected.issubset(set(tables)):
        raise RuntimeError(
            f"{name}: Missing tables: {expected - set(tables)}"
        )

    print(f"[OK] {name} database: {db_path.name}")
    print(f"     Tables: {', '.join(tables)}")


def main():
    DATA_DIR.mkdir(parents=True, exist_ok=True)

    schema = SCHEMA_PATH.read_text(encoding="utf-8")

    for name, db_path in DATABASES.items():
        initialize_database(name, db_path, schema)

    print("\nAll databases initialized successfully!")


if __name__ == "__main__":
    main()
