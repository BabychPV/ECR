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
