-- DOCUX:ui-review ── ru/kz: огляд UI всіх сторінок меню (координатор 27.11, lane/integ-docux-polish); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'filters.all', N'ru', N'Все'),
    (N'filters.all', N'kz', N'Барлығы'),
    (N'templates.stateAll', N'ru', N'Все состояния'),
    (N'templates.stateAll', N'kz', N'Барлық күйлер'),
    (N'permits.types.filter.categoryAll', N'ru', N'Все категории'),
    (N'permits.types.filter.categoryAll', N'kz', N'Барлық санаттар'),
    (N'units.dimensionAll', N'ru', N'Все размерности'),
    (N'units.dimensionAll', N'kz', N'Барлық өлшемдер'),
    (N'methodologies.search', N'ru', N'Поиск методологий'),
    (N'methodologies.search', N'kz', N'Әдістемелерді іздеу'),
    (N'methodologies.searchPlaceholder', N'ru', N'Код или название'),
    (N'methodologies.searchPlaceholder', N'kz', N'Код немесе атауы'),
    (N'methodologies.searchNoMatches', N'ru', N'Нет методологий, подходящих под поиск.'),
    (N'methodologies.searchNoMatches', N'kz', N'Іздеуге сай әдістеме жоқ.'),
    (N'methodologies.mode.numeric.Legacy', N'ru', N'Прежняя арифметика'),
    (N'methodologies.mode.numeric.Legacy', N'kz', N'Бұрынғы арифметика'),
    (N'methodologies.mode.numeric.Strict', N'ru', N'Строгая арифметика'),
    (N'methodologies.mode.numeric.Strict', N'kz', N'Қатаң арифметика'),
    (N'methodologies.mode.numeric.LegacyHint', N'ru', N'Прежняя арифметика: считается так же, как в прежней системе, — результаты совпадают с историческими.'),
    (N'methodologies.mode.numeric.LegacyHint', N'kz', N'Бұрынғы арифметика: бұрынғы жүйедегідей есептеледі — нәтижелер тарихи көрсеткіштермен сәйкес келеді.'),
    (N'methodologies.mode.numeric.StrictHint', N'ru', N'Строгая арифметика: точная десятичная арифметика с явным округлением; результаты могут немного отличаться от прежней системы.'),
    (N'methodologies.mode.numeric.StrictHint', N'kz', N'Қатаң арифметика: анық дөңгелектеуі бар дәл ондық арифметика; нәтижелер бұрынғы жүйеден сәл өзгеше болуы мүмкін.'),
    (N'methodologies.mode.trace.Off', N'ru', N'трассировка: выкл.'),
    (N'methodologies.mode.trace.Off', N'kz', N'трассировка: өшірулі'),
    (N'methodologies.mode.trace.ErrorsOnly', N'ru', N'трассировка: только ошибки'),
    (N'methodologies.mode.trace.ErrorsOnly', N'kz', N'трассировка: тек қателер'),
    (N'methodologies.mode.trace.Full', N'ru', N'трассировка: полная'),
    (N'methodologies.mode.trace.Full', N'kz', N'трассировка: толық'),
    (N'methodologies.mode.trace.OffHint', N'ru', N'Трассировка выключена: пошаговая запись расчёта не сохраняется.'),
    (N'methodologies.mode.trace.OffHint', N'kz', N'Трассировка өшірулі: есептің қадамдық жазбасы сақталмайды.'),
    (N'methodologies.mode.trace.ErrorsOnlyHint', N'ru', N'Трассировка только ошибок: записываются лишь шаги, завершившиеся ошибкой, — для разбора проблем.'),
    (N'methodologies.mode.trace.ErrorsOnlyHint', N'kz', N'Тек қателер трассировкасы: мәселелерді талдау үшін тек қатемен аяқталған қадамдар жазылады.'),
    (N'methodologies.mode.trace.FullHint', N'ru', N'Полная трассировка: записывается каждый шаг расчёта (медленнее; для подробной проверки).'),
    (N'methodologies.mode.trace.FullHint', N'kz', N'Толық трассировка: есептің әр қадамы жазылады (баяуырақ; толық тексеру үшін).'),
    (N'methodologies.mode.calendar.Actual', N'ru', N'фактические дни'),
    (N'methodologies.mode.calendar.Actual', N'kz', N'нақты күндер'),
    (N'methodologies.mode.calendar.Fixed365', N'ru', N'год 365 дней'),
    (N'methodologies.mode.calendar.Fixed365', N'kz', N'365 күндік жыл'),
    (N'methodologies.mode.calendar.Fixed360', N'ru', N'год 360 дней'),
    (N'methodologies.mode.calendar.Fixed360', N'kz', N'360 күндік жыл'),
    (N'sources.goToSources', N'ru', N'Настроить источники'),
    (N'sources.goToSources', N'kz', N'Дереккөздерді баптау'),
    (N'inspector.calcProblems', N'ru', N'Проблем расчёта: {count}'),
    (N'inspector.calcProblems', N'kz', N'Есеп мәселелері: {count}'),
    (N'inspector.calcProblemsOpen', N'ru', N'Открыть «Статус»'),
    (N'inspector.calcProblemsOpen', N'kz', N'«Күйі» қойындысын ашу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOCUX:ui-review ── кінець секції ──

-- DOCUX:ui-review ── en; ru/kz — порцією DOCUX:ui-review вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'filters.all', N'en', N'All', 1),
    (N'templates.stateAll', N'en', N'All states', 1),
    (N'permits.types.filter.categoryAll', N'en', N'All categories', 1),
    (N'units.dimensionAll', N'en', N'All dimensions', 1),
    (N'methodologies.search', N'en', N'Search methodologies', 1),
    (N'methodologies.searchPlaceholder', N'en', N'Code or name', 1),
    (N'methodologies.searchNoMatches', N'en', N'No methodologies match the search.', 1),
    (N'methodologies.mode.numeric.Legacy', N'en', N'Legacy numbers', 1),
    (N'methodologies.mode.numeric.Strict', N'en', N'Strict numbers', 1),
    (N'methodologies.mode.numeric.LegacyHint', N'en', N'Legacy numbers: calculated the same way as the previous system, so results match the historical figures.', 1),
    (N'methodologies.mode.numeric.StrictHint', N'en', N'Strict numbers: exact decimal arithmetic with explicit rounding; results may differ slightly from the previous system.', 1),
    (N'methodologies.mode.trace.Off', N'en', N'trace: off', 1),
    (N'methodologies.mode.trace.ErrorsOnly', N'en', N'trace: errors only', 1),
    (N'methodologies.mode.trace.Full', N'en', N'trace: full', 1),
    (N'methodologies.mode.trace.OffHint', N'en', N'Trace off: no step-by-step record of the calculation is kept.', 1),
    (N'methodologies.mode.trace.ErrorsOnlyHint', N'en', N'Trace errors only: only the steps that failed are recorded, for diagnosing problems.', 1),
    (N'methodologies.mode.trace.FullHint', N'en', N'Full trace: every step of the calculation is recorded (slower; for detailed checks).', 1),
    (N'methodologies.mode.calendar.Actual', N'en', N'actual days', 1),
    (N'methodologies.mode.calendar.Fixed365', N'en', N'365-day year', 1),
    (N'methodologies.mode.calendar.Fixed360', N'en', N'360-day year', 1),
    (N'sources.goToSources', N'en', N'Set up sources', 1),
    (N'inspector.calcProblems', N'en', N'Calculation problems: {count}', 1),
    (N'inspector.calcProblemsOpen', N'en', N'See Status', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOCUX:ui-review ── кінець секції ──

-- DOCUX:ui-review ── змінені тексти наявних ключів: епілог оновлює, лише поки в базі старе (випущене) значення ──
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'sources.notConfiguredHint', N'en',
     N'To connect PI, an administrator adds a connection here: the PI Web API address, the secret and the authentication. Until then data is entered by hand. See docs/admin/admin-guide.md, section 5, and docs/admin/operations-runbook.md, section 15.',
     N'To connect a data source, an administrator adds a connection here: PI Web API, PI SQL Client or SQL, with its address and credentials. Until then data is entered by hand. Details are in the administrator guide.'),
    (N'sources.notConfiguredHint', N'ru',
     N'Чтобы подключить PI, администратор добавляет здесь подключение: адрес PI Web API, секрет и способ аутентификации. До этого данные вводятся вручную. См. docs/admin/admin-guide.md, раздел 5, и docs/admin/operations-runbook.md, раздел 15.',
     N'Чтобы подключить источник данных, администратор добавляет здесь подключение: PI Web API, PI SQL Client или SQL — с адресом и учётными данными. До этого данные вводятся вручную. Подробности — в руководстве администратора.'),
    (N'sources.notConfiguredHint', N'kz',
     N'PI-ды қосу үшін әкімші осы жерде қосылым қосады: PI Web API мекенжайы, құпия және аутентификация тәсілі. Ол уақытқа дейін деректер қолмен енгізіледі. docs/admin/admin-guide.md құжатының 5-бөлімін және docs/admin/operations-runbook.md құжатының 15-бөлімін қараңыз.',
     N'Дереккөзді қосу үшін әкімші осы жерде қосылым қосады: PI Web API, PI SQL Client немесе SQL — мекенжайы мен тіркелгі деректерімен. Ол уақытқа дейін деректер қолмен енгізіледі. Толығырақ — әкімші нұсқаулығында.'),
    (N'permits.types.subtitle', N'en',
     N'A type says what a permit contains: which fields it has and which tables, with which columns. Add a type here and permits of that type work everywhere — no code, no release.',
     N'A type says what a permit contains: its fields and its tables with columns. Add a type here and its permits work everywhere — no code, no release.'),
    (N'permits.types.subtitle', N'ru',
     N'Тип описывает, что входит в разрешение: какие у него поля и какие таблицы с какими столбцами. Добавьте тип здесь — и разрешения этого типа заработают везде: без кода и без релиза.',
     N'Тип описывает состав разрешения: поля и таблицы со столбцами. Добавьте тип здесь — и разрешения этого типа заработают везде, без кода и релиза.'),
    (N'permits.types.subtitle', N'kz',
     N'Түр рұқсатта не бар екенін сипаттайды: қандай өрістер және қандай бағандары бар қандай кестелер. Түрді осында қосыңыз — және осы түрдегі рұқсаттар барлық жерде жұмыс істейді: кодсыз, релизсіз.',
     N'Түр рұқсаттың құрамын сипаттайды: өрістер мен бағандары бар кестелер. Түрді осында қосыңыз — осы түрдегі рұқсаттар кодсыз әрі релизсіз барлық жерде жұмыс істейді.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- DOCUX:ui-review ── кінець секції ──
