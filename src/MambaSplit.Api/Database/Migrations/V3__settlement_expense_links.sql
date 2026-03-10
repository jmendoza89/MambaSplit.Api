CREATE TABLE IF NOT EXISTS settlement_expenses (
    id uuid PRIMARY KEY,
    settlement_id uuid NOT NULL,
    expense_id uuid NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_settlement_expenses_expense_id ON settlement_expenses (expense_id);
CREATE INDEX IF NOT EXISTS ix_settlement_expenses_settlement_id ON settlement_expenses (settlement_id);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_settlement_expenses_settlements_settlement_id'
    ) THEN
        ALTER TABLE settlement_expenses
            ADD CONSTRAINT fk_settlement_expenses_settlements_settlement_id
                FOREIGN KEY (settlement_id) REFERENCES settlements (id) ON DELETE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_settlement_expenses_expenses_expense_id'
    ) THEN
        ALTER TABLE settlement_expenses
            ADD CONSTRAINT fk_settlement_expenses_expenses_expense_id
                FOREIGN KEY (expense_id) REFERENCES expenses (id) ON DELETE CASCADE;
    END IF;
END $$;
