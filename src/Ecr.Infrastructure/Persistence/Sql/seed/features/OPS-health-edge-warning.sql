-- OPS:health-edge-warning ── ru/kz PSS-L6b: інформаційна примітка на картці БД про рядки поза підготовленими річними партиціями; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.db.edgeWarning.title', N'ru', N'Часть документов относится к периодам вне подготовленных годовых секций'),
    (N'health.db.edgeWarning.title', N'kz', N'Кейбір құжаттар дайындалған жылдық бөлімдерден тыс кезеңдерге жатады'),
    (N'health.db.edgeWarning.text', N'ru', N'Это не ошибка: система работает как обычно. Когда будет удобно, посмотрите раздел 7.5 руководства по эксплуатации.'),
    (N'health.db.edgeWarning.text', N'kz', N'Бұл қате емес: жүйе әдеттегідей жұмыс істеп тұр. Ыңғайлы болғанда пайдалану нұсқаулығының 7.5-бөлімін қараңыз.'),
    (N'health.db.edgeWarning.rows', N'ru', N'Строк в крайних секциях: до первой границы — {first}, после последней — {last}.'),
    (N'health.db.edgeWarning.rows', N'kz', N'Шеткі бөлімдердегі жолдар: бірінші шекке дейін — {first}, соңғысынан кейін — {last}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- OPS:health-edge-warning ── кінець секції ──
-- OPS:health-edge-warning ── PSS-L6b: примітка про рядки в крайніх партиціях pf_ByPeriodKey (`DatabaseHealthCheck`, `edgeWarning`); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.db.edgeWarning.title', N'en', N'Some documents have periods outside the prepared yearly partitions', 1),
    (N'health.db.edgeWarning.text', N'en', N'This is not an error: the system works as usual. See the runbook, section 7.5, when convenient.', 1),
    (N'health.db.edgeWarning.rows', N'en', N'Rows in the edge partitions: before the first boundary - {first}, after the last - {last}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- OPS:health-edge-warning ── кінець секції ──
