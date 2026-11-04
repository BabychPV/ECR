-- COLL:ui-docs ── ru/kz UI-19/UI-29/UI-18: перелік документів і швидкий перегляд; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'segments.approvedOf', N'ru', N'{done} из {total} утверждено'),
    (N'segments.approvedOf', N'kz', N'{total} ішінен {done} бекітілді'),
    (N'documents.document', N'ru', N'Документ'),
    (N'documents.document', N'kz', N'Құжат'),
    (N'documents.issues', N'ru', N'Ошибки'),
    (N'documents.issues', N'kz', N'Қателер'),
    (N'documents.issuesHint', N'ru', N'Открытых ошибок проверки: {count}'),
    (N'documents.issuesHint', N'kz', N'Ашық тексеру қателері: {count}'),
    (N'documents.updated', N'ru', N'Изменён'),
    (N'documents.updated', N'kz', N'Өзгертілді'),
    (N'documents.quickLookColumn', N'ru', N'Быстрый просмотр'),
    (N'documents.quickLookColumn', N'kz', N'Жылдам қарау'),
    (N'documents.quickLook', N'ru', N'Быстрый просмотр {key}'),
    (N'documents.quickLook', N'kz', N'{key} — жылдам қарау'),
    (N'documents.quickLookClose', N'ru', N'Закрыть быстрый просмотр'),
    (N'documents.quickLookClose', N'kz', N'Жылдам қарауды жабу'),
    (N'documents.openDocument', N'ru', N'Открыть документ'),
    (N'documents.openDocument', N'kz', N'Құжатты ашу'),
    (N'documents.quickLookNoPeriod', N'ru', N'Выберите период, чтобы увидеть состояния листов и заполненность таблиц.'),
    (N'documents.quickLookNoPeriod', N'kz', N'Парақтардың күйін және кестелердің толтырылуын көру үшін кезеңді таңдаңыз.'),
    (N'documents.quickLookResponsible', N'ru', N'Ответственные'),
    (N'documents.quickLookResponsible', N'kz', N'Жауаптылар'),
    (N'documents.lastChangedBy', N'ru', N'Последним изменил'),
    (N'documents.lastChangedBy', N'kz', N'Соңғы өзгерткен'),
    (N'documents.quickLookTablesFilled', N'ru', N'Заполнено таблиц: {filled} из {total}'),
    (N'documents.quickLookTablesFilled', N'kz', N'Толтырылған кестелер: {filled} / {total}'),
    (N'documents.quickLookLatestChanges', N'ru', N'Последние изменения'),
    (N'documents.quickLookLatestChanges', N'kz', N'Соңғы өзгерістер'),
    (N'documents.clearFilters', N'ru', N'Сбросить фильтры'),
    (N'documents.clearFilters', N'kz', N'Сүзгілерді тазалау')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-docs ── кінець секції ──

-- COLL:ui-docs ── UI-19/UI-29/UI-18: колонки переліку документів, смужка аркушів, швидкий перегляд, Clear filters; ru/kz — порцією COLL:ui-docs у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'segments.approvedOf', N'en', N'{done} of {total} approved', 1),
    (N'documents.document', N'en', N'Document', 1),
    (N'documents.issues', N'en', N'Issues', 1),
    (N'documents.issuesHint', N'en', N'Open validation errors: {count}', 1),
    (N'documents.updated', N'en', N'Updated', 1),
    (N'documents.quickLookColumn', N'en', N'Quick look', 1),
    (N'documents.quickLook', N'en', N'Quick look at {key}', 1),
    (N'documents.quickLookClose', N'en', N'Close quick look', 1),
    (N'documents.openDocument', N'en', N'Open document', 1),
    (N'documents.quickLookNoPeriod', N'en', N'Choose a period to see sheet states and filled tables.', 1),
    (N'documents.quickLookResponsible', N'en', N'Responsible', 1),
    (N'documents.lastChangedBy', N'en', N'Last changed by', 1),
    (N'documents.quickLookTablesFilled', N'en', N'{filled} of {total} tables filled', 1),
    (N'documents.quickLookLatestChanges', N'en', N'Latest changes', 1),
    (N'documents.clearFilters', N'en', N'Clear filters', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-docs ── кінець секції ──
