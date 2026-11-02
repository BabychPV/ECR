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
