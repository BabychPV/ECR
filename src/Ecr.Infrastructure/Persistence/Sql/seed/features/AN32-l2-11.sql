-- AN32:L2-11 ── ru/kz черги (перезапуск, коли на ціль уже чекає інша задача); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-JOB-0409.restartCoveredBy', N'ru', N'Задача {jobId} не перезапущена: на ту же цель уже ожидает задача {coveredBy}, она и выполнит работу.'),
    (N'err.ECR-JOB-0409.restartCoveredBy', N'kz', N'{jobId} тапсырмасы қайта іске қосылмады: сол нысанаға {coveredBy} тапсырмасы кезекте күтуде, жұмысты сол орындайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- AN32:L2-11 ── кінець секції ──
-- AN32:L2-11 ── черга: перезапуск, коли на ціль уже чекає інша задача; ru/kz — порцією AN32:L2-11 у блоці I18N нижче
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-JOB-0409.restartCoveredBy', N'en', N'Job {jobId} was not restarted: job {coveredBy} is already queued for the same target and will do this work.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- AN32:L2-11 ── кінець секції ──
