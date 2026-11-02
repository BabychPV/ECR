-- COLL:rc16-p3 ── ru/kz: Lookup шапки без Registry.View і тост перерахунку; власна порція (попередні порції заповнені до межі 500)
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.header.lookupNoAccess', N'ru', N'Выбрано (название недоступно)'),
    (N'workflow.recalcSkipsSubmitted', N'ru', N'Поданные и утверждённые листы пропускаются.'),
    (N'document.header.lookupNoAccess', N'kz', N'Таңдалған (атауы қолжетімсіз)'),
    (N'workflow.recalcSkipsSubmitted', N'kz', N'Тапсырылған және бекітілген парақтар өткізіліп жіберіледі.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc16-p3 ── кінець секції ──

