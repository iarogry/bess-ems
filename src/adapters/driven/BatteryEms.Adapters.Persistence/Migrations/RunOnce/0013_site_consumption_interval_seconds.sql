ALTER TABLE site_consumption_readings
    ADD COLUMN IF NOT EXISTS interval_seconds integer NULL;
