DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_expenses_amount_positive'
    ) THEN
        ALTER TABLE expenses DROP CONSTRAINT ck_expenses_amount_positive;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_expenses_amount_non_zero'
    ) THEN
        ALTER TABLE expenses
            ADD CONSTRAINT ck_expenses_amount_non_zero CHECK (amount_cents <> 0);
    END IF;
END $$;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_expense_splits_amount_non_negative'
    ) THEN
        ALTER TABLE expense_splits DROP CONSTRAINT ck_expense_splits_amount_non_negative;
    END IF;
END $$;
