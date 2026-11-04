-- RPT-STABLE: Part B ── перенос версії «з місяця» (en; ru/kz — порцією RPT-STABLE: Part B нижче) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateFromMonthRequired', N'en', N'Nothing was moved: the "From month" migration needs fromPeriodKey (a period of the project, YYYYMM).', 1),
    (N'err.ECR-SCHM-0422.migrateFromMonthInvalid', N'en', N'Nothing was moved: period {periodKey} is not a period of this project.', 1),
    (N'err.ECR-SCHM-0422.migrateFromMonthBeforeSegment', N'en', N'Nothing was moved: period {periodKey} is earlier than {segmentFrom}, where the current template version of the project starts. Choose a period from {segmentFrom} or later.', 1),
    (N'err.ECR-SCHM-0422.migrateWholeProjectMultiVersion', N'en', N'Nothing was moved: the project already uses several template versions by period, so a whole-project migration is unavailable. Use "From month" with a period not earlier than the start of the current version.', 1),
    (N'err.ECR-SCHM-0422.migrateTargetNotPublished', N'en', N'Nothing was moved: the target template version is not published.', 1),
    (N'err.ECR-SCHM-0422.migrateFromMonthDisabled', N'en', N'Nothing was moved: the "From month" migration is not enabled yet. Only the whole-project migration is available.', 1),
    (N'documents.migrateRangeLabel', N'en', N'Scope', 1),
    (N'documents.migrateRangeWhole', N'en', N'Whole project, any time', 1),
    (N'documents.migrateRangeFromMonth', N'en', N'From month', 1),
    (N'documents.migrateFromMonthLabel', N'en', N'First month on the new version', 1),
    (N'documents.migrateWarnEffectiveFromJanuary', N'en', N'The project has a table with months in columns, so the whole year moves to the new version: the migration starts from January ({effective}) instead of {requested}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RPT-STABLE: Part B ── кінець секції en ──
