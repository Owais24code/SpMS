-- An open stock count has no observed quantity yet, so its generated variance
-- is NULL until the count is recorded; the column was declared NOT NULL,
-- which made every new count fail to insert.
ALTER TABLE inventory.stock_count ALTER COLUMN variance_quantity DROP NOT NULL;
COMMENT ON COLUMN inventory.stock_count.variance_quantity IS 'NULL until counted';
