CREATE TABLE IF NOT EXISTS "price_series" (
    "series_id" UUID NOT NULL,
    "market_bid_area" TEXT NOT NULL,
    "product" TEXT NOT NULL,
    "price_kind" TEXT NOT NULL,
    "source" TEXT NOT NULL,
    "unit" TEXT NOT NULL,
    "horizon_start" TIMESTAMP WITH TIME ZONE NOT NULL,
    "horizon_end" TIMESTAMP WITH TIME ZONE NOT NULL,
    "time_step_ticks" BIGINT NOT NULL,
    "imported_at" TIMESTAMP WITH TIME ZONE NOT NULL,
    PRIMARY KEY ("series_id"),
    CONSTRAINT "price_series_key" UNIQUE (
        "market_bid_area",
        "product",
        "price_kind",
        "source",
        "horizon_start",
        "horizon_end",
        "time_step_ticks"
    )
);

CREATE TABLE IF NOT EXISTS "price_series_points" (
    "series_id" UUID NOT NULL,
    "position" INTEGER NOT NULL,
    "timestamp" TIMESTAMP WITH TIME ZONE NOT NULL,
    "value" DOUBLE PRECISION NOT NULL,
    CONSTRAINT "price_series_points_series_id_fkey"
        FOREIGN KEY ("series_id") REFERENCES "price_series" ("series_id") ON DELETE CASCADE,
    PRIMARY KEY ("series_id", "position"),
    CONSTRAINT "price_series_points_series_id_timestamp_key" UNIQUE ("series_id", "timestamp")
);

CREATE INDEX IF NOT EXISTS "idx_price_series_lookup" ON "price_series" (
    "market_bid_area",
    "product",
    "price_kind",
    "source",
    "horizon_start",
    "horizon_end",
    "time_step_ticks"
);
