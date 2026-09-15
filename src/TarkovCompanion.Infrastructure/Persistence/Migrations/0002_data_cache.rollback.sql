DROP TABLE IF EXISTS http_response_cache;
DROP INDEX IF EXISTS idx_items_normalized_short_name;
ALTER TABLE items DROP COLUMN high_24h_price;
ALTER TABLE items DROP COLUMN low_24h_price;
ALTER TABLE items DROP COLUMN normalized_short_name;
