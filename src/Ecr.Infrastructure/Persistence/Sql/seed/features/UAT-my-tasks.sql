-- UAT:my-tasks ── ru/kz: блоки «My tasks» згортаються, місяці одного аркуша — однією групою ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'myTasks.jobs.title', N'ru', N'Фоновые задачи'),
    (N'myTasks.approval.months.one', N'ru', N'{count} месяц'),
    (N'myTasks.approval.months.few', N'ru', N'{count} месяца'),
    (N'myTasks.approval.months.many', N'ru', N'{count} месяцев'),
    (N'myTasks.approval.months.other', N'ru', N'{count} месяца'),
    (N'myTasks.jobs.title', N'kz', N'Фондық тапсырмалар'),
    (N'myTasks.approval.months.one', N'kz', N'{count} ай'),
    (N'myTasks.approval.months.few', N'kz', N'{count} ай'),
    (N'myTasks.approval.months.many', N'kz', N'{count} ай'),
    (N'myTasks.approval.months.other', N'kz', N'{count} ай')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UAT:my-tasks ── кінець секції ──
-- UAT:my-tasks ── блоки «My tasks» згортаються, місяці одного аркуша — однією групою (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'myTasks.jobs.title', N'en', N'Background tasks', 1),
    (N'myTasks.approval.months.one', N'en', N'{count} month', 1),
    (N'myTasks.approval.months.few', N'en', N'{count} months', 1),
    (N'myTasks.approval.months.many', N'en', N'{count} months', 1),
    (N'myTasks.approval.months.other', N'en', N'{count} months', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UAT:my-tasks ── кінець секції ──
