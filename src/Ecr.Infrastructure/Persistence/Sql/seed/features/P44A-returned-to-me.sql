-- P44A:returned-to-me ── ru/kz: «My tasks» — «Повернуто на доопрацювання» і лічильник задач на кнопці (D-449); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'myTasks.returned.title', N'ru', N'Возвращено на доработку'),
    (N'myTasks.returned.title', N'kz', N'Пысықтауға қайтарылды'),
    (N'myTasks.returned.error', N'ru', N'Не удалось загрузить листы, возвращённые вам на доработку.'),
    (N'myTasks.returned.error', N'kz', N'Сізге пысықтауға қайтарылған парақтарды жүктеу мүмкін болмады.'),
    (N'myTasks.returned.more', N'ru', N'Показать ещё'),
    (N'myTasks.returned.more', N'kz', N'Көбірек көрсету'),
    (N'myTasks.returned.open', N'ru', N'Открыть лист'),
    (N'myTasks.returned.open', N'kz', N'Парақты ашу'),
    (N'myTasks.returned.returnedBy', N'ru', N'Вернул(а) {name} · {date}'),
    (N'myTasks.returned.returnedBy', N'kz', N'Қайтарған: {name} · {date}'),
    (N'myTasks.returned.returnedAt', N'ru', N'Возвращено {date}'),
    (N'myTasks.returned.returnedAt', N'kz', N'Қайтарылған күні: {date}'),
    (N'myTasks.returned.reason', N'ru', N'Причина'),
    (N'myTasks.returned.reason', N'kz', N'Себебі'),
    (N'myTasks.returned.reasonMore', N'ru', N'Показать причину полностью'),
    (N'myTasks.returned.reasonMore', N'kz', N'Себебін толық көрсету'),
    (N'myTasks.returned.reasonLess', N'ru', N'Свернуть'),
    (N'myTasks.returned.reasonLess', N'kz', N'Жию'),
    (N'myTasks.waitingBadge', N'ru', N'Задач, ожидающих вас: {n}'),
    (N'myTasks.waitingBadge', N'kz', N'Сізді күтетін тапсырмалар: {n}'),
    (N'err.ECR-REQ-0422.returnedSheetsCursorInvalid', N'ru', N'Курсор страницы возвращённых листов недействителен. Обновите список.'),
    (N'err.ECR-REQ-0422.returnedSheetsCursorInvalid', N'kz', N'Қайтарылған парақтар бетінің курсоры жарамсыз. Тізімді жаңартыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- P44A:returned-to-me ── кінець секції ──
