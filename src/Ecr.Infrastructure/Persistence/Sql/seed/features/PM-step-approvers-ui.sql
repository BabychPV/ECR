-- COLL:pm-step-approvers-ui ── ru/kz: D-466 (AN-59, варіант C): швидкий перегляд документа - хто може діяти на поточному кроці; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.quickLookStepApprovers', N'ru', N'Могут утвердить сейчас'),
    (N'documents.quickLookStepApprovers', N'kz', N'Қазір бекіте алады'),
    (N'documents.quickLookStepApproversMore', N'ru', N'и другие'),
    (N'documents.quickLookStepApproversMore', N'kz', N'және басқалар'),
    (N'documents.quickLookStepApproversNone', N'ru', N'Текущий шаг утверждения некому утвердить: нет пользователя с нужными правами. Обратитесь к администратору.'),
    (N'documents.quickLookStepApproversNone', N'kz', N'Ағымдағы бекіту қадамын бекітетін ешкім жоқ: қажетті құқығы бар пайдаланушы жоқ. Әкімшіге хабарласыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:pm-step-approvers-ui ── кінець секції ──

-- COLL:pm-step-approvers-ui ── D-466: en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.quickLookStepApprovers', N'en', N'Can approve now', 1),
    (N'documents.quickLookStepApproversMore', N'en', N'and others', 1),
    (N'documents.quickLookStepApproversNone', N'en', N'Nobody can approve the current step: no user has the required rights. Contact an administrator.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:pm-step-approvers-ui ── кінець секції ──
