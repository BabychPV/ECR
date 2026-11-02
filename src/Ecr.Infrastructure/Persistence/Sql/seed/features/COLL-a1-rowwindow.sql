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

-- COLL:a1-rowwindow ── підтягування значень PI за вікном рядка: прогрес і вид задачі (HSE301 A1, FEATURE-HSE301-VIEW §4.4) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.kind.rowWindowFetch',       N'en', N'PI row-window fetch', 1),
    (N'jobs.rowWindowSkipped',          N'en', N'Nothing to fetch: the table instance, the open period or an active binding is missing', 1),
    (N'jobs.rowWindowReading',          N'en', N'Reading row windows from the source', 1),
    (N'jobs.rowWindowDone',             N'en', N'Fetched {fetched}, partial {partial}, no data {noData}, kept manual {keptManual}, failed {failed}, invalid window {invalid}, no source {notApplicable}', 1),
    (N'jobs.rowWindowRefetchDone',      N'en', N'Table instances queued for re-fetch: {instances}', 1)
    -- ru/kz — окремою порцією `COLL:a1-rowwindow` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a1-rowwindow ── кінець секції ──
