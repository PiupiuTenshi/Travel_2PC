
import sqlite3

conn = sqlite3.connect("data/airline.db")

try:
    cursor = conn.execute(
        """
        SELECT reservation_id, quantity, status
        FROM reservations
        WHERE transaction_id = ?
        """,
        ("TX001",),
    )

    rows = cursor.fetchall()

    print("Reservations:", rows)
    print("Number of records:", len(rows))

finally:
    conn.close()
