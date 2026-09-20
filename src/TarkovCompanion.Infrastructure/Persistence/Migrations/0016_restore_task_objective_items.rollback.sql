-- 0016 only puts back rows that 0013 deleted; the schema is the same before and after it, and
-- deleting the rows again would repeat the damage, so there is nothing to undo.
SELECT 1 WHERE 0;
