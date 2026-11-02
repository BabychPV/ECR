-- COLL:rpt-version-race ── ru/kz: гонка двох створень версії звіту (ECR-RPT-4091.versionConcurrent); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-RPT-4091.versionConcurrent', N'ru', N'Версию с этим номером в этом описании отчёта только что создал кто-то другой. Перечитайте описание и выберите другой номер версии.'),
    (N'err.ECR-RPT-4091.versionConcurrent', N'kz', N'Осы есеп анықтамасында осы нөмірлі нұсқаны жаңа ғана басқа біреу жасады. Анықтаманы қайта жүктеп, басқа нұсқа нөмірін таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rpt-version-race ── кінець секції ──
-- COLL:rpt-version-race ── гонка двох створень версії звіту (UQ_ReportVersion) -> 409; ru/kz - порцією COLL:rpt-version-race нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-RPT-4091.versionConcurrent', N'en', N'Someone else has just created a version with this number in this report definition. Reload the definition and pick another version number.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rpt-version-race ── кінець секції ──
