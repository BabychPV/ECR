-- PERMITS: binding-race ── ru/kz: паралельна прив'язка тієї самої таблиці аркуша (аудит A, PA-A-11); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-4101.sheetConcurrent', N'ru', N'Таблицу {tableDefId} этой версии только что привязал другой запрос: перечитайте привязки. Одна таблица листа — одно разрешение.'),
    (N'err.ECR-PRM-4101.sheetConcurrent', N'kz', N'Осы нұсқаның {tableDefId} кестесін қазір басқа сұрау байлады: байланыстарды қайта оқыңыз. Бір парақ кестесі — бір рұқсат.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: binding-race ── кінець секції ──
