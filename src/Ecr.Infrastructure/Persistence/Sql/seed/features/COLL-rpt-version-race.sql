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
