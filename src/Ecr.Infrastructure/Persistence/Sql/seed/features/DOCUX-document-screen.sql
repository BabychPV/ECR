-- DOCUX:document-screen ── ru/kz: екран документа — перерахунок за правдою сервера (L1), обов'язкові комірки (L2), рядок повідомлень і вкладка Status (L7), чипи стану (L8), подання з переходом до комірки (L9), чип розрахунку (L4); власна порція (docs/build/UX-REVIEW-document-screen.md) ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.recalculateSheetApproved', N'ru', N'Лист {sheet} утверждён: утверждённые показатели не пересчитываются. Чтобы их изменить, сначала верните лист на правки.'),
    (N'workflow.recalculateSheetApproved', N'kz', N'{sheet} парағы бекітілген: бекітілген көрсеткіштер қайта есептелмейді. Оларды өзгерту үшін алдымен парақты түзетуге қайтарыңыз.'),
    (N'workflow.recalculateAllApproved', N'ru', N'Все листы периода {period} утверждены: пересчитывать нечего. Чтобы изменить показатели, сначала верните лист на правки.'),
    (N'workflow.recalculateAllApproved', N'kz', N'{period} кезеңінің барлық парақтары бекітілген: қайта есептейтін ештеңе жоқ. Көрсеткіштерді өзгерту үшін алдымен парақты түзетуге қайтарыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOCUX:document-screen ── кінець секції ──

-- DOCUX:document-screen ── en: екран документа (L1/L2/L7/L8/L9/L4); ru/kz — порцією DOCUX:document-screen вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.recalculateSheetApproved', N'en', N'Sheet {sheet} is approved: approved figures are not recalculated. To change them, return the sheet for edits first.', 1),
    (N'workflow.recalculateAllApproved', N'en', N'All sheets of period {period} are approved: there is nothing to recalculate. To change the figures, return a sheet for edits first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOCUX:document-screen ── кінець секції ──
