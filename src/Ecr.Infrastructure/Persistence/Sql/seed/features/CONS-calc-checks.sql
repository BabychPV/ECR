-- CONS:calc-checks ── ru/kz: тексти знахідок правил розрахунку й методологій нічної перевірки узгодженості (аудит 24.11, R1–R5, R7, R8); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE', N'ru', N'Документ {documentId}, период {period}: {results} результатов методологии {methodology} рассчитаны версией {resultVersion}, а для этого периода действует версия {effectiveVersion}. Пересчитайте период, чтобы применить действующую версию.'),
    (N'consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE_APPROVED', N'ru', N'Документ {documentId}, период {period}: {results} результатов методологии {methodology} рассчитаны версией {resultVersion}, а для этого периода действует версия {effectiveVersion}. В периоде есть утверждённые листы, а утверждённые данные не пересчитываются: верните период в работу и пересчитайте его либо подтвердите, что утверждённые числа остаются на версии {resultVersion}.'),
    (N'consistency.finding.EFFECTIVE_VERSION_FORMULAS', N'ru', N'Методология {methodology}: действующая сегодня версия {version} (с {effectiveFrom}) всё ещё работает на движке формул. Методологии считаются только скриптом: переведите эту версию в скрипт с подтверждённым совпадением результатов и опубликуйте её.'),
    (N'consistency.finding.ROW_INPUTS_COMPLETE_NO_RESULT', N'ru', N'Документ {documentId}, период {period}, таблица {table}, строка {row}: все обязательные входы методологии {methodology} заполнены, но текущий расчёт не дал для строки ни результата, ни проблемы. Пересчитайте период; если строка останется пустой, проверьте правила методологии для этой строки.'),
    (N'consistency.finding.ROW_INPUTS_COMPLETE_NO_RESULT_MORE', N'ru', N'Документ {documentId}, период {period}: ещё {more} строк с заполненными обязательными входами не имеют ни результата, ни проблемы в текущем расчёте (поимённо показаны первые 50).'),
    (N'consistency.finding.CURRENT_RUN_NO_RESULTS_WITH_BINDINGS', N'ru', N'Прогон расчёта {runId} (документ {documentId}, период {period}) актуален, но не дал ни результатов, ни проблем, хотя к нему относятся {tables} привязанных таблиц со строками и {methodologies} действующих методологий. Пересчитайте период и проверьте журнал задачи этого прогона.'),
    (N'consistency.finding.CALC_ISSUE_WITHOUT_RUN', N'ru', N'{issues} проблем расчёта документа {documentId}, период {period}, ссылаются на прогон расчёта {runId}, которого больше нет. Это остатки удалённого прогона: панель проблем не должна их показывать, их нужно удалить.'),
    (N'consistency.finding.OUTPUT_WITHOUT_BINDING', N'ru', N'Выход {output} методологии {methodology} (действующая версия {version}) рассчитывается, но не привязан ни к одной колонке используемых шаблонов, поэтому его значение нигде не показывается. Привяжите выход к колонке или уберите его в новой версии методологии.'),
    (N'consistency.finding.BINDING_DEAD_COLUMN', N'ru', N'Действующая привязка {bindingId} методологии {methodology} ведёт в колонку {table}.{column} версии шаблона {version}, которая удалена. Расчёт по-прежнему пишет по этой привязке, но форма её не показывает. Отключите привязку или привяжите выход к существующей колонке.'),
    (N'consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE', N'kz', N'{documentId} құжаты, {period} кезеңі: {methodology} әдістемесінің {results} нәтижесі {resultVersion} нұсқасымен есептелген, ал бұл кезеңге {effectiveVersion} нұсқасы қолданылады. Қолданыстағы нұсқаны қолдану үшін кезеңді қайта есептеңіз.'),
    (N'consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE_APPROVED', N'kz', N'{documentId} құжаты, {period} кезеңі: {methodology} әдістемесінің {results} нәтижесі {resultVersion} нұсқасымен есептелген, ал бұл кезеңге {effectiveVersion} нұсқасы қолданылады. Кезеңде бекітілген парақтар бар, ал бекітілген деректер қайта есептелмейді: кезеңді жұмысқа қайтарып, қайта есептеңіз немесе бекітілген сандар {resultVersion} нұсқасында қалатынын растаңыз.'),
    (N'consistency.finding.EFFECTIVE_VERSION_FORMULAS', N'kz', N'{methodology} әдістемесі: бүгін қолданыстағы {version} нұсқасы ({effectiveFrom} бастап) әлі де формулалар қозғалтқышында жұмыс істейді. Әдістемелер тек скриптпен есептеледі: бұл нұсқаны нәтижелердің сәйкестігі расталған скриптке ауыстырып, жариялаңыз.'),
    (N'consistency.finding.ROW_INPUTS_COMPLETE_NO_RESULT', N'kz', N'{documentId} құжаты, {period} кезеңі, {table} кестесі, {row} жолы: {methodology} әдістемесінің барлық міндетті кірістері толтырылған, бірақ ағымдағы есептеу бұл жол үшін нәтиже де, мәселе де бермеді. Кезеңді қайта есептеңіз; жол бос қалса, осы жолға арналған әдістеме ережелерін тексеріңіз.'),
    (N'consistency.finding.ROW_INPUTS_COMPLETE_NO_RESULT_MORE', N'kz', N'{documentId} құжаты, {period} кезеңі: міндетті кірістері толтырылған тағы {more} жолдың ағымдағы есептеуде нәтижесі де, мәселесі де жоқ (атымен алғашқы 50 жол көрсетілген).'),
    (N'consistency.finding.CURRENT_RUN_NO_RESULTS_WITH_BINDINGS', N'kz', N'{runId} есептеу іске қосылымы ({documentId} құжаты, {period} кезеңі) өзекті, бірақ оған жолдары бар {tables} байланыстырылған кесте және {methodologies} қолданыстағы әдістеме қатысты болса да, нәтиже де, мәселе де бермеді. Кезеңді қайта есептеп, осы іске қосылымның тапсырма журналын тексеріңіз.'),
    (N'consistency.finding.CALC_ISSUE_WITHOUT_RUN', N'kz', N'{documentId} құжатының {period} кезеңіндегі {issues} есептеу мәселесі енді жоқ {runId} есептеу іске қосылымына сілтейді. Бұл жойылған іске қосылымның қалдықтары: мәселелер тақтасы оларды көрсетпеуі керек, оларды жою қажет.'),
    (N'consistency.finding.OUTPUT_WITHOUT_BINDING', N'kz', N'{methodology} әдістемесінің {output} шығысы (қолданыстағы нұсқа {version}) есептеледі, бірақ қолданылатын үлгілердің бірде-бір бағанына байланыстырылмаған, сондықтан оның мәні еш жерде көрсетілмейді. Шығысты бағанға байланыстырыңыз немесе әдістеменің жаңа нұсқасында алып тастаңыз.'),
    (N'consistency.finding.BINDING_DEAD_COLUMN', N'kz', N'{methodology} әдістемесінің {bindingId} белсенді байланысы {version} үлгі нұсқасының жойылған {table}.{column} бағанына апарады. Есептеу бұл байланыс бойынша әлі де жазады, бірақ пішін оны көрсетпейді. Байланысты өшіріңіз немесе шығысты бар бағанға байланыстырыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CONS:calc-checks ── кінець секції ──

-- CONS:calc-checks ── en (область 1 — адмінський екран /admin/consistency-issues); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE', N'en', N'Document {documentId}, period {period}: {results} results of methodology {methodology} were calculated by version {resultVersion}, but the version in effect for this period is {effectiveVersion}. Recalculate the period to apply the version in effect.', 1),
    (N'consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE_APPROVED', N'en', N'Document {documentId}, period {period}: {results} results of methodology {methodology} were calculated by version {resultVersion}, but the version in effect for this period is {effectiveVersion}. The period has approved sheets, and approved data is never recalculated: return the period to work and recalculate it, or confirm that the approved numbers stay on version {resultVersion}.', 1),
    (N'consistency.finding.EFFECTIVE_VERSION_FORMULAS', N'en', N'Methodology {methodology}: the version in effect today, {version} (from {effectiveFrom}), still runs on the formula engine. Methodologies are calculated by script only: convert this version to a script with confirmed matching results and publish it.', 1),
    (N'consistency.finding.ROW_INPUTS_COMPLETE_NO_RESULT', N'en', N'Document {documentId}, period {period}, table {table}, row {row}: every required input of methodology {methodology} is filled, but the current calculation produced neither a result nor a problem for this row. Recalculate the period; if the row stays empty, check the methodology rules for this row.', 1),
    (N'consistency.finding.ROW_INPUTS_COMPLETE_NO_RESULT_MORE', N'en', N'Document {documentId}, period {period}: {more} more rows with every required input filled have neither a result nor a problem in the current calculation (the first 50 are listed one by one).', 1),
    (N'consistency.finding.CURRENT_RUN_NO_RESULTS_WITH_BINDINGS', N'en', N'Calculation run {runId} (document {documentId}, period {period}) is current but produced no results and no problems, although {tables} bound tables with rows and {methodologies} methodologies in effect apply to it. Recalculate the period and check the job log of this run.', 1),
    (N'consistency.finding.CALC_ISSUE_WITHOUT_RUN', N'en', N'{issues} calculation problems of document {documentId}, period {period}, refer to calculation run {runId}, which no longer exists. They are leftovers of a deleted run: the problems panel should not show them, and they must be removed.', 1),
    (N'consistency.finding.OUTPUT_WITHOUT_BINDING', N'en', N'Output {output} of methodology {methodology} (version in effect {version}) is calculated but not bound to any column of a template in use, so its value is shown nowhere. Bind the output to a column or remove it in a new methodology version.', 1),
    (N'consistency.finding.BINDING_DEAD_COLUMN', N'en', N'Active binding {bindingId} of methodology {methodology} points to column {table}.{column} of template version {version}, which has been deleted. Calculation still writes through this binding, but the form does not show it. Disable the binding or bind the output to an existing column.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CONS:calc-checks ── кінець секції ──

-- CONS:calc-checks-ui ── ru/kz: екран /admin/consistency-issues — назви правил, блок «Перевірки останнього прогону», порожній стан, посилання (аудит 24.11) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'consistency.rule.ORPHANED_CELL', N'ru', N'Ячейка ссылается на отсутствующую запись справочника'),
    (N'consistency.rule.BROKEN_FK', N'ru', N'Строка ссылается на отсутствующий экземпляр таблицы'),
    (N'consistency.rule.ARCHIVE_CHECKSUM', N'ru', N'Архив не совпадает с контрольными суммами'),
    (N'consistency.rule.UNBOUND_CALCULATED_COLUMN', N'ru', N'Расчётная колонка без источника значения'),
    (N'consistency.rule.UNSOURCED_FORMULA_COLUMN', N'ru', N'Формульная колонка без формулы и привязки'),
    (N'consistency.rule.NON_CURRENT_RUN_ROWS', N'ru', N'Остались строки неактуальных прогонов расчёта'),
    (N'consistency.rule.STALE_APPROVED_DEPENDENT', N'ru', N'Утверждённый месяц, источник которого изменился'),
    (N'consistency.rule.CALC_RESULT_VERSION_NOT_EFFECTIVE', N'ru', N'Результаты не действующей версии методологии'),
    (N'consistency.rule.EFFECTIVE_VERSION_FORMULAS', N'ru', N'Действующая версия методологии ещё на формулах'),
    (N'consistency.rule.ROW_INPUTS_COMPLETE_NO_RESULT', N'ru', N'Строка со всеми входами, но без результата'),
    (N'consistency.rule.CURRENT_RUN_NO_RESULTS_WITH_BINDINGS', N'ru', N'Текущий расчёт ничего не дал'),
    (N'consistency.rule.CALC_ISSUE_WITHOUT_RUN', N'ru', N'Проблемы расчёта удалённого прогона'),
    (N'consistency.rule.OUTPUT_WITHOUT_BINDING', N'ru', N'Выход методологии нигде не показан'),
    (N'consistency.rule.BINDING_DEAD_COLUMN', N'ru', N'Привязка к удалённой колонке'),
    (N'consistency.checks.title', N'ru', N'Проверки последнего запуска ({count})'),
    (N'consistency.checks.skipped', N'ru', N'Не выполнялась'),
    (N'consistency.checks.partial', N'ru', N'Проверено частично'),
    (N'consistency.checks.found', N'ru', N'Находок: {count}'),
    (N'consistency.emptyChecked', N'ru', N'Последняя проверка выполнила проверок: {count} — находок нет'),
    (N'consistency.openDocument', N'ru', N'Открыть документ {documentId}, период {period}'),
    (N'consistency.openMethodology', N'ru', N'Открыть методологию {methodology}'),
    (N'consistency.rule.ORPHANED_CELL', N'kz', N'Ұяшық жоқ анықтамалық жазбасына сілтейді'),
    (N'consistency.rule.BROKEN_FK', N'kz', N'Жол жоқ кесте данасына сілтейді'),
    (N'consistency.rule.ARCHIVE_CHECKSUM', N'kz', N'Мұрағат бақылау сомаларына сәйкес келмейді'),
    (N'consistency.rule.UNBOUND_CALCULATED_COLUMN', N'kz', N'Мән көзі жоқ есептелетін баған'),
    (N'consistency.rule.UNSOURCED_FORMULA_COLUMN', N'kz', N'Формуласы да, байланысы да жоқ формулалық баған'),
    (N'consistency.rule.NON_CURRENT_RUN_ROWS', N'kz', N'Өзекті емес есептеу іске қосылымдарының жолдары қалды'),
    (N'consistency.rule.STALE_APPROVED_DEPENDENT', N'kz', N'Дереккөзі өзгерген бекітілген ай'),
    (N'consistency.rule.CALC_RESULT_VERSION_NOT_EFFECTIVE', N'kz', N'Нәтижелер қолданыстағы әдістеме нұсқасынан емес'),
    (N'consistency.rule.EFFECTIVE_VERSION_FORMULAS', N'kz', N'Қолданыстағы әдістеме нұсқасы әлі формулаларда'),
    (N'consistency.rule.ROW_INPUTS_COMPLETE_NO_RESULT', N'kz', N'Барлық кірістері бар, бірақ нәтижесі жоқ жол'),
    (N'consistency.rule.CURRENT_RUN_NO_RESULTS_WITH_BINDINGS', N'kz', N'Ағымдағы есептеу ештеңе бермеді'),
    (N'consistency.rule.CALC_ISSUE_WITHOUT_RUN', N'kz', N'Жойылған іске қосылымның есептеу мәселелері'),
    (N'consistency.rule.OUTPUT_WITHOUT_BINDING', N'kz', N'Әдістеме шығысы еш жерде көрсетілмейді'),
    (N'consistency.rule.BINDING_DEAD_COLUMN', N'kz', N'Жойылған бағанға байланыс'),
    (N'consistency.checks.title', N'kz', N'Соңғы іске қосудың тексерулері ({count})'),
    (N'consistency.checks.skipped', N'kz', N'Орындалмады'),
    (N'consistency.checks.partial', N'kz', N'Ішінара тексерілді'),
    (N'consistency.checks.found', N'kz', N'Табылғандар: {count}'),
    (N'consistency.emptyChecked', N'kz', N'Соңғы тексеру орындаған тексерулер: {count} — табылған жоқ'),
    (N'consistency.openDocument', N'kz', N'{documentId} құжатын ашу, {period} кезеңі'),
    (N'consistency.openMethodology', N'kz', N'{methodology} әдістемесін ашу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CONS:calc-checks-ui ── кінець секції ──

-- CONS:calc-checks-ui ── en (область 1 — адмінський екран /admin/consistency-issues); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'consistency.rule.ORPHANED_CELL', N'en', N'Cell refers to a missing registry entry', 1),
    (N'consistency.rule.BROKEN_FK', N'en', N'Row refers to a missing table instance', 1),
    (N'consistency.rule.ARCHIVE_CHECKSUM', N'en', N'Archive does not match its checksums', 1),
    (N'consistency.rule.UNBOUND_CALCULATED_COLUMN', N'en', N'Calculated column without a value source', 1),
    (N'consistency.rule.UNSOURCED_FORMULA_COLUMN', N'en', N'Formula column without a formula or binding', 1),
    (N'consistency.rule.NON_CURRENT_RUN_ROWS', N'en', N'Rows of outdated calculation runs left', 1),
    (N'consistency.rule.STALE_APPROVED_DEPENDENT', N'en', N'Approved month whose source changed', 1),
    (N'consistency.rule.CALC_RESULT_VERSION_NOT_EFFECTIVE', N'en', N'Results not from the methodology version in effect', 1),
    (N'consistency.rule.EFFECTIVE_VERSION_FORMULAS', N'en', N'Methodology version in effect still on formulas', 1),
    (N'consistency.rule.ROW_INPUTS_COMPLETE_NO_RESULT', N'en', N'Row with all inputs but no result', 1),
    (N'consistency.rule.CURRENT_RUN_NO_RESULTS_WITH_BINDINGS', N'en', N'Current calculation produced nothing', 1),
    (N'consistency.rule.CALC_ISSUE_WITHOUT_RUN', N'en', N'Calculation problems of a deleted run', 1),
    (N'consistency.rule.OUTPUT_WITHOUT_BINDING', N'en', N'Methodology output shown nowhere', 1),
    (N'consistency.rule.BINDING_DEAD_COLUMN', N'en', N'Binding to a deleted column', 1),
    (N'consistency.checks.title', N'en', N'Checks in the last run ({count})', 1),
    (N'consistency.checks.skipped', N'en', N'Not run', 1),
    (N'consistency.checks.partial', N'en', N'Partly checked', 1),
    (N'consistency.checks.found', N'en', N'Findings: {count}', 1),
    (N'consistency.emptyChecked', N'en', N'The last check ran {count} checks — no findings', 1),
    (N'consistency.openDocument', N'en', N'Open document {documentId}, period {period}', 1),
    (N'consistency.openMethodology', N'en', N'Open methodology {methodology}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CONS:calc-checks-ui ── кінець секції ──

-- CONS:calc-checks-ui-text ── опис сторінки більше не обіцяє «звіряти числа між документами» (жодне правило цього не робить);
-- фільтр правила — перелік, тож підказка поля — «Усі правила». Змінені тексти наявних ключів: нові бази беруть нове значення
-- з COLL-ui20consistency / CATALOG-en-base / 00-prelude; епілог оновлює наявні бази, лише поки там стоїть САМЕ старе
-- значення (правку адміністратора з /admin/ui-strings не затирає).
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'consistency.description', N'en', N'Every night ECR compares numbers that must agree with each other across documents, tables and registries. Each finding names the rule that failed and the record it affects.', N'Every night and on demand, ECR checks data integrity and calculation results against the rules listed below. Each finding names its rule and links to the affected record.'),
    (N'consistency.description', N'ru', N'Каждую ночь ECR сверяет числа, которые должны совпадать между документами, таблицами и справочниками. Каждая находка называет нарушенное правило и затронутую запись.', N'Каждую ночь и по запросу ECR проверяет целостность данных и результаты расчётов по правилам, перечисленным ниже. Каждая находка называет своё правило и ведёт к затронутой записи.'),
    (N'consistency.description', N'kz', N'ECR әр түнде құжаттар, кестелер мен анықтамалықтар арасында сәйкес болуға тиіс сандарды салыстырады. Әр табылған жағдай бұзылған ережені және қозғалған жазбаны атайды.', N'ECR әр түнде және сұраныс бойынша деректердің тұтастығын және есептеу нәтижелерін төменде аталған ережелер бойынша тексереді. Әр табылған жағдай өз ережесін атайды және қозғалған жазбаға апарады.'),
    (N'consistency.ruleHint', N'en', N'Leave empty for every rule.', N'All rules'),
    (N'consistency.ruleHint', N'ru', N'Оставьте пустым для всех правил.', N'Все правила'),
    (N'consistency.ruleHint', N'kz', N'Барлық ереже үшін бос қалдырыңыз.', N'Барлық ережелер')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- CONS:calc-checks-ui-text ── кінець секції ──