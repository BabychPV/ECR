-- COLL:docs-list-reopened ── ru/kz: перелік документів — місяць, відкритий для ОДНОГО документа (смуга року, легенда, фільтр); слово людини 10.11 ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.strip.reopened', N'ru', N'Открыт для этого документа'),
    (N'documents.strip.reopened', N'kz', N'Осы құжат үшін ашылған'),
    (N'documents.strip.reopenedUntil', N'ru', N'Открыт для этого документа до {until}'),
    (N'documents.strip.reopenedUntil', N'kz', N'Осы құжат үшін {until} дейін ашылған'),
    (N'documents.filterReopenedMonths', N'ru', N'Переоткрытые месяцы'),
    (N'documents.filterReopenedMonths', N'kz', N'Қайта ашылған айлар')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:docs-list-reopened ── кінець секції ──

-- COLL:docs-list-reopened ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.strip.reopened', N'en', N'Reopened for this document', 1),
    (N'documents.strip.reopenedUntil', N'en', N'Reopened for this document until {until}', 1),
    (N'documents.filterReopenedMonths', N'en', N'Reopened months', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:docs-list-reopened ── кінець секції ──
