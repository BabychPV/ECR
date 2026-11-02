-- COLL:defer-health ── ru/kz вичерпаної стелі відкладень у перевірці jobs (Д-2 огляду O1); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.jobs.deferralExhausted', N'ru', N'Фоновые задачи, остановленные по пределу откладывания за последние 24 часа: {count}. Удерживаемый ресурс — в списке задач.'),
    (N'health.jobs.deferralExhausted', N'kz', N'Соңғы 24 сағатта кейінге қалдыру шегі бойынша тоқтатылған фондық тапсырмалар: {count}. Ұсталып тұрған ресурс — тапсырмалар тізімінде.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:defer-health ── кінець секції ──

