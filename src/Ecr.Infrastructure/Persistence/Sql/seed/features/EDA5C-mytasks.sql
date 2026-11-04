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
-- EDA-5c:mytasks ── секції «Assigned to me» / «Awaiting my validation» у My tasks; ru/kz — порцією EDA-5c:mytasks нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'myTasks.assignToFill', N'en', N'Assigned to me', 1),
    (N'myTasks.awaitingValidation', N'en', N'Awaiting my validation', 1),
    (N'myTasks.edit.error', N'en', N'Could not load the months assigned to you.', 1),
    (N'myTasks.edit.filledAt', N'en', N'Filled {date}', 1),
    (N'myTasks.edit.returnedBy', N'en', N'Returned by owner: {reason}', 1),
    (N'myTasks.edit.validate', N'en', N'Validate…', 1),
    (N'myTasks.edit.open', N'en', N'Open', 1),
    (N'myTasks.edit.pendingBadge', N'en', N'{n} assigned months need your action', 1),
    (N'myTasks.edit.status.Assigned', N'en', N'Assigned', 1),
    (N'myTasks.edit.status.Filled', N'en', N'Filled, awaiting validation', 1),
    (N'myTasks.edit.status.Returned', N'en', N'Returned for edits', 1),
    -- ФВ-12.5:approval-queue ── блок «До погодження» у My tasks ──
    (N'myTasks.approval.title', N'en', N'Awaiting my approval', 1),
    (N'myTasks.approval.error', N'en', N'Could not load the sheets awaiting your approval.', 1),
    (N'myTasks.approval.more', N'en', N'Show more', 1),
    (N'myTasks.approval.open', N'en', N'Review', 1),
    (N'myTasks.approval.submittedAt', N'en', N'Submitted {date}', 1),
    (N'myTasks.approval.submittedBy', N'en', N'Submitted by {name} · {date}', 1),
    (N'myTasks.approval.step', N'en', N'Step {n} of {count}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- EDA-5c:mytasks ── кінець секції ──
