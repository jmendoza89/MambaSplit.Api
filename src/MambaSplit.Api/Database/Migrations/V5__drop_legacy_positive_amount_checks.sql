DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'expenses_amount_cents_check'
    ) THEN
        ALTER TABLE expenses DROP CONSTRAINT expenses_amount_cents_check;
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'expense_splits_amount_owed_cents_check'
    ) THEN
        ALTER TABLE expense_splits DROP CONSTRAINT expense_splits_amount_owed_cents_check;
    END IF;
END $$;
