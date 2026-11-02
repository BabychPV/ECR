-- COLL:uidocgrid ── ru/kz UI-23/24/26: рядок стану сітки й банер стану аркуша; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.status.size', N'ru', N'Строк: {rows} · столбцов: {columns}'),
    (N'grid.status.size', N'kz', N'Жолдар: {rows} · бағандар: {columns}'),
    (N'grid.status.average', N'ru', N'Среднее'),
    (N'grid.status.average', N'kz', N'Орташа'),
    (N'grid.status.count', N'ru', N'Количество'),
    (N'grid.status.count', N'kz', N'Саны'),
    (N'grid.status.sum', N'ru', N'Сумма'),
    (N'grid.status.sum', N'kz', N'Сомасы'),
    (N'document.banner.submitted', N'ru', N'Отправлено на утверждение'),
    (N'document.banner.submitted', N'kz', N'Бекітуге жіберілді'),
    (N'document.banner.submittedBy', N'ru', N'Отправлено {date}, {name}'),
    (N'document.banner.submittedBy', N'kz', N'{date} жіберілді, {name}'),
    (N'document.banner.submittedDecide', N'ru', N'Ожидает вашего решения: проверьте цифры, затем утвердите или отклоните с указанием причины.'),
    (N'document.banner.submittedDecide', N'kz', N'Сіздің шешіміңізді күтуде: сандарды тексеріңіз, содан кейін бекітіңіз немесе себебін көрсетіп қабылдамаңыз.'),
    (N'document.banner.submittedWait', N'ru', N'Ожидает решения утверждающего.'),
    (N'document.banner.submittedWait', N'kz', N'Бекітушінің шешімін күтуде.'),
    (N'document.banner.approved', N'ru', N'Утверждено'),
    (N'document.banner.approved', N'kz', N'Бекітілді'),
    (N'document.banner.approvedBy', N'ru', N'Утверждено {date}, {name}'),
    (N'document.banner.approvedBy', N'kz', N'{date} бекітілді, {name}'),
    (N'document.banner.rejected', N'ru', N'Возвращено на доработку'),
    (N'document.banner.rejected', N'kz', N'Түзетуге қайтарылды'),
    (N'document.banner.rejectedBy', N'ru', N'Отклонено {date}, {name}'),
    (N'document.banner.rejectedBy', N'kz', N'{date} қабылданбады, {name}'),
    (N'document.banner.rejectedNext', N'ru', N'Исправьте цифры, проверьте лист и отправьте его снова.'),
    (N'document.banner.rejectedNext', N'kz', N'Сандарды түзетіп, парақты тексеріп, қайта жіберіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:uidocgrid ── кінець секції ──
-- COLL:uidocgrid ── UI-23/24/26: рядок стану сітки й банер стану аркуша (хто, коли, що далі); ru/kz — порцією COLL:uidocgrid у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.status.size', N'en', N'Rows: {rows} · columns: {columns}', 1),
    (N'grid.status.average', N'en', N'Average', 1),
    (N'grid.status.count', N'en', N'Count', 1),
    (N'grid.status.sum', N'en', N'Sum', 1),
    (N'document.banner.submitted', N'en', N'Submitted for approval', 1),
    (N'document.banner.submittedBy', N'en', N'Submitted {date} by {name}', 1),
    (N'document.banner.submittedDecide', N'en', N'Waiting for your decision: check the figures, then approve or reject with a reason.', 1),
    (N'document.banner.submittedWait', N'en', N'Waiting for the approver''s decision.', 1),
    (N'document.banner.approved', N'en', N'Approved', 1),
    (N'document.banner.approvedBy', N'en', N'Approved {date} by {name}', 1),
    (N'document.banner.rejected', N'en', N'Returned for corrections', 1),
    (N'document.banner.rejectedBy', N'en', N'Rejected {date} by {name}', 1),
    (N'document.banner.rejectedNext', N'en', N'Correct the figures, validate the sheet and submit it again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:uidocgrid ── кінець секції ──
