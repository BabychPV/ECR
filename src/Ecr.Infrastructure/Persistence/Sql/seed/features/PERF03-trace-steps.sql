-- PERF03:trace-steps ── D-565: прапор методології «трасувати проміжні кроки» (редактор методології), журнал структурних змін; ru/kz ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'methodologies.traceSteps.title', N'ru', N'Трассировка промежуточных шагов'),
    (N'methodologies.traceSteps.on', N'ru', N'Включена'),
    (N'methodologies.traceSteps.off', N'ru', N'Выключена'),
    (N'methodologies.traceSteps.label', N'ru', N'Сохранять промежуточные шаги расчёта'),
    (N'methodologies.traceSteps.hint', N'ru', N'Когда включено, каждый перерасчёт этой методологии сохраняет промежуточные шаги (формула, входы, значение), и расчёт можно объяснить по шагам. Результаты и входы сохраняются в любом случае, числа не меняются. По умолчанию выключено: шаги многократно увеличивают журнал транзакций базы. Включайте на время, пока нужно посмотреть промежуточные расчёты, затем выключите.'),
    (N'methodologies.traceSteps.offNote', N'ru', N'Трассировка выключена: для чисел, посчитанных при выключенной трассировке, промежуточные шаги не сохранены, поэтому пошаговое объяснение расчёта для них недоступно. Включить её здесь может пользователь с правом редактировать формулы методологии; шаги появятся после следующего перерасчёта документа.'),
    (N'methodologies.traceSteps.readOnly', N'ru', N'Изменить может только пользователь с правом редактировать формулы методологии.'),
    (N'methodologies.traceSteps.savedOn', N'ru', N'Трассировка промежуточных шагов включена. Шаги появятся после следующего перерасчёта.'),
    (N'methodologies.traceSteps.savedOff', N'ru', N'Трассировка промежуточных шагов выключена.'),
    (N'audit.entityLabel.calc.Methodology', N'ru', N'Методология'),
    (N'audit.operationLabel.SetTraceSteps', N'ru', N'Изменена трассировка промежуточных шагов'),
    (N'methodologies.traceSteps.title', N'kz', N'Аралық қадамдарды трассалау'),
    (N'methodologies.traceSteps.on', N'kz', N'Қосулы'),
    (N'methodologies.traceSteps.off', N'kz', N'Өшірулі'),
    (N'methodologies.traceSteps.label', N'kz', N'Есептеудің аралық қадамдарын сақтау'),
    (N'methodologies.traceSteps.hint', N'kz', N'Қосулы болғанда осы әдістеменің әр қайта есептеуі аралық қадамдарды (формула, кірістер, мән) сақтайды, сонда есептеуді қадам бойынша түсіндіруге болады. Нәтижелер мен кірістер кез келген жағдайда сақталады, сандар өзгермейді. Әдепкі бойынша өшірулі: қадамдар дерекқордың транзакциялар журналын бірнеше есе ұлғайтады. Аралық есептеулерді қарау қажет кезде қосып, кейін өшіріңіз.'),
    (N'methodologies.traceSteps.offNote', N'kz', N'Трассалау өшірулі: ол өшірулі кезде есептелген сандар үшін аралық қадамдар сақталмаған, сондықтан олар үшін есептеудің қадамдық түсіндірмесі қолжетімсіз. Мұнда оны әдістеме формулаларын өңдеу құқығы бар пайдаланушы қоса алады; қадамдар құжатты келесі қайта есептегеннен кейін пайда болады.'),
    (N'methodologies.traceSteps.readOnly', N'kz', N'Тек әдістеме формулаларын өңдеу құқығы бар пайдаланушы өзгерте алады.'),
    (N'methodologies.traceSteps.savedOn', N'kz', N'Аралық қадамдарды трассалау қосылды. Қадамдар келесі қайта есептеуден кейін пайда болады.'),
    (N'methodologies.traceSteps.savedOff', N'kz', N'Аралық қадамдарды трассалау өшірілді.'),
    (N'audit.entityLabel.calc.Methodology', N'kz', N'Әдістеме'),
    (N'audit.operationLabel.SetTraceSteps', N'kz', N'Аралық қадамдарды трассалау өзгертілді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERF03:trace-steps ── кінець секції ──
-- PERF03:trace-steps ── D-565: прапор «трасувати проміжні кроки» (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'methodologies.traceSteps.title', N'en', N'Intermediate-step tracing', 1),
    (N'methodologies.traceSteps.on', N'en', N'On', 1),
    (N'methodologies.traceSteps.off', N'en', N'Off', 1),
    (N'methodologies.traceSteps.label', N'en', N'Store intermediate calculation steps', 1),
    (N'methodologies.traceSteps.hint', N'en', N'When on, every recalculation of this methodology stores its intermediate steps (formula, inputs, value), so the calculation can be explained step by step. Results and inputs are stored either way; the numbers do not change. Off by default: the steps multiply the database transaction log several times over. Turn it on while you need to look at intermediate calculations, then turn it off.', 1),
    (N'methodologies.traceSteps.offNote', N'en', N'Tracing is off: intermediate steps were not stored for numbers calculated while it was off, so a step-by-step explanation is not available for them. A user with the right to edit methodology formulas can turn it on here; steps appear after the next recalculation of the document.', 1),
    (N'methodologies.traceSteps.readOnly', N'en', N'Only a user with the right to edit methodology formulas can change this.', 1),
    (N'methodologies.traceSteps.savedOn', N'en', N'Intermediate-step tracing is on. Steps appear after the next recalculation.', 1),
    (N'methodologies.traceSteps.savedOff', N'en', N'Intermediate-step tracing is off.', 1),
    (N'audit.entityLabel.calc.Methodology', N'en', N'Methodology', 1),
    (N'audit.operationLabel.SetTraceSteps', N'en', N'Intermediate-step tracing changed', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERF03:trace-steps ── кінець секції ──
-- PERF03:trace-steps-diagnostics ── D-565: проблема рядка StepError — чи записано кроки, перехід до кроку, що впав; ru/kz ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.stepErrorRows.traceOff', N'ru', N'Шаг, где возникла ошибка, не виден: последний пересчёт не сохранял промежуточные шаги методологии ({methodologies}) — трассировка выключена. Пользователь с правом редактировать формулы методологии может её включить; после пересчёта здесь будет видно шаг с ошибкой и его входы.'),
    (N'document.stepErrorRows.traceOffLink', N'ru', N'Трассировка методологии {methodology}'),
    (N'document.stepErrorRows.showStep', N'ru', N'показать шаг'),
    (N'document.stepTrace.title', N'ru', N'Шаг расчёта с ошибкой · {row}'),
    (N'document.stepTrace.notRecorded', N'ru', N'Шаги этого расчёта не сохранены: трассировка методологии была выключена. Включите её в редакторе методологии и пересчитайте документ.'),
    (N'document.stepTrace.noSteps', N'ru', N'Для этой строки в актуальном расчёте нет записанных шагов. Пересчитайте документ и откройте снова.'),
    (N'document.stepTrace.input', N'ru', N'Вход'),
    (N'document.stepTrace.value', N'ru', N'Значение'),
    (N'document.stepTrace.unit', N'ru', N'Единица'),
    (N'document.stepTrace.noInputs', N'ru', N'У шага нет входов.'),
    (N'document.stepTrace.truncated', N'ru', N'Показаны не все шаги строки.'),
    (N'document.stepErrorRows.traceOff', N'kz', N'Қате болған қадам көрінбейді: соңғы қайта есептеу әдістеменің ({methodologies}) аралық қадамдарын сақтамады — трассалау өшірулі. Әдістеме формулаларын өңдеу құқығы бар пайдаланушы оны қоса алады; қайта есептегеннен кейін мұнда қатесі бар қадам мен оның кірістері көрінеді.'),
    (N'document.stepErrorRows.traceOffLink', N'kz', N'{methodology} әдістемесін трассалау'),
    (N'document.stepErrorRows.showStep', N'kz', N'қадамды көрсету'),
    (N'document.stepTrace.title', N'kz', N'Қатесі бар есептеу қадамы · {row}'),
    (N'document.stepTrace.notRecorded', N'kz', N'Бұл есептеудің қадамдары сақталмаған: әдістемені трассалау өшірулі болған. Оны әдістеме редакторында қосып, құжатты қайта есептеңіз.'),
    (N'document.stepTrace.noSteps', N'kz', N'Бұл жол үшін өзекті есептеуде жазылған қадамдар жоқ. Құжатты қайта есептеп, қайта ашыңыз.'),
    (N'document.stepTrace.input', N'kz', N'Кіріс'),
    (N'document.stepTrace.value', N'kz', N'Мән'),
    (N'document.stepTrace.unit', N'kz', N'Бірлік'),
    (N'document.stepTrace.noInputs', N'kz', N'Қадамның кірістері жоқ.'),
    (N'document.stepTrace.truncated', N'kz', N'Жолдың барлық қадамдары көрсетілмеген.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERF03:trace-steps-diagnostics ── кінець секції ──
-- PERF03:trace-steps-diagnostics ── D-565: діагностика StepError (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.stepErrorRows.traceOff', N'en', N'The step that failed is not visible: the last recalculation did not store the intermediate steps of the methodology ({methodologies}) because tracing is off. A user with the right to edit methodology formulas can turn it on; after the next recalculation the failing step and its inputs will be shown here.', 1),
    (N'document.stepErrorRows.traceOffLink', N'en', N'Tracing of methodology {methodology}', 1),
    (N'document.stepErrorRows.showStep', N'en', N'show step', 1),
    (N'document.stepTrace.title', N'en', N'Calculation step that failed · {row}', 1),
    (N'document.stepTrace.notRecorded', N'en', N'The steps of this calculation were not stored: tracing of the methodology was off. Turn it on in the methodology editor and recalculate the document.', 1),
    (N'document.stepTrace.noSteps', N'en', N'The current calculation has no stored steps for this row. Recalculate the document and open this again.', 1),
    (N'document.stepTrace.input', N'en', N'Input', 1),
    (N'document.stepTrace.value', N'en', N'Value', 1),
    (N'document.stepTrace.unit', N'en', N'Unit', 1),
    (N'document.stepTrace.noInputs', N'en', N'The step has no inputs.', 1),
    (N'document.stepTrace.truncated', N'en', N'Not all steps of the row are shown.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERF03:trace-steps-diagnostics ── кінець секції ──
