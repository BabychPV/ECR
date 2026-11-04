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
    (N'documents.createSheetsCount', N'kz', N'{total} ішінен {included}')
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
    (N'documents.createSheetsCount', N'en', N'{included} of {total}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui31wizard ── кінець секції ──
