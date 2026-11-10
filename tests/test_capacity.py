
from pathlib import Path
import sqlite3
import sys

# Allow importing common from the project root.
ROOT_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT_DIR))

from common.transaction_manager import TransactionManager


def main():
    db_path = ROOT_DIR / "data" / "airline.db"
    tm = TransactionManager(db_path)

    print("=== STEP 19: CAPACITY TEST ===")

    # Use a separate transaction for this test.
    transaction_id = "TX002"
    resource_id = "FL001"
    requested_quantity = 8

    result = tm.begin_transaction(transaction_id)
    print("Transaction:", result)

    # Check current capacity before attempting the booking.
    with sqlite3.connect(db_path) as conn:
        total_capacity = conn.execute(
            """
            SELECT total_capacity
            FROM resources
            WHERE resource_id = ?
            """,
            (resource_id,),
        ).fetchone()[0]

        reserved = conn.execute(
            """
            SELECT COALESCE(SUM(quantity), 0)
            FROM reservations
            WHERE resource_id = ?
              AND status IN ('PENDING', 'CONFIRMED')
            """,
            (resource_id,),
        ).fetchone()[0]

    available = total_capacity - reserved

    print("Total capacity:", total_capacity)
    print("Already reserved:", reserved)
    print("Available:", available)
    print("Requested:", requested_quantity)

    # This test expects the request to exceed capacity.
    if requested_quantity <= available:
        print("TEST NOT APPLICABLE: Request does not exceed capacity")
        return

    try:
        reservation = tm.reserve_resource(
            transaction_id,
            resource_id,
            requested_quantity,
        )
        print("Unexpected reservation:", reservation)
        print("TEST FAILED: Overbooking was accepted!")

    except ValueError as error:
        if "Insufficient capacity" in str(error):
            print("Expected rejection:", error)

            # Verify that the rejected request left no reservation.
            with sqlite3.connect(db_path) as conn:
                count = conn.execute(
                    """
                    SELECT COUNT(*)
                    FROM reservations
                    WHERE transaction_id = ?
                      AND resource_id = ?
                    """,
                    (transaction_id, resource_id),
                ).fetchone()[0]

            if count == 0:
                print("TEST PASSED: Overbooking prevented!")
            else:
                print("TEST FAILED: Rejected booking was saved!")
        else:
            print("TEST FAILED: Unexpected error:", error)


if __name__ == "__main__":
    main()
