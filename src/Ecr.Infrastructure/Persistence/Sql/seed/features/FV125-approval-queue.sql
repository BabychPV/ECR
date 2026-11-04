-- ФВ-12.5:approval-queue ── ru/kz блоку «До погодження»; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'myTasks.approval.title', N'ru', N'К согласованию'),
    (N'myTasks.approval.title', N'kz', N'Келісуге'),
    (N'myTasks.approval.error', N'ru', N'Не удалось загрузить листы, ожидающие вашего согласования.'),
    (N'myTasks.approval.error', N'kz', N'Сіздің келісуіңізді күтетін парақтарды жүктеу мүмкін болмады.'),
    (N'myTasks.approval.more', N'ru', N'Показать ещё'),
    (N'myTasks.approval.more', N'kz', N'Көбірек көрсету'),
    (N'myTasks.approval.open', N'ru', N'Проверить'),
    (N'myTasks.approval.open', N'kz', N'Қарау'),
    (N'myTasks.approval.submittedAt', N'ru', N'Подано {date}'),
    (N'myTasks.approval.submittedAt', N'kz', N'Жіберілген күні: {date}'),
    (N'myTasks.approval.submittedBy', N'ru', N'Подал(а) {name} · {date}'),
    (N'myTasks.approval.submittedBy', N'kz', N'Жіберген: {name} · {date}'),
    (N'myTasks.approval.step', N'ru', N'Шаг {n} из {count}'),
    (N'myTasks.approval.step', N'kz', N'{count} қадамның {n}-і')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- ФВ-12.5:approval-queue ── кінець секції ──
