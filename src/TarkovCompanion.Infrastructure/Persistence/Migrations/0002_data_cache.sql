ALTER TABLE items ADD COLUMN normalized_short_name TEXT NOT NULL DEFAULT '';
ALTER TABLE items ADD COLUMN low_24h_price INTEGER;
ALTER TABLE items ADD COLUMN high_24h_price INTEGER;

CREATE INDEX idx_items_normalized_short_name ON items(normalized_short_name);

CREATE TABLE http_response_cache (
    cache_key TEXT PRIMARY KEY,
    body_json TEXT NOT NULL,
    cached_utc TEXT NOT NULL,
    etag TEXT,
    last_modified TEXT
);
