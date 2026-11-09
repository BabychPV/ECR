-- COLL:role-permissions-edit ── ru/kz: (v) людина 08.11 — права власної ролі правляться (перегляд впливу, підтвердження небезпечних, 409); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SEC-0409.roleBuiltInPermissions', N'ru', N'Роль «{code}» встроенная: её права меняются только с обновлением продукта. Клонируйте роль и измените копию.'),
    (N'err.ECR-SEC-0409.roleBuiltInPermissions', N'kz', N'«{code}» рөлі кірістірілген: оның құқықтары тек өнім жаңартуымен өзгереді. Рөлді көшіріп, көшірмесін өзгертіңіз.'),
    (N'err.ECR-SEC-0409.permissionsStale', N'ru', N'Права роли «{code}» изменил кто-то другой после того, как вы их открыли. Перечитайте роль и повторите изменения.'),
    (N'err.ECR-SEC-0409.permissionsStale', N'kz', N'«{code}» рөлінің құқықтарын сіз ашқаннан кейін басқа біреу өзгертті. Рөлді қайта оқып, өзгерістерді қайталаңыз.'),
    (N'err.ECR-SEC-0409.dangerousPermissionsNeedConfirmation', N'ru', N'Роль «{code}» получит опасные права ({permissions}). Подтвердите их отдельно перед сохранением.'),
    (N'err.ECR-SEC-0409.dangerousPermissionsNeedConfirmation', N'kz', N'«{code}» рөлі қауіпті құқықтар алады ({permissions}). Сақтау алдында оларды бөлек растаңыз.'),
    (N'err.ECR-SEC-0409.roleKeepsLastAdministrator', N'ru', N'Без права управления пользователями в роли «{code}» управлять пользователями будет некому. Сначала выдайте это право другому активному пользователю.'),
    (N'err.ECR-SEC-0409.roleKeepsLastAdministrator', N'kz', N'«{code}» рөлінде пайдаланушыларды басқару құқығы болмаса, пайдаланушыларды басқаратын ешкім қалмайды. Алдымен бұл құқықты басқа белсенді пайдаланушыға беріңіз.'),
    (N'err.ECR-REQ-0422.rolePermissionsIfMatch', N'ru', N'Изменение прав роли должно указывать версию прочитанного набора (заголовок If-Match). Откройте просмотр изменений ещё раз.'),
    (N'err.ECR-REQ-0422.rolePermissionsIfMatch', N'kz', N'Рөл құқықтарын өзгерту оқылған жиынтықтың нұсқасын көрсетуі керек (If-Match тақырыбы). Өзгерістерді қарауды қайта ашыңыз.'),
    (N'security.editPermissions', N'ru', N'Изменить права'),
    (N'security.editPermissions', N'kz', N'Құқықтарды өзгерту'),
    (N'security.editingPermissionsTitle', N'ru', N'Изменение прав'),
    (N'security.editingPermissionsTitle', N'kz', N'Құқықтарды өзгерту'),
    (N'security.editingPermissionsHint', N'ru', N'Отметьте или снимите права. Ничего не изменится, пока вы не просмотрите влияние и не сохраните.'),
    (N'security.editingPermissionsHint', N'kz', N'Құқықтарды белгілеңіз немесе алып тастаңыз. Әсерін қарап, сақтамайынша ештеңе өзгермейді.'),
    (N'security.unsavedPermissionChanges', N'ru', N'Несохранённых изменений: {count}'),
    (N'security.unsavedPermissionChanges', N'kz', N'Сақталмаған өзгерістер: {count}'),
    (N'security.unsavedDangerousNote', N'ru', N'среди них опасных прав: {count}, понадобится подтверждение'),
    (N'security.unsavedDangerousNote', N'kz', N'олардың ішінде қауіпті құқықтар: {count}, растау қажет болады'),
    (N'security.discardPermissionChanges', N'ru', N'Отменить'),
    (N'security.discardPermissionChanges', N'kz', N'Бас тарту'),
    (N'security.reviewAndSave', N'ru', N'Просмотреть и сохранить…'),
    (N'security.reviewAndSave', N'kz', N'Қарап шығу және сақтау…'),
    (N'security.reviewTitle', N'ru', N'Проверка изменений · {role}'),
    (N'security.reviewTitle', N'kz', N'Өзгерістерді тексеру · {role}'),
    (N'security.impactChangesTitle', N'ru', N'Изменения: добавлено {added}, убрано {removed}'),
    (N'security.impactChangesTitle', N'kz', N'Өзгерістер: қосылды {added}, алынды {removed}'),
    (N'security.changeAdded', N'ru', N'Добавлено:'),
    (N'security.changeAdded', N'kz', N'Қосылды:'),
    (N'security.changeRemoved', N'ru', N'Убрано:'),
    (N'security.changeRemoved', N'kz', N'Алынды:'),
    (N'security.impactUsersTitle', N'ru', N'Пользователи с этой ролью: {count}'),
    (N'security.impactUsersTitle', N'kz', N'Осы рөлі бар пайдаланушылар: {count}'),
    (N'security.impactNoUsers', N'ru', N'Эта роль не назначена напрямую ни одному пользователю.'),
    (N'security.impactNoUsers', N'kz', N'Бұл рөл ешбір пайдаланушыға тікелей тағайындалмаған.'),
    (N'security.impactUser', N'ru', N'Пользователь'),
    (N'security.impactUser', N'kz', N'Пайдаланушы'),
    (N'security.impactAccess', N'ru', N'Прав до → после'),
    (N'security.impactAccess', N'kz', N'Құқықтар дейін → кейін'),
    (N'security.impactChange', N'ru', N'Что изменится для него'),
    (N'security.impactChange', N'kz', N'Ол үшін не өзгереді'),
    (N'security.impactCounts', N'ru', N'{before} → {after}'),
    (N'security.impactCounts', N'kz', N'{before} → {after}'),
    (N'security.impactNoChange', N'ru', N'Без изменений — то же даёт другая его роль'),
    (N'security.impactNoChange', N'kz', N'Өзгеріс жоқ — мұны оның басқа рөлі береді'),
    (N'security.impactGroupsTitle', N'ru', N'Группы с этой ролью: {count}'),
    (N'security.impactGroupsTitle', N'kz', N'Осы рөлі бар топтар: {count}'),
    (N'security.impactNoGroups', N'ru', N'Эта роль не назначена ни одной группе каталога.'),
    (N'security.impactNoGroups', N'kz', N'Бұл рөл каталогтың ешбір тобына тағайындалмаған.'),
    (N'security.impactGroupsHint', N'ru', N'Участники этих групп получат ровно изменения, перечисленные выше.'),
    (N'security.impactGroupsHint', N'kz', N'Бұл топтардың мүшелері дәл жоғарыда көрсетілген өзгерістерді алады.'),
    (N'security.impactSessionHint', N'ru', N'После сохранения пользователи с этой ролью выйдут из системы и получат новые права при следующем входе.'),
    (N'security.impactSessionHint', N'kz', N'Сақтағаннан кейін осы рөлі бар пайдаланушылар жүйеден шығады және келесі кіргенде жаңа құқықтарды алады.'),
    (N'security.dangerousConfirmTitle', N'ru', N'Опасные права: {count}'),
    (N'security.dangerousConfirmTitle', N'kz', N'Қауіпті құқықтар: {count}'),
    (N'security.dangerousConfirmLabel', N'ru', N'Подтверждаю, что роль будет давать: {permissions}'),
    (N'security.dangerousConfirmLabel', N'kz', N'Рөлдің мына құқықтарды беретінін растаймын: {permissions}'),
    (N'security.rolePermissionsChangedMeanwhile', N'ru', N'Права этой роли изменил кто-то другой после того, как вы начали правку. Перечитайте роль и повторите изменения.'),
    (N'security.rolePermissionsChangedMeanwhile', N'kz', N'Сіз өзгертуді бастағаннан кейін бұл рөлдің құқықтарын басқа біреу өзгертті. Рөлді қайта оқып, өзгерістерді қайталаңыз.'),
    (N'security.reloadRole', N'ru', N'Перечитать роль'),
    (N'security.reloadRole', N'kz', N'Рөлді қайта оқу'),
    (N'security.backToEditing', N'ru', N'Вернуться к правке'),
    (N'security.backToEditing', N'kz', N'Өзгертуге оралу'),
    (N'security.savePermissions', N'ru', N'Сохранить права'),
    (N'security.savePermissions', N'kz', N'Құқықтарды сақтау'),
    (N'security.rolePermissionsSaved', N'ru', N'Права роли {role} сохранены'),
    (N'security.rolePermissionsSaved', N'kz', N'{role} рөлінің құқықтары сақталды'),
    (N'audit.eventLabel.RolePermissionsChanged', N'ru', N'Изменены права роли'),
    (N'audit.eventLabel.RolePermissionsChanged', N'kz', N'Рөл құқықтары өзгертілді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:role-permissions-edit ── кінець секції ──

-- COLL:role-permissions-edit ── (v) людина 08.11: «Edit permissions» власної ролі, перегляд впливу, окреме підтвердження небезпечних прав, 409 на конкурентну правку; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SEC-0409.roleBuiltInPermissions', N'en', N'Role "{code}" is built in: its permissions change only with a product update. Clone the role and edit the copy.', 1),
    (N'err.ECR-SEC-0409.permissionsStale', N'en', N'Someone else changed the permissions of role "{code}" after you opened them. Reload the role and repeat your changes.', 1),
    (N'err.ECR-SEC-0409.dangerousPermissionsNeedConfirmation', N'en', N'Role "{code}" would get dangerous permissions ({permissions}). Confirm them separately before saving.', 1),
    (N'err.ECR-SEC-0409.roleKeepsLastAdministrator', N'en', N'Without the user-management permission in role "{code}" nobody would be left to manage users. First give this permission to another active user.', 1),
    (N'err.ECR-REQ-0422.rolePermissionsIfMatch', N'en', N'A change of role permissions must name the version of the set you read (If-Match header). Open the review of changes again.', 1),
    (N'security.editPermissions', N'en', N'Edit permissions', 1),
    (N'security.editingPermissionsTitle', N'en', N'Editing permissions', 1),
    (N'security.editingPermissionsHint', N'en', N'Tick or clear permissions. Nothing changes until you review the impact and save.', 1),
    (N'security.unsavedPermissionChanges', N'en', N'Unsaved changes: {count}', 1),
    (N'security.unsavedDangerousNote', N'en', N'includes dangerous permissions: {count}, you will be asked to confirm', 1),
    (N'security.discardPermissionChanges', N'en', N'Discard', 1),
    (N'security.reviewAndSave', N'en', N'Review and save…', 1),
    (N'security.reviewTitle', N'en', N'Review changes · {role}', 1),
    (N'security.impactChangesTitle', N'en', N'Changes: {added} added, {removed} removed', 1),
    (N'security.changeAdded', N'en', N'Added:', 1),
    (N'security.changeRemoved', N'en', N'Removed:', 1),
    (N'security.impactUsersTitle', N'en', N'Users with this role: {count}', 1),
    (N'security.impactNoUsers', N'en', N'No user has this role directly.', 1),
    (N'security.impactUser', N'en', N'User', 1),
    (N'security.impactAccess', N'en', N'Permissions before → after', 1),
    (N'security.impactChange', N'en', N'What changes for them', 1),
    (N'security.impactCounts', N'en', N'{before} → {after}', 1),
    (N'security.impactNoChange', N'en', N'No change — another of their roles gives the same', 1),
    (N'security.impactGroupsTitle', N'en', N'Groups with this role: {count}', 1),
    (N'security.impactNoGroups', N'en', N'No directory group has this role.', 1),
    (N'security.impactGroupsHint', N'en', N'Members of these groups get exactly the changes listed above.', 1),
    (N'security.impactSessionHint', N'en', N'After you save, people with this role are signed out and get the new permissions when they sign in again.', 1),
    (N'security.dangerousConfirmTitle', N'en', N'Dangerous permissions: {count}', 1),
    (N'security.dangerousConfirmLabel', N'en', N'I confirm that this role will grant: {permissions}', 1),
    (N'security.rolePermissionsChangedMeanwhile', N'en', N'Someone else changed the permissions of this role after you started editing. Reload the role and repeat your changes.', 1),
    (N'security.reloadRole', N'en', N'Reload role', 1),
    (N'security.backToEditing', N'en', N'Back to editing', 1),
    (N'security.savePermissions', N'en', N'Save permissions', 1),
    (N'security.rolePermissionsSaved', N'en', N'Permissions of {role} saved', 1),
    (N'audit.eventLabel.RolePermissionsChanged', N'en', N'Role permissions changed', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:role-permissions-edit ── кінець секції ──

-- COLL:role-permissions-edit ── змінені тексти наявних ключів: підпис під власною роллю — права тепер правляться (людина 08.11, (v)) ──
-- Нове значення стоїть у COLL-ui37security.sql (#UiEn — en, #I18N — ru/kz); епілог оновлює, лише поки в базі старе значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'security.customReadOnly', N'en',
     N'The permissions of a role are set when it is created. For a different set of permissions, create a new role.',
     N'A custom role: change its permissions with Edit permissions. Before saving you see who is affected.'),
    (N'security.customReadOnly', N'ru',
     N'Права роли задаются при её создании. Для другого набора прав создайте новую роль.',
     N'Собственная роль: её права меняются кнопкой «Изменить права». Перед сохранением видно, кого это затронет.'),
    (N'security.customReadOnly', N'kz',
     N'Рөл құқықтары ол құрылған кезде беріледі. Басқа құқықтар жиынтығы үшін жаңа рөл құрыңыз.',
     N'Өз рөліңіз: оның құқықтары «Құқықтарды өзгерту» батырмасымен өзгертіледі. Сақтау алдында кімге әсер ететіні көрінеді.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- COLL:role-permissions-edit ── кінець секції ──
