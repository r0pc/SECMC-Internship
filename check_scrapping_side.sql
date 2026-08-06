SELECT 'CpiObservation' AS TableName, COUNT(*) AS Rows FROM core.CpiObservation
UNION ALL SELECT 'SofrDailyRate',      COUNT(*) FROM core.SofrDailyRate
UNION ALL SELECT 'CollectionRun',      COUNT(*) FROM collect.CollectionRun
UNION ALL SELECT 'RawPayload',         COUNT(*) FROM collect.RawPayload
UNION ALL SELECT 'RejectedObservation',COUNT(*) FROM core.RejectedObservation
UNION ALL SELECT 'DataSource (keep 2)',COUNT(*) FROM collect.DataSource;