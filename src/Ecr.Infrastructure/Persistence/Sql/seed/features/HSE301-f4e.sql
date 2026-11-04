-- HSE301:F4e ── події джерела через PI SQL Client (FEATURE-HSE301-VIEW §4.7.2):
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- запиту подій немає — стан «не налаштовано», а не порожній список; рядок подій без
    -- ідентифікатора чи з нечитабельним часом — відмова вікна, а не мовчазний пропуск.
    (N'err.ECR-INT-0422.eventQueryNotConfigured', N'en', N'No {queryKind} query is configured for source "{dataSource}": set {sourceConfigKey} (or the shared {configKey}) in the environment settings.', 1),
    (N'err.ECR-INT-0422.eventTimestampUnreadable', N'en', N'The event query of source "{dataSource}" returned {field} of type {valueType} for event "{eventId}". Only datetimeoffset or a date/time type without offset (read as UTC) can be used, so the events of this window are not read.', 1),
    (N'err.ECR-INT-0422.eventIdMissing', N'en', N'The event query of source "{dataSource}" returned a row without EventId. Without it an event cannot be matched to a document row, so the events of this window are not read: fix the event query in the environment settings.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:F4e ── кінець секції
