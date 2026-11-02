-- FP:fixes ── ru/kz: прохід «від першого обличчя» 2026-10-11 (порожній стан документів, нова версія методики, CSV довідника без полів, пароль, журнал, назви задач); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.noProjects', N'ru', N'У вас нет доступа ни к одному проекту'),
    (N'documents.noProjects', N'kz', N'Сізде ешбір жобаға қолжетімділік жоқ'),
    (N'documents.noProjectsHint', N'ru', N'Документы показываются по проектам, а вашей учётной записи пока не выдан доступ ни к одному проекту. Попросите администратора выдать доступ; раздел «Мои группы» показывает, какие роли у вас есть.'),
    (N'documents.noProjectsHint', N'kz', N'Құжаттар жобалар бойынша көрсетіледі, ал сіздің тіркелгіңізге әлі ешбір жобаға қолжетімділік берілмеген. Әкімшіден қолжетімділік беруді сұраңыз; «Менің топтарым» бөлімі сізде қандай рөлдер бар екенін көрсетеді.'),
    (N'methodologies.emptyDraftWarning', N'ru', N'В пустом черновике нет формул, констант, правил и выходов: ничего не копируется. Чтобы изменить существующую версию, выберите её в поле «Копировать из».'),
    (N'methodologies.emptyDraftWarning', N'kz', N'Бос нобайда формулалар, тұрақтылар, ережелер және шығыс мәндер жоқ: ештеңе көшірілмейді. Бар нұсқаны өзгерту үшін оны «Көшіру көзі» өрісінде таңдаңыз.'),
    (N'err.ECR-REG-0422.entriesCsvNoFields', N'ru', N'В справочнике «{registryCode}» ещё нет полей, поэтому столбец «{column}» импортировать некуда. Сначала добавьте поля в конструкторе справочника и опубликуйте его: столбцы файла — это коды полей, плюс «code».'),
    (N'err.ECR-REG-0422.entriesCsvNoFields', N'kz', N'«{registryCode}» анықтамалығында әлі өрістер жоқ, сондықтан «{column}» бағанын импорттауға болмайды. Алдымен анықтамалық конструкторында өрістерді қосып, оны жариялаңыз: файл бағандары — өріс кодтары, және «code».'),
    (N'password.changed', N'ru', N'Пароль изменён. При следующем входе используйте новый пароль.'),
    (N'password.changed', N'kz', N'Құпиясөз өзгертілді. Келесі кіргенде жаңа құпиясөзді пайдаланыңыз.'),
    (N'registryAudit.entryValidityRescanned', N'ru', N'Пересчитано строк: {count}.'),
    (N'registryAudit.entryValidityRescanned', N'kz', N'Қайта есептелген жолдар: {count}.'),
    (N'jobs.kind.archive', N'ru', N'Архивация года'),
    (N'jobs.kind.archive', N'kz', N'Жылды мұрағаттау'),
    (N'jobs.kind.notification', N'ru', N'Доставка уведомлений'),
    (N'jobs.kind.notification', N'kz', N'Хабарламаларды жеткізу'),
    (N'jobs.kind.partitionCheck', N'ru', N'Проверка запаса секций'),
    (N'jobs.kind.partitionCheck', N'kz', N'Бөлімдер қорын тексеру'),
    (N'jobs.kind.periodState', N'ru', N'Обновление состояния периодов'),
    (N'jobs.kind.periodState', N'kz', N'Кезеңдер күйін жаңарту'),
    (N'jobs.kind.registrySync', N'ru', N'Синхронизация справочников из источника'),
    (N'jobs.kind.registrySync', N'kz', N'Анықтамалықтарды дереккөзден синхрондау'),
    (N'jobs.kind.reportRetention', N'ru', N'Хранение срезов отчётов'),
    (N'jobs.kind.reportRetention', N'kz', N'Есеп кесінділерін сақтау'),
    (N'jobs.kind.reportSnapshotFormat', N'ru', N'Обновление формата срезов отчётов'),
    (N'jobs.kind.reportSnapshotFormat', N'kz', N'Есеп кесінділерінің пішімін жаңарту'),
    (N'jobs.kind.submissionSnapshotRetention', N'ru', N'Очистка старых срезов подачи'),
    (N'jobs.kind.submissionSnapshotRetention', N'kz', N'Ескі тапсыру кесінділерін тазарту'),
    (N'jobs.kind.rowWindowRefetch', N'ru', N'Повторная выборка окон строк'),
    (N'jobs.kind.rowWindowRefetch', N'kz', N'Жол терезелерін қайта алу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- FP:fixes ── кінець секції ──
