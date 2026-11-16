-- B79:dependent-months ── ru/kz: відмова DependentMonthsLocked — від загального стовпця залежать розрахунки затверджених місяців (B79); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'deny.DependentMonthsLocked', N'ru', N'От этого значения зависят расчёты утверждённых месяцев. Верните эти месяцы на редактирование, измените значение и пересчитайте.'),
    (N'deny.DependentMonthsLocked', N'kz', N'Бекітілген айлардың есептеулері осы мәнге тәуелді. Сол айларды өңдеуге қайтарып, мәнді өзгертіңіз де, қайта есептеңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B79:dependent-months ── кінець секції ──

-- B79:dependent-months ── en: відмова DependentMonthsLocked; ru/kz — порцією B79:dependent-months вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'deny.DependentMonthsLocked', N'en', N'Calculations of approved months depend on this value. Return those months for editing, change the value and recalculate.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B79:dependent-months ── кінець секції ──
