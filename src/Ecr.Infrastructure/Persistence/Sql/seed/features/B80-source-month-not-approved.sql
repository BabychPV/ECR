-- B80:source-month-not-approved ── ru/kz: B80 N2b (R3.1 A3), подання й затвердження Land за місяць, якого джерело HSE400 ще не затвердило; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SUB-4221.sourceMonthNotApproved', N'ru', N'Лист нельзя подать или утвердить: месяц {sourcePeriodKey} документа-источника ({sourceProjectCode}) ещё не утверждён. Сначала утвердите этот месяц документа-источника.'),
    (N'err.ECR-SUB-4221.sourceMonthNotApproved', N'kz', N'Парақты тапсыруға немесе бекітуге болмайды: бастапқы құжаттың ({sourceProjectCode}) {sourcePeriodKey} айы әлі бекітілмеген. Алдымен бастапқы құжаттың осы айын бекітіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B80:source-month-not-approved ── кінець секції ──

-- B80:source-month-not-approved ── en: відмова подання й затвердження споживача зв'язки ApprovedOnly (SourceMonthApprovalCheck); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SUB-4221.sourceMonthNotApproved', N'en', N'The sheet cannot be submitted or approved: month {sourcePeriodKey} of the source document ({sourceProjectCode}) is not approved yet. Approve that month of the source document first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B80:source-month-not-approved ── кінець секції ──
