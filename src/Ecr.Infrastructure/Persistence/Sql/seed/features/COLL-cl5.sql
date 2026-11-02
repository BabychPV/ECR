-- COLL:cl5 ── ru/kz недоставленого зведення збоїв (CL-5); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.jobs.notificationsUndelivered', N'ru', N'Последняя сводка сбоев никому не доставлена: сбоев {count}, отправлено 0. Проверьте настройки почтового сервера, каналы уведомлений и получателей оповещений.'),
    (N'health.jobs.notificationsUndelivered', N'kz', N'Ақаулардың соңғы жиынтығы ешкімге жеткізілмеді: {count} ақау, 0 жіберілді. Пошта серверінің баптауларын, хабарлама арналарын және ескерту алушыларын тексеріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:cl5 ── кінець секції ──

