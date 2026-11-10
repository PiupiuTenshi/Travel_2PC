
-- Shared schema for Airline and Hotel Resource Managers.
-- Each server uses its own independent SQLite database.

PRAGMA foreign_keys = ON;

-- Available airline seats or hotel rooms
CREATE TABLE IF NOT EXISTS resources (
    resource_id TEXT PRIMARY KEY,
    resource_name TEXT NOT NULL,
    total_capacity INTEGER NOT NULL
        CHECK (total_capacity >= 0)
);

-- Durable transaction state for 2PC
CREATE TABLE IF NOT EXISTS transactions (
    transaction_id TEXT PRIMARY KEY,
    status TEXT NOT NULL
        CHECK (
            status IN (
                'ACTIVE',
                'PREPARED',
                'COMMITTED',
                'ABORTED'
            )
        ),
    created_at TEXT NOT NULL
        DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL
        DEFAULT CURRENT_TIMESTAMP
);

-- Tentative and confirmed reservations
CREATE TABLE IF NOT EXISTS reservations (
    reservation_id TEXT PRIMARY KEY,
    transaction_id TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    quantity INTEGER NOT NULL
        CHECK (quantity > 0),
    status TEXT NOT NULL
        CHECK (
            status IN (
                'PENDING',
                'CONFIRMED',
                'CANCELLED'
            )
        ),
    created_at TEXT NOT NULL
        DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY (transaction_id)
        REFERENCES transactions(transaction_id),

    FOREIGN KEY (resource_id)
        REFERENCES resources(resource_id),

    UNIQUE (transaction_id, resource_id)
);

CREATE INDEX IF NOT EXISTS idx_reservations_resource
ON reservations(resource_id, status);

CREATE INDEX IF NOT EXISTS idx_reservations_transaction
ON reservations(transaction_id);
