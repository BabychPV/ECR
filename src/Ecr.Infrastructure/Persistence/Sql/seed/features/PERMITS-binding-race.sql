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
-- PERMITS: binding-race ── en: паралельна прив'язка тієї самої таблиці аркуша (ECR-PRM-4101 sheetConcurrent; аудит A, PA-A-11); ru/kz — порцією PERMITS: binding-race нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-4101.sheetConcurrent', N'en', N'Table {tableDefId} of this version was just bound by another request: reload the bindings. One sheet table takes one permit.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: binding-race ── кінець секції ──
