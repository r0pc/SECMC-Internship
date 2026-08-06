USE SECMC_INTERN_DATABASE;

BEGIN TRANSACTION;

-- Fact tables first: their FKs to CollectionRun are NO ACTION, so the runs
-- cannot be deleted while these rows point at them. Not referenced by anything
-- themselves, so TRUNCATE is allowed and resets IDENTITY.
TRUNCATE TABLE core.CpiObservation;
TRUNCATE TABLE core.SofrDailyRate;

-- Cascades to collect.RawPayload and core.RejectedObservation. DELETE, not
-- TRUNCATE: SQL Server refuses to truncate an FK-referenced table even when
-- every child row is already gone.
DELETE FROM collect.CollectionRun;

COMMIT;

-- Optional: restart run ids at 1.
DBCC CHECKIDENT ('collect.CollectionRun', RESEED, 0);