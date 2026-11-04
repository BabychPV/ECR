-- HSE301:A7 ── SQL-джерело: мітка часу точки (аудит A7). Рядок із непрочитаною міткою
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- або мітки не по черзі — відмова інтервалу, а не мовчазний пропуск із повним покриттям.
    (N'err.ECR-INT-0422.timestampUnreadable', N'en', N'The value query of source "{dataSource}" returned Ts of type {valueType} for "{sourcePath}". Only datetimeoffset or a date/time type without offset (read as UTC) can be used, so this interval is not recorded as collected.', 1),
    (N'err.ECR-INT-0422.timestampsOutOfOrder', N'en', N'The value query of source "{dataSource}" returns rows for "{sourcePath}" out of time order ({current} after {previous}). Add ORDER BY on the timestamp to the query: without it the unread tail of a batch cannot be determined.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:A7 ── кінець секції
