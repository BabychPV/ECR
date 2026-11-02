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
