
import sqlite3
from contextlib import closing
import sys
import tempfile
import threading
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT_DIR))

from common.transaction_manager import TransactionManager


def run_test():
    print("=== STEP 20: CONCURRENCY TEST ===")

    # Temporary database: production data is not modified.
    with tempfile.TemporaryDirectory() as temp_dir:
        db_path = Path(temp_dir) / "concurrency.db"
        schema_path = ROOT_DIR / "common" / "schema.sql"

        with closing(sqlite3.connect(db_path)) as conn:
            conn.executescript(
                schema_path.read_text(encoding="utf-8")
            )
            conn.execute(
                """
                INSERT INTO resources (
                    resource_id,
                    resource_name,
                    total_capacity
                )
                VALUES (?, ?, ?)
                """,
                ("TEST_FLIGHT", "Concurrency Test Flight", 10),
            )
            conn.commit()

        tm = TransactionManager(db_path)

        tm.begin_transaction("TX_A")
        tm.begin_transaction("TX_B")

        # Both workers wait until they can start together.
        barrier = threading.Barrier(2)

        def reserve(transaction_id):
            barrier.wait()

            try:
                result = tm.reserve_resource(
                    transaction_id,
                    "TEST_FLIGHT",
                    7,
                )
                return transaction_id, "SUCCESS", result

            except ValueError as error:
                return transaction_id, "REJECTED", str(error)

        with ThreadPoolExecutor(max_workers=2) as executor:
            future_a = executor.submit(reserve, "TX_A")
            future_b = executor.submit(reserve, "TX_B")

            results = [
                future_a.result(),
                future_b.result(),
            ]

        for transaction_id, status, detail in results:
            print(f"{transaction_id}: {status}")
            print(f"  Detail: {detail}")

        with closing(sqlite3.connect(db_path)) as conn:
            reserved = conn.execute(
                """
                SELECT COALESCE(SUM(quantity), 0)
                FROM reservations
                WHERE resource_id = ?
                  AND status IN ('PENDING', 'CONFIRMED')
                """,
                ("TEST_FLIGHT",),
            ).fetchone()[0]

        success_count = sum(
            status == "SUCCESS"
            for _, status, _ in results
        )

        rejected_count = sum(
            status == "REJECTED"
            and "Insufficient capacity" in str(detail)
            for _, status, detail in results
        )

        print("Total reserved:", reserved)
        print("Successful transactions:", success_count)
        print("Rejected transactions:", rejected_count)

        if (
            reserved == 7
            and success_count == 1
            and rejected_count == 1
        ):
            print("TEST PASSED: No overbooking under concurrency!")
        else:
            print("TEST FAILED: Unexpected concurrency result!")


if __name__ == "__main__":
    run_test()
