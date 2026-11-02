-- COLL:a1-rowwindow ── ru/kz підтягування значень PI за вікном рядка (HSE301 A1); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.kind.rowWindowFetch', N'ru', N'Подтягивание окон строк из PI'),
    (N'jobs.rowWindowSkipped', N'ru', N'Подтягивать нечего: нет экземпляра таблицы, открытого периода или активной привязки'),
    (N'jobs.rowWindowReading', N'ru', N'Чтение окон строк из источника'),
    (N'jobs.rowWindowDone', N'ru', N'Подтянуто: {fetched}; неполных: {partial}; без данных: {noData}; сохранено ручных значений: {keptManual}; ошибок: {failed}; недопустимое окно: {invalid}; без источника: {notApplicable}'),
    (N'jobs.rowWindowRefetchDone', N'ru', N'Экземпляров таблиц поставлено на повторное подтягивание: {instances}'),
    (N'jobs.kind.rowWindowFetch', N'kz', N'PI жол терезелерін тарту'),
    (N'jobs.rowWindowSkipped', N'kz', N'Тартатын ештеңе жоқ: кесте данасы, ашық кезең немесе белсенді байланыс жоқ'),
    (N'jobs.rowWindowReading', N'kz', N'Жол терезелерін көзден оқу'),
    (N'jobs.rowWindowDone', N'kz', N'Тартылды: {fetched}; толық емес: {partial}; деректер жоқ: {noData}; сақталған қолмен енгізілген мәндер: {keptManual}; қателер: {failed}; жарамсыз терезе: {invalid}; көзі жоқ: {notApplicable}'),
    (N'jobs.rowWindowRefetchDone', N'kz', N'Қайта тартуға қойылған кесте даналары: {instances}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a1-rowwindow ── кінець секції ──

