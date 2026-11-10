
import sqlite3
from pathlib import Path
from contextlib import contextmanager, closing


class TransactionManager:
    """
    Manages local SQLite access for a Resource Manager.

    Each Resource Manager uses its own database.
    """

    def __init__(self, db_path: str | Path):
        self.db_path = Path(db_path)

        if not self.db_path.is_file():
            raise FileNotFoundError(
                f"Database not found: {self.db_path}"
            )

    
    @contextmanager
    def get_connection(self):
        """
        Open a SQLite connection and always close it.

        Commit on success, rollback on error.
        """
        with closing(
            sqlite3.connect(
                self.db_path,
                timeout=10.0,
            )
        ) as conn:
            conn.row_factory = sqlite3.Row
            conn.execute("PRAGMA foreign_keys = ON")

            with conn:
                yield conn


    def get_transaction(self, transaction_id: str):
        """
        Read a transaction from the local database.

        Returns None if the transaction does not exist.
        """
        with self.get_connection() as conn:
            row = conn.execute(
                """
                SELECT
                    transaction_id,
                    status,
                    created_at,
                    updated_at
                FROM transactions
                WHERE transaction_id = ?
                """,
                (transaction_id,),
            ).fetchone()

            return dict(row) if row else None

    def list_transactions(self):
        """
        Read all local transactions.
        """
        with self.get_connection() as conn:
            rows = conn.execute(
                """
                SELECT
                    transaction_id,
                    status,
                    created_at,
                    updated_at
                FROM transactions
                ORDER BY created_at, transaction_id
                """
            ).fetchall()

            return [dict(row) for row in rows]
    
    def begin_transaction(self, transaction_id: str):
        """
        Create a new local transaction with ACTIVE status.

        Repeated requests with the same transaction_id are
        idempotent: they return the existing transaction.
        """
        if not transaction_id or not transaction_id.strip():
            raise ValueError("transaction_id cannot be empty")

        with self.get_connection() as conn:
            # Acquire SQLite's writer lock before checking.
            conn.execute("BEGIN IMMEDIATE")

            existing = conn.execute(
                """
                SELECT status
                FROM transactions
                WHERE transaction_id = ?
                """,
                (transaction_id,),
            ).fetchone()

            if existing is not None:
                return {
                    "transaction_id": transaction_id,
                    "status": existing["status"],
                    "created": False,
                }

            conn.execute(
                """
                INSERT INTO transactions (
                    transaction_id,
                    status
                )
                VALUES (?, 'ACTIVE')
                """,
                (transaction_id,),
            )

            conn.commit()

            return {
                "transaction_id": transaction_id,
                "status": "ACTIVE",
                "created": True,
            }

    def reserve_resource(
        self,
        transaction_id: str,
        resource_id: str,
        quantity: int,
    ):
        """
        Create a durable PENDING reservation.

        Capacity check and reservation insertion are
        protected by one SQLite write transaction.
        """
        if not transaction_id or not transaction_id.strip():
            raise ValueError("transaction_id cannot be empty")

        if not resource_id or not resource_id.strip():
            raise ValueError("resource_id cannot be empty")

        if type(quantity) is not int or quantity <= 0:
            raise ValueError("quantity must be a positive integer")

        with self.get_connection() as conn:
            conn.execute("BEGIN IMMEDIATE")

            transaction = conn.execute(
                """
                SELECT status
                FROM transactions
                WHERE transaction_id = ?
                """,
                (transaction_id,),
            ).fetchone()

            if transaction is None:
                raise ValueError("Transaction does not exist")

            if transaction["status"] != "ACTIVE":
                raise ValueError(
                    "Transaction is not ACTIVE"
                )

            # Check whether this request was already applied.
            existing = conn.execute(
                """
                SELECT reservation_id, quantity, status
                FROM reservations
                WHERE transaction_id = ?
                  AND resource_id = ?
                """,
                (transaction_id, resource_id),
            ).fetchone()

            if existing is not None:
                if (
                    existing["quantity"] == quantity
                    and existing["status"] == "PENDING"
                ):
                    return {
                        "reservation_id": existing["reservation_id"],
                        "transaction_id": transaction_id,
                        "resource_id": resource_id,
                        "quantity": quantity,
                        "status": "PENDING",
                        "created": False,
                    }

                raise ValueError(
                    "Conflicting reservation already exists"
                )

            resource = conn.execute(
                """
                SELECT total_capacity
                FROM resources
                WHERE resource_id = ?
                """,
                (resource_id,),
            ).fetchone()

            if resource is None:
                raise ValueError("Resource does not exist")

            reserved = conn.execute(
                """
                SELECT COALESCE(SUM(quantity), 0)
                FROM reservations
                WHERE resource_id = ?
                  AND status IN ('PENDING', 'CONFIRMED')
                """,
                (resource_id,),
            ).fetchone()[0]

            available = resource["total_capacity"] - reserved

            if quantity > available:
                raise ValueError(
                    f"Insufficient capacity: "
                    f"requested={quantity}, available={available}"
                )

            # Deterministic ID for safe retries.
            reservation_id = (
                f"{transaction_id}:{resource_id}"
            )

            conn.execute(
                """
                INSERT INTO reservations (
                    reservation_id,
                    transaction_id,
                    resource_id,
                    quantity,
                    status
                )
                VALUES (?, ?, ?, ?, 'PENDING')
                """,
                (
                    reservation_id,
                    transaction_id,
                    resource_id,
                    quantity,
                ),
            )

            conn.commit()

            return {
                "reservation_id": reservation_id,
                "transaction_id": transaction_id,
                "resource_id": resource_id,
                "quantity": quantity,
                "status": "PENDING",
                "created": True,
            }

