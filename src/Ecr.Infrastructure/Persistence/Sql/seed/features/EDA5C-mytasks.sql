-- EDA-5c:mytasks ── ru/kz секцій передач у My tasks; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'myTasks.assignToFill', N'ru', N'Переданы мне'),
    (N'myTasks.assignToFill', N'kz', N'Маған берілген'),
    (N'myTasks.awaitingValidation', N'ru', N'Ожидают моей валидации'),
    (N'myTasks.awaitingValidation', N'kz', N'Менің валидациямды күтуде'),
    (N'myTasks.edit.error', N'ru', N'Не удалось загрузить переданные вам месяцы.'),
    (N'myTasks.edit.error', N'kz', N'Сізге берілген айларды жүктеу мүмкін болмады.'),
    (N'myTasks.edit.filledAt', N'ru', N'Заполнено {date}'),
    (N'myTasks.edit.filledAt', N'kz', N'Толтырылған күні: {date}'),
    (N'myTasks.edit.returnedBy', N'ru', N'Возвращено владельцем: {reason}'),
    (N'myTasks.edit.returnedBy', N'kz', N'Иесі қайтарды: {reason}'),
    (N'myTasks.edit.validate', N'ru', N'Валидировать…'),
    (N'myTasks.edit.validate', N'kz', N'Валидациялау…'),
    (N'myTasks.edit.open', N'ru', N'Открыть'),
    (N'myTasks.edit.open', N'kz', N'Ашу'),
    (N'myTasks.edit.pendingBadge', N'ru', N'Месяцев, требующих вашего действия: {n}'),
    (N'myTasks.edit.pendingBadge', N'kz', N'Сіздің әрекетіңізді қажет ететін айлар: {n}'),
    (N'myTasks.edit.status.Assigned', N'ru', N'Передано'),
    (N'myTasks.edit.status.Assigned', N'kz', N'Берілген'),
    (N'myTasks.edit.status.Filled', N'ru', N'Заполнено, ждёт валидации'),
    (N'myTasks.edit.status.Filled', N'kz', N'Толтырылған, валидацияны күтуде'),
    (N'myTasks.edit.status.Returned', N'ru', N'Возвращено на доработку'),
    (N'myTasks.edit.status.Returned', N'kz', N'Пысықтауға қайтарылған')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- EDA-5c:mytasks ── кінець секції ──
