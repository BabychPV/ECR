-- COLL:rc21misc ── ru/kz: RC21 дрібні пункти черги (EA6-D2, RC20 D-2/D-3/D-4); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.editAssignmentExpiresAtInvalid', N'ru', N'Срок действия передачи должен быть в будущем и не позже чем через 366 дней (UTC).'),
    (N'err.ECR-REQ-0422.editAssignmentExpiresAtInvalid', N'kz', N'Тапсыру мерзімі болашақта болуы және бүгіннен бастап 366 күннен кеш болмауы керек (UTC).'),
    (N'deny.NotAssignedToYou', N'ru', N'Эта часть вам не передана. Править можно только части, переданные вам владельцем; попросите владельца передать вам и эту.'),
    (N'deny.NotAssignedToYou', N'kz', N'Бұл бөлік сізге берілмеген. Тек иесі сізге тапсырған бөліктерді ғана өзгертуге болады; иесінен осы бөлікті де сізге беруін сұраңыз.'),
    (N'editAssign.notVisible', N'ru', N'(не видит документ)'),
    (N'editAssign.notVisible', N'kz', N'(құжатты көрмейді)'),
    (N'editAssign.notVisibleHint', N'ru', N'Этот человек не видит документ или лист, поэтому передача для него не действует. Попросите администратора выдать ему роль, открывающую документ, например «Исполнитель».'),
    (N'editAssign.notVisibleHint', N'kz', N'Бұл адам құжатты немесе парақты көрмейді, сондықтан тапсыру оған әсер етпейді. Әкімшіден оған құжатты ашатын рөлді, мысалы «Орындаушы» рөлін беруін сұраңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc21misc ── кінець секції ──

-- COLL:rc21misc ── RC21: дрібні пункти черги (EA6-D2 термін дії передачі, RC20 D-2/D-3/D-4); ru/kz — порцією COLL:rc21misc нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.editAssignmentExpiresAtInvalid', N'en', N'The assignment expiry must be in the future and no later than 366 days from now (UTC).', 1),
    (N'deny.NotAssignedToYou', N'en', N'This part is not assigned to you. You can edit only the parts the owner assigned to you; ask the owner to assign this one to you.', 1),
    (N'editAssign.notVisible', N'en', N'(cannot see the document)', 1),
    (N'editAssign.notVisibleHint', N'en', N'This person cannot see this document or sheet, so the assignment has no effect for them. Ask an administrator to grant them a role that opens it, for example the Executor role.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc21misc ── кінець секції ──
