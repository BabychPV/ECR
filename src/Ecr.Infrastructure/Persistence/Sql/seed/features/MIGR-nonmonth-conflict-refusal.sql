-- MIGR:nonmonth-conflict-refusal ── ru/kz: LAND-YEAR L3 (B-static), перехід на рік відмовляє, коли якір року порожній, а місяці мають різні значення немісячної комірки; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateNonMonthConflict', N'ru', N'Годовой просмотр показывает немесячные значения первого месяца года, а там их нет, и в более поздних месяцах года эти ячейки заполнены по-разному. Система не выбирает значение молча: введите нужное значение в первом месяце года или выровняйте месяцы (перечень ячеек — в отчёте пробного переноса), затем переносите проект.'),
    (N'err.ECR-SCHM-0422.migrateNonMonthConflict', N'kz', N'Жылдық көрініс жылдың бірінші айының айлық емес мәндерін көрсетеді, бірақ онда олар жоқ, ал жылдың кейінгі айларында бұл ұяшықтар әртүрлі толтырылған. Жүйе мәнді үнсіз таңдамайды: қажетті мәнді жылдың бірінші айында енгізіңіз немесе айларды теңестіріңіз (ұяшықтар тізімі — сынақ көшірудің есебінде), содан кейін жобаны көшіріңіз.'),
    (N'documents.migrateRefusalNonMonthConflict', N'ru', N'Первый месяц года пуст, а месяцы года расходятся в немесячных ячейках'),
    (N'documents.migrateRefusalNonMonthConflict', N'kz', N'Жылдың бірінші айы бос, ал жыл айлары айлық емес ұяшықтарда сәйкес келмейді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MIGR:nonmonth-conflict-refusal ── кінець секції ──

-- MIGR:nonmonth-conflict-refusal ── LAND-YEAR L3 (B-static): якір року без немісячного значення, місяці року не згодні — відмова з переліком комірок; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateNonMonthConflict', N'en', N'The year view shows the non-month values of the first month of the year, which has none, and later months of the year hold different values in those cells. The system does not pick a value silently: enter the value in the first month of the year or make the months agree (the cells are listed in the dry-run report), then move the project.', 1),
    (N'documents.migrateRefusalNonMonthConflict', N'en', N'The first month of the year is empty and the months disagree in non-month cells', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MIGR:nonmonth-conflict-refusal ── кінець секції ──
