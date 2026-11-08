-- COLL:role-reassign-expired ── ru/kz: P1 08.11 — повторне призначення сплилої ролі (UQ_RoleAssignment_User → оновлення рядка / 409); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SEC-0409.roleAssignmentConflict', N'ru', N'Назначение этой роли пользователю только что изменил кто-то другой. Перечитайте роли пользователя и сохраните ещё раз.'),
    (N'err.ECR-SEC-0409.roleAssignmentConflict', N'kz', N'Бұл рөлдің пайдаланушыға тағайындалуын жаңа ғана басқа біреу өзгертті. Пайдаланушы рөлдерін қайта оқып, қайтадан сақтаңыз.'),
    (N'security.roleExpiredOn', N'ru', N'истекла {date}'),
    (N'security.roleExpiredOn', N'kz', N'мерзімі {date} аяқталды'),
    (N'security.roleExpiredReassign', N'ru', N'{role} — истекла {date}, назначить снова?'),
    (N'security.roleExpiredReassign', N'kz', N'{role} — мерзімі {date} аяқталды, қайта тағайындау керек пе?'),
    (N'security.roleReassignPending', N'ru', N'{role} — истекла {date}; при сохранении роль будет назначена снова без даты окончания.'),
    (N'security.roleReassignPending', N'kz', N'{role} — мерзімі {date} аяқталды; сақтағанда рөл аяқталу күнісіз қайта тағайындалады.'),
    (N'security.reassignRole', N'ru', N'Назначить снова'),
    (N'security.reassignRole', N'kz', N'Қайта тағайындау'),
    (N'security.reassignRoleNamed', N'ru', N'Назначить снова {role}'),
    (N'security.reassignRoleNamed', N'kz', N'{role} рөлін қайта тағайындау'),
    (N'security.roleValidUntil', N'ru', N'{role} — действует до {date}'),
    (N'security.roleValidUntil', N'kz', N'{role} — {date} дейін жарамды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:role-reassign-expired ── кінець секції ──

-- COLL:role-reassign-expired ── P1 08.11: сплила строкова роль у формі доступу — «expired <дата>, re-assign?»; 409 замість 500 на UQ_RoleAssignment_User; ru/kz — порцією COLL:role-reassign-expired вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SEC-0409.roleAssignmentConflict', N'en', N'Someone else has just changed this role assignment for the user. Reload the user''s roles and save again.', 1),
    (N'security.roleExpiredOn', N'en', N'expired on {date}', 1),
    (N'security.roleExpiredReassign', N'en', N'{role} — expired {date}, re-assign?', 1),
    (N'security.roleReassignPending', N'en', N'{role} — expired {date}; it will be re-assigned with no end date when you save.', 1),
    (N'security.reassignRole', N'en', N'Re-assign', 1),
    (N'security.reassignRoleNamed', N'en', N'Re-assign {role}', 1),
    (N'security.roleValidUntil', N'en', N'{role} — valid until {date}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:role-reassign-expired ── кінець секції ──
