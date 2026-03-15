CREATE OR REPLACE FUNCTION check_settlement_has_expense_link()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    settlement_id_to_check uuid;
BEGIN
    IF TG_TABLE_NAME = 'settlements' THEN
        settlement_id_to_check := NEW.id;
    ELSE
        settlement_id_to_check := COALESCE(NEW.settlement_id, OLD.settlement_id);
    END IF;

    IF settlement_id_to_check IS NULL THEN
        RETURN NULL;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM settlements s
        WHERE s.id = settlement_id_to_check
    ) AND NOT EXISTS (
        SELECT 1
        FROM settlement_expenses se
        WHERE se.settlement_id = settlement_id_to_check
    ) THEN
        RAISE EXCEPTION 'Settlement % must link at least one expense', settlement_id_to_check
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger
        WHERE tgname = 'ctrg_settlement_requires_expense_link_on_settlement'
    ) THEN
        CREATE CONSTRAINT TRIGGER ctrg_settlement_requires_expense_link_on_settlement
            AFTER INSERT OR UPDATE ON settlements
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION check_settlement_has_expense_link();
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger
        WHERE tgname = 'ctrg_settlement_requires_expense_link_on_settlement_expense'
    ) THEN
        CREATE CONSTRAINT TRIGGER ctrg_settlement_requires_expense_link_on_settlement_expense
            AFTER UPDATE OF settlement_id OR DELETE ON settlement_expenses
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION check_settlement_has_expense_link();
    END IF;
END $$;
