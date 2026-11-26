-- REQIN:banner ── ru/kz: попередження «обов'язкові колонки методології» в сітці — кнопка в рядку інструментів, перелік на вимогу: одна колонка = один рядок, назва колонки, номери рядків (живий прохід 25.11); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.requiredInput.summary', N'ru', N'Не заполнено обязательных столбцов: {count} · сохранению не мешает'),
    (N'grid.requiredInput.summary', N'kz', N'{count} міндетті баған толтырылмаған · сақтауға кедергі келтірмейді'),
    (N'grid.requiredInput.listLabel', N'ru', N'Незаполненные обязательные столбцы'),
    (N'grid.requiredInput.listLabel', N'kz', N'Толтырылмаған міндетті бағандар'),
    (N'grid.requiredInput.line', N'ru', N'{column} · строки: {rows} · требуется для {methodologies}'),
    (N'grid.requiredInput.line', N'kz', N'{column} · жолдар: {rows} · {methodologies} үшін қажет'),
    (N'grid.requiredInput.lineNoMethodology', N'ru', N'{column} · строки: {rows}'),
    (N'grid.requiredInput.lineNoMethodology', N'kz', N'{column} · жолдар: {rows}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REQIN:banner ── кінець секції ──

-- REQIN:banner ── en: попередження «обов'язкові колонки методології» в сітці; ru/kz — порцією REQIN:banner вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.requiredInput.summary', N'en', N'{count} required column(s) not filled yet · saving is not blocked', 1),
    (N'grid.requiredInput.listLabel', N'en', N'Required columns not filled', 1),
    (N'grid.requiredInput.line', N'en', N'{column} · rows: {rows} · needed by {methodologies}', 1),
    (N'grid.requiredInput.lineNoMethodology', N'en', N'{column} · rows: {rows}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REQIN:banner ── кінець секції ──

-- REQIN:banner ── прибрані ключі: розгорнутий заголовок «{count} required column(s) missing (does not block saving)» замінено згорнутим рядком grid.requiredInput.summary ──
INSERT INTO #UiRemoved ([Key], Lang, OldVal)
SELECT v.[Key], v.Lang, v.OldVal
  FROM (VALUES
    (N'grid.requiredInputWarningTitle', N'en', N'{count} required column(s) missing (does not block saving)'),
    (N'grid.requiredInputWarningTitle', N'ru', N'Не заполнено обязательных столбцов: {count} (сохранению не мешает)'),
    (N'grid.requiredInputWarningTitle', N'kz', N'{count} міндетті баған толтырылмаған (сақтауға кедергі келтірмейді)')
       ) AS v ([Key], Lang, OldVal)
OPTION (RECOMPILE);
GO
-- REQIN:banner ── кінець секції ──
