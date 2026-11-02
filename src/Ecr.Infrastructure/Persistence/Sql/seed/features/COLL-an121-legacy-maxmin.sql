-- COLL:an121-legacy-maxmin ── ru/kz: AN-121 / C1-01: Max/Min з цілим першим аргументом у Legacy; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'publish.warning.legacyIntegerMaxMin', N'ru', N'Формула {formula}: у {functions} первый аргумент целый, а второй может быть дробным. Прежняя система (NCalc 1.3.8) округляла здесь второй аргумент до целого, ECR — нет: результаты могут отличаться от поданных форм на целые единицы. Запишите первый аргумент с десятичной точкой (0.0), чтобы намерение было явным.'),
    (N'publish.warning.legacyIntegerMaxMin', N'kz', N'{formula} формуласы: {functions} ішінде бірінші аргумент бүтін, ал екіншісі бөлшек болуы мүмкін. Бұрынғы жүйе (NCalc 1.3.8) мұнда екінші аргументті бүтінге дейін дөңгелектеген, ECR дөңгелектемейді: нәтижелер тапсырылған формалардан бүтін бірліктерге өзгеше болуы мүмкін. Ниетті анық көрсету үшін бірінші аргументті ондық нүктемен (0.0) жазыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an121-legacy-maxmin ── кінець секції ──

-- COLL:an121-legacy-maxmin ── AN-121 / C1-01 (HU-14 Q1): попередження публікації Legacy — Max/Min з цілим першим аргументом (NCalc округлював другий); ru/kz — порцією COLL:an121-legacy-maxmin нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'publish.warning.legacyIntegerMaxMin', N'en', N'Formula {formula}: {functions} has an integer first argument and a possibly fractional second one. The legacy system (NCalc 1.3.8) rounded the second argument to an integer here, ECR does not: results may differ from the submitted forms by whole units. Write the first argument with a decimal point (0.0) to make the intent explicit.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an121-legacy-maxmin ── кінець секції ──
