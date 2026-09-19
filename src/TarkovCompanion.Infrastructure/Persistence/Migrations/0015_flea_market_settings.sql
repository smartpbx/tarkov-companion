-- json.tarkov.dev publishes the two flea listing rates beside the items, under data.fleaMarket.
-- Nothing kept them, so the Loot Scan could price an item and never say what a listing returns.
-- One row, replaced by each items refresh, dated so a stale rate is refused like a stale price.
CREATE TABLE flea_market_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    sell_offer_fee_rate REAL NOT NULL CHECK (sell_offer_fee_rate >= 0 AND sell_offer_fee_rate <= 1),
    sell_requirement_fee_rate REAL NOT NULL CHECK (sell_requirement_fee_rate >= 0 AND sell_requirement_fee_rate <= 1),
    observed_utc TEXT NOT NULL
);
