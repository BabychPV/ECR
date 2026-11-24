-- COLL:ui31wizard ── ru/kz UI-31: майстер створення документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'wizard.back', N'ru', N'Назад'),
    (N'wizard.back', N'kz', N'Артқа'),
    (N'wizard.next', N'ru', N'Далее'),
    (N'wizard.next', N'kz', N'Келесі'),
    (N'wizard.review', N'ru', N'Проверка'),
    (N'wizard.review', N'kz', N'Тексеру'),
    (N'documents.createApply', N'ru', N'Создать документ'),
    (N'documents.createApply', N'kz', N'Құжат құру'),
    (N'documents.createReviewText', N'ru', N'Пока вы не подтвердите, ничего не создаётся. Документ создаётся пустым, как черновик.'),
    (N'documents.createReviewText', N'kz', N'Сіз растамайынша ештеңе құрылмайды. Құжат бос, жоба ретінде құрылады.'),
    (N'documents.createProjectHint', N'ru', N'Для какой площадки этот отчёт?'),
    (N'documents.createProjectHint', N'kz', N'Бұл есеп қай алаңға арналған?'),
    (N'documents.createTemplateHint', N'ru', N'Версию шаблона определяет проект.'),
    (N'documents.createTemplateHint', N'kz', N'Үлгі нұсқасын жоба анықтайды.'),
    (N'documents.createPeriodHint', N'ru', N'Документ откроется в этом периоде. Ещё не открытые периоды не показаны.'),
    (N'documents.createPeriodHint', N'kz', N'Құжат осы кезеңде ашылады. Әлі ашылмаған кезеңдер көрсетілмейді.'),
    (N'documents.createNoOpenPeriod', N'ru', N'В этом проекте нет открытого периода. Документ можно создать, но вводить данные можно будет только после того, как администратор периодов откроет период.'),
    (N'documents.createNoOpenPeriod', N'kz', N'Бұл жобада ашық кезең жоқ. Құжатты құруға болады, бірақ деректерді кезең әкімшісі кезеңді ашқаннан кейін ғана енгізуге болады.'),
    (N'documents.createClosedPeriod', N'ru', N'Этот период закрыт. Документ будет создан, но изменить его данные можно будет только после повторного открытия периода.'),
    (N'documents.createClosedPeriod', N'kz', N'Бұл кезең жабық. Құжат құрылады, бірақ оның деректерін кезең қайта ашылғаннан кейін ғана өзгертуге болады.'),
    (N'documents.createSheetsHint', N'ru', N'Снимите отметку с листов, которые не относятся к этой площадке. По умолчанию включены все.'),
    (N'documents.createSheetsHint', N'kz', N'Бұл алаңға қатысы жоқ парақтардың белгісін алып тастаңыз. Әдепкіде барлығы қосылған.'),
    (N'documents.createNoSheets', N'ru', N'Включите хотя бы один лист: документ без листов нельзя заполнить.'),
    (N'documents.createNoSheets', N'kz', N'Кемінде бір парақты қосыңыз: парақсыз құжатты толтыру мүмкін емес.'),
    (N'documents.createSheetsCount', N'ru', N'{included} из {total}'),
    (N'documents.createSheetsCount', N'kz', N'{total} ішінен {included}'),
    -- Pass2 п. 4/5 (людський прохід 07.11): документ проєкту вже є; «відкинути введене?»
    -- ✎ B74 (24.11): тексти «уже є» — про документ ОБРАНОГО шаблону (проєкт тримає кілька шаблонів); старе → нове — #UiChanged нижче
    (N'documents.createExistsTitle', N'ru', N'По этому шаблону в проекте уже есть документ'),
    (N'documents.createExistsTitle', N'kz', N'Бұл жобада осы үлгі бойынша құжат бұрыннан бар'),
    (N'documents.createExistsText', N'ru', N'{key} уже ведётся по этому шаблону в проекте, все месяцы — внутри него: откройте его вместо того, чтобы заводить второй.'),
    (N'documents.createExistsText', N'kz', N'{key} осы жобада осы үлгі бойынша жүргізіліп жатыр, барлық айлар оның ішінде: екіншісін бастаудың орнына оны ашыңыз.'),
    (N'documents.createExistsOpen', N'ru', N'Открыть {key}'),
    (N'documents.createExistsOpen', N'kz', N'{key} ашу'),
    (N'documents.createExistsShowAll', N'ru', N'Все документы проекта'),
    (N'documents.createExistsShowAll', N'kz', N'Жобаның барлық құжаттары'),
    (N'documents.createExistsReview', N'ru', N'В проекте уже есть {key} по этому шаблону. Второй документ создаётся, только если шаблон допускает больше одного на проект.'),
    (N'documents.createExistsReview', N'kz', N'Жобада осы үлгі бойынша {key} бұрыннан бар. Екінші құжат үлгі жобаға біреуден көп құжатқа рұқсат етсе ғана құрылады.'),
    (N'documents.createDiscardTitle', N'ru', N'Отменить введённое?'),
    (N'documents.createDiscardTitle', N'kz', N'Енгізілгенді тастау керек пе?'),
    (N'documents.createDiscardText', N'ru', N'Документ ещё не создан. Выбранные проект, листы и название будут потеряны.'),
    (N'documents.createDiscardText', N'kz', N'Құжат әлі құрылған жоқ. Таңдалған жоба, парақтар мен атау жоғалады.'),
    (N'documents.createDiscardVerb', N'ru', N'Отменить'),
    (N'documents.createDiscardVerb', N'kz', N'Тастау'),
    (N'documents.createKeepEditing', N'ru', N'Продолжить ввод'),
    (N'documents.createKeepEditing', N'kz', N'Енгізуді жалғастыру'),
    -- ✎ B74 (24.11): шаблон «один документ на проєкт» (B151) із документом у проєкті — Next вимкнено, лише «Open»
    (N'documents.createExistsSingleText', N'ru', N'{key} уже ведётся по этому шаблону в проекте, а шаблон допускает только один документ на проект: откройте его.'),
    (N'documents.createExistsSingleText', N'kz', N'{key} осы жобада осы үлгі бойынша жүргізіліп жатыр, ал үлгі жобаға тек бір құжатқа рұқсат етеді: оны ашыңыз.'),
    (N'documents.createExistsSingleHidden', N'ru', N'Этот шаблон допускает только один документ на проект, и в проекте он уже есть, но вам не виден. Попросите доступ у руководителя проекта.'),
    (N'documents.createExistsSingleHidden', N'kz', N'Бұл үлгі жобаға тек бір құжатқа рұқсат етеді, жобада ол бұрыннан бар, бірақ сізге көрінбейді. Жоба жетекшісінен рұқсат сұраңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui31wizard ── кінець секції ──
-- COLL:ui31wizard ── UI-31: майстер створення документа (кроки, підсумок, підписи Back/Next/Review); ru/kz — порцією COLL:ui31wizard у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'wizard.back', N'en', N'Back', 1),
    (N'wizard.next', N'en', N'Next', 1),
    (N'wizard.review', N'en', N'Review', 1),
    (N'documents.createApply', N'en', N'Create document', 1),
    (N'documents.createReviewText', N'en', N'Nothing is created until you confirm. The document starts empty, as a draft.', 1),
    (N'documents.createProjectHint', N'en', N'Which site is this report for?', 1),
    (N'documents.createTemplateHint', N'en', N'The project decides the template version.', 1),
    (N'documents.createPeriodHint', N'en', N'The document opens in this period. Periods not opened yet are not listed.', 1),
    (N'documents.createNoOpenPeriod', N'en', N'No period of this project is open. The document can be created, but data can be entered only after a period administrator opens a period.', 1),
    (N'documents.createClosedPeriod', N'en', N'This period is closed. The document will be created, but its data can be changed only after the period is reopened.', 1),
    (N'documents.createSheetsHint', N'en', N'Leave out sheets that do not apply to this site. All are included by default.', 1),
    (N'documents.createNoSheets', N'en', N'Include at least one sheet: a document without sheets cannot be filled in.', 1),
    (N'documents.createSheetsCount', N'en', N'{included} of {total}', 1),
    -- Pass2 п. 4/5 (людський прохід 07.11): документ проєкту вже є; «відкинути введене?»
    -- ✎ B74 (24.11): тексти «уже є» — про документ ОБРАНОГО шаблону; старе → нове — #UiChanged нижче
    (N'documents.createExistsTitle', N'en', N'This template already has a document in this project', 1),
    (N'documents.createExistsText', N'en', N'{key} is already kept for this template in this project, all months inside it: open it instead of starting a second one.', 1),
    (N'documents.createExistsOpen', N'en', N'Open {key}', 1),
    (N'documents.createExistsShowAll', N'en', N'All documents of the project', 1),
    (N'documents.createExistsReview', N'en', N'The project already has {key} for this template. A second document is created only if the template allows more than one per project.', 1),
    (N'documents.createDiscardTitle', N'en', N'Discard what you entered?', 1),
    (N'documents.createDiscardText', N'en', N'The document has not been created. The project, sheets and name you chose will be lost.', 1),
    (N'documents.createDiscardVerb', N'en', N'Discard', 1),
    (N'documents.createKeepEditing', N'en', N'Keep editing', 1),
    -- ✎ B74 (24.11): шаблон «один документ на проєкт» (B151) із документом у проєкті — Next вимкнено, лише «Open»
    (N'documents.createExistsSingleText', N'en', N'{key} is already kept for this template in this project, and the template allows only one document per project: open it.', 1),
    (N'documents.createExistsSingleHidden', N'en', N'This template allows only one document per project, and this project already has one that you cannot see. Ask the project manager for access.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui31wizard ── кінець секції ──
-- COLL:ui31wizard ── змінені тексти наявних ключів (✎ B74, людина 24.11 live: «чому я бачу це і для чого?»): проєкт
-- «рік · об'єкт» тримає документи кількох шаблонів, тож «у проєкту вже є документ… Next — створити ще один» — шум і
-- суперечить `409 ECR-DOC-4092 documentExistsForTemplate`. Епілог оновлює, лише поки в базі стоїть САМЕ старе значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'documents.createExistsTitle', N'en', N'This project already has a document', N'This template already has a document in this project'),
    (N'documents.createExistsTitle', N'ru', N'У этого проекта уже есть документ', N'По этому шаблону в проекте уже есть документ'),
    (N'documents.createExistsTitle', N'kz', N'Бұл жобаның құжаты бұрыннан бар', N'Бұл жобада осы үлгі бойынша құжат бұрыннан бар'),
    (N'documents.createExistsText', N'en', N'{key} is already kept for this project. One document per object, all months inside it: open it, or press Next to create another one.', N'{key} is already kept for this template in this project, all months inside it: open it instead of starting a second one.'),
    (N'documents.createExistsText', N'ru', N'{key} уже ведётся по этому проекту. Один документ — на объект, все месяцы внутри него: откройте его или нажмите «Далее», чтобы создать ещё один.', N'{key} уже ведётся по этому шаблону в проекте, все месяцы — внутри него: откройте его вместо того, чтобы заводить второй.'),
    (N'documents.createExistsText', N'kz', N'{key} осы жоба бойынша жүргізіліп жатыр. Бір нысанға бір құжат, барлық айлар оның ішінде: оны ашыңыз немесе тағы біреуін құру үшін «Келесі» басыңыз.', N'{key} осы жобада осы үлгі бойынша жүргізіліп жатыр, барлық айлар оның ішінде: екіншісін бастаудың орнына оны ашыңыз.'),
    (N'documents.createExistsReview', N'en', N'The project already has {key}: another document will be created.', N'The project already has {key} for this template. A second document is created only if the template allows more than one per project.'),
    (N'documents.createExistsReview', N'ru', N'У проекта уже есть {key}: будет создан ещё один документ.', N'В проекте уже есть {key} по этому шаблону. Второй документ создаётся, только если шаблон допускает больше одного на проект.'),
    (N'documents.createExistsReview', N'kz', N'Жобада {key} бұрыннан бар: тағы бір құжат құрылады.', N'Жобада осы үлгі бойынша {key} бұрыннан бар. Екінші құжат үлгі жобаға біреуден көп құжатқа рұқсат етсе ғана құрылады.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- COLL:ui31wizard ── кінець секції ──
