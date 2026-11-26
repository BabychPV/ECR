-- DOCUX:document-screen ── ru/kz: екран документа — перерахунок за правдою сервера (L1), обов'язкові комірки (L2), рядок повідомлень і вкладка Status (L7), чипи стану (L8), подання з переходом до комірки (L9), чип розрахунку (L4); власна порція (docs/build/UX-REVIEW-document-screen.md) ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.recalculateSheetApproved', N'ru', N'Лист {sheet} утверждён: утверждённые показатели не пересчитываются. Чтобы их изменить, сначала верните лист на правки.'),
    (N'workflow.recalculateSheetApproved', N'kz', N'{sheet} парағы бекітілген: бекітілген көрсеткіштер қайта есептелмейді. Оларды өзгерту үшін алдымен парақты түзетуге қайтарыңыз.'),
    (N'workflow.recalculateAllApproved', N'ru', N'Все листы периода {period} утверждены: пересчитывать нечего. Чтобы изменить показатели, сначала верните лист на правки.'),
    (N'workflow.recalculateAllApproved', N'kz', N'{period} кезеңінің барлық парақтары бекітілген: қайта есептейтін ештеңе жоқ. Көрсеткіштерді өзгерту үшін алдымен парақты түзетуге қайтарыңыз.'),
    (N'grid.needsInput.label', N'ru', N'Обязательно'),
    (N'grid.needsInput.label', N'kz', N'Міндетті'),
    (N'grid.needsInput.counter', N'ru', N'Не заполнено обязательных ячеек: {count}'),
    (N'grid.needsInput.counter', N'kz', N'{count} міндетті ұяшық толтырылмаған'),
    (N'grid.needsInput.listLabel', N'ru', N'Обязательные ячейки, которые нужно заполнить'),
    (N'grid.needsInput.listLabel', N'kz', N'Толтыру керек міндетті ұяшықтар'),
    (N'grid.needsInput.next', N'ru', N'Следующая обязательная ячейка (F8)'),
    (N'grid.needsInput.next', N'kz', N'Келесі міндетті ұяшық (F8)'),
    (N'grid.needsInput.prev', N'ru', N'Предыдущая обязательная ячейка (Shift+F8)'),
    (N'grid.needsInput.prev', N'kz', N'Алдыңғы міндетті ұяшық (Shift+F8)'),
    (N'grid.needsInput.none', N'ru', N'На этом листе больше нет незаполненных обязательных ячеек'),
    (N'grid.needsInput.none', N'kz', N'Бұл парақта толтырылмаған міндетті ұяшықтар қалмады'),
    (N'grid.needsInput.hintTemplate', N'ru', N'Обязательно — без этого значения лист нельзя подать'),
    (N'grid.needsInput.hintTemplate', N'kz', N'Міндетті — бұл мәнсіз парақты тапсыру мүмкін емес'),
    (N'grid.needsInput.hintMethodology', N'ru', N'Требуется для методологии «{methodologies}» — без этого значения строка не рассчитывается и лист нельзя подать'),
    (N'grid.needsInput.hintMethodology', N'kz', N'«{methodologies}» әдістемесі үшін қажет — бұл мәнсіз жол есептелмейді және парақты тапсыру мүмкін емес'),
    (N'grid.needsInput.columnCount', N'ru', N'В этом столбце не заполнено обязательных ячеек: {count} — перейти к первой'),
    (N'grid.needsInput.columnCount', N'kz', N'Бұл бағанда {count} міндетті ұяшық толтырылмаған — біріншісіне өту'),
    (N'grid.columnRequiredSomeRows', N'ru', N'Обязательно для части строк: это значение нужно их методологии'),
    (N'grid.columnRequiredSomeRows', N'kz', N'Кейбір жолдар үшін міндетті: бұл мән олардың әдістемесіне қажет'),
    (N'grid.keys.nextRequired', N'ru', N'Следующая / предыдущая обязательная ячейка'),
    (N'grid.keys.nextRequired', N'kz', N'Келесі / алдыңғы міндетті ұяшық'),
    (N'grid.needsInput.hintMethodologyUnnamed', N'ru', N'Требуется методологии этой строки — без этого значения строка не рассчитывается и лист нельзя подать'),
    (N'grid.needsInput.hintMethodologyUnnamed', N'kz', N'Бұл жолдың әдістемесіне қажет — бұл мәнсіз жол есептелмейді және парақты тапсыру мүмкін емес')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOCUX:document-screen ── кінець секції ──

-- DOCUX:document-screen ── en: екран документа (L1/L2/L7/L8/L9/L4); ru/kz — порцією DOCUX:document-screen вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.recalculateSheetApproved', N'en', N'Sheet {sheet} is approved: approved figures are not recalculated. To change them, return the sheet for edits first.', 1),
    (N'workflow.recalculateAllApproved', N'en', N'All sheets of period {period} are approved: there is nothing to recalculate. To change the figures, return a sheet for edits first.', 1),
    (N'grid.needsInput.label', N'en', N'Required', 1),
    (N'grid.needsInput.counter', N'en', N'{count} required cell(s) empty', 1),
    (N'grid.needsInput.listLabel', N'en', N'Required cells to fill', 1),
    (N'grid.needsInput.next', N'en', N'Next required cell (F8)', 1),
    (N'grid.needsInput.prev', N'en', N'Previous required cell (Shift+F8)', 1),
    (N'grid.needsInput.none', N'en', N'No more required cells to fill on this sheet', 1),
    (N'grid.needsInput.hintTemplate', N'en', N'Required — the sheet cannot be submitted without this value', 1),
    (N'grid.needsInput.hintMethodology', N'en', N'Required for methodology “{methodologies}” — without this value the row is not calculated and the sheet cannot be submitted', 1),
    (N'grid.needsInput.columnCount', N'en', N'{count} required cell(s) empty in this column — go to the first', 1),
    (N'grid.columnRequiredSomeRows', N'en', N'Required for some rows: their methodology needs this value', 1),
    (N'grid.keys.nextRequired', N'en', N'Next / previous required cell', 1),
    (N'grid.needsInput.hintMethodologyUnnamed', N'en', N'Required by the methodology of this row — without this value the row is not calculated and the sheet cannot be submitted', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOCUX:document-screen ── кінець секції ──
