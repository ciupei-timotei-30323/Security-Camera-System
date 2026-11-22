-- Factory-issued camera kit administrator accounts.
-- Run through `npm run seed`, which creates this table and upserts the local sample kits.
CREATE TABLE IF NOT EXISTS factory_kits (
    id             SERIAL PRIMARY KEY,
    system_id      TEXT UNIQUE NOT NULL,
    admin_username TEXT UNIQUE NOT NULL,
    password_hash  TEXT NOT NULL,
    pi_api_key     TEXT UNIQUE NOT NULL,
    is_active      BOOLEAN NOT NULL DEFAULT TRUE,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
