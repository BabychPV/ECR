-- B80:source-binding ── ru/kz: право Calculation.ManageSourceBinding і відмови перемикання стану-воріт зв'язки (B80 N1); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permission.Calculation.ManageSourceBinding', N'ru', N'Управлять связями документа-источника'),
    (N'permission.Calculation.ManageSourceBinding', N'kz', N'Дереккөз құжатының байланыстарын басқару'),
    (N'err.ECR-CALC-0404.sourceBinding', N'ru', N'Связь документа-источника не найдена.'),
    (N'err.ECR-CALC-0404.sourceBinding', N'kz', N'Дереккөз құжатының байланысы табылмады.'),
    (N'err.ECR-REQ-0422.sourceStateInvalid', N'ru', N'Состояние источника должно быть any или approvedOnly.'),
    (N'err.ECR-REQ-0422.sourceStateInvalid', N'kz', N'Дереккөз күйі any немесе approvedOnly болуы керек.'),
    (N'err.ECR-CALC-0409.sourceBindingNoDocument', N'ru', N'«Только утверждённое» не включается: за текущий год нет документа-источника.'),
    (N'err.ECR-CALC-0409.sourceBindingNoDocument', N'kz', N'«Тек бекітілген» қосылмайды: ағымдағы жылға дереккөз құжаты жоқ.'),
    (N'err.ECR-CALC-0409.sourceBindingNotMonthly', N'ru', N'«Только утверждённое» не включается: документ-источник текущего года не в месячном проекте. Сначала перестройте его в месячном проекте.'),
    (N'err.ECR-CALC-0409.sourceBindingNotMonthly', N'kz', N'«Тек бекітілген» қосылмайды: ағымдағы жылдың дереккөз құжаты айлық жобада емес. Алдымен оны айлық жобада қайта құрыңыз.'),
    (N'err.ECR-CALC-0409.sourceBindingWorkflowNone', N'ru', N'«Только утверждённое» не включается: у шаблона документа-источника нет согласования.'),
    (N'err.ECR-CALC-0409.sourceBindingWorkflowNone', N'kz', N'«Тек бекітілген» қосылмайды: дереккөз құжатының үлгісінде келісу жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B80:source-binding ── кінець секції ──
-- B80:source-binding ── en: право Calculation.ManageSourceBinding і відмови перемикання стану-воріт зв'язки; ru/kz — порцією B80:source-binding вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permission.Calculation.ManageSourceBinding', N'en', N'Manage document source bindings', 1),
    (N'err.ECR-CALC-0404.sourceBinding', N'en', N'The document source binding was not found.', 1),
    (N'err.ECR-REQ-0422.sourceStateInvalid', N'en', N'The source state must be any or approvedOnly.', 1),
    (N'err.ECR-CALC-0409.sourceBindingNoDocument', N'en', N'«Approved only» cannot be turned on: there is no source document for the current year.', 1),
    (N'err.ECR-CALC-0409.sourceBindingNotMonthly', N'en', N'«Approved only» cannot be turned on: the source document of the current year is not in a monthly project. Rebuild it in a monthly project first.', 1),
    (N'err.ECR-CALC-0409.sourceBindingWorkflowNone', N'en', N'«Approved only» cannot be turned on: the source document template has no approval workflow.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B80:source-binding ── кінець секції ──
