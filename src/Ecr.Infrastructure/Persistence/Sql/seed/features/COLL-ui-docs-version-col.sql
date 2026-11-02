-- COLL:ui-docs-version-col ── ru/kz: колонка «Version» переліку документів; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.versionColumn', N'ru', N'Версия'),
    (N'documents.versionColumn', N'kz', N'Нұсқа'),
    (N'documents.versionValue', N'ru', N'v{version}'),
    (N'documents.versionValue', N'kz', N'v{version}'),
    (N'documents.versionUnknown', N'ru', N'Версия шаблона не показана'),
    (N'documents.versionUnknown', N'kz', N'Үлгі нұсқасы көрсетілмейді'),
    (N'documents.versionForPeriod', N'ru', N'Версия шаблона за {period}'),
    (N'documents.versionForPeriod', N'kz', N'{period} үшін үлгі нұсқасы'),
    (N'documents.versionSortBy', N'ru', N'Сортировать по версии (документы этой страницы)'),
    (N'documents.versionSortBy', N'kz', N'Нұсқа бойынша сұрыптау (осы беттегі құжаттар)'),
    (N'documents.versionSortedAsc', N'ru', N'по возрастанию'),
    (N'documents.versionSortedAsc', N'kz', N'өсу бойынша'),
    (N'documents.versionSortedDesc', N'ru', N'по убыванию'),
    (N'documents.versionSortedDesc', N'kz', N'кему бойынша'),
    (N'documents.versionAll', N'ru', N'Все версии'),
    (N'documents.versionAll', N'kz', N'Барлық нұсқалар'),
    (N'documents.versionFilterHint', N'ru', N'Фильтрует документы, загруженные на этой странице'),
    (N'documents.versionFilterHint', N'kz', N'Осы бетке жүктелген құжаттарды сүзеді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-docs-version-col ── кінець секції ──
