-- ⚠ SET-опції задаються ЯВНО і першими.
-- `sqlcmd` за замовчуванням має `QUOTED_IDENTIFIER OFF`, а `SqlClient` — `ON`.
-- Через це скрипт, який проходить у тестах (їх виконує SqlClient), падає в
-- розгортанні (його виконує DBA через sqlcmd, `09-commands.md` §3) на будь-якій
-- таблиці з фільтрованим індексом або індексованою в'юхою. Опція ще й
-- ЗАПАМ'ЯТОВУЄТЬСЯ в момент створення процедури — тому її треба поставити до
-- першого `CREATE`, а не «якось у сесії».
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql
--
-- Seed чистої БД. Витягнуто ДОСЛІВНО з `02a-db-schema.md` §17 — щоб не
-- з'явилося другої, розбіжної копії. Правити треба контракт, а не цей файл.
--
-- Ідемпотентний: MERGE … WHEN NOT MATCHED. Повторний запуск не створює
-- дублікатів і не чіпає наявних рядків.
--
-- Виконується застосунком (`SeedRunner`), а не SQL Agent: без нього
-- застосунок не стартує (`02-contracts.md` §14).
--
-- ⚠ Потребує `08-system-tables.sql`: `sys_ecr.*` не має доменних сутностей,
-- тому міграція EF цих таблиць не створює.

-- Мови
MERGE sys_ecr.Language AS t
USING (VALUES (N'en', N'English', 1, 1), (N'ru', N'Русский', 2, 0), (N'kz', N'Қазақша', 3, 0))
      AS s (Code, NameNative, Ordinal, IsDefault)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, NameNative, Ordinal, IsDefault, IsActive)
     VALUES (s.Code, s.NameNative, s.Ordinal, s.IsDefault, 1);
GO

-- Функціональні права
MERGE sec.Permission AS t
USING (VALUES
  (N'Template.View',            N'Template',    0), (N'Template.Edit',        N'Template',    0),
  (N'Template.Publish',         N'Template',    0), (N'Template.Migrate',     N'Template',    0),
  (N'Registry.View',            N'Registry',    0), (N'Registry.EditData',    N'Registry',    0),
  (N'Registry.EditDefinition',  N'Registry',    0), (N'Registry.Publish',     N'Registry',    0),
  (N'Document.View',            N'Document',    0), (N'Document.Create',      N'Document',    0),
  (N'Document.Delete',          N'Document',    0), (N'Document.Import',      N'Document',    0),
  (N'Document.Export',          N'Document',    0), (N'Document.Reopen',      N'Document',    0),
  (N'Project.Manage',           N'Project',     0),
  (N'Period.Configure',         N'Period',      0), (N'Period.Reopen',        N'Period',      1),
  (N'Calculation.View',         N'Calculation', 0), (N'Calculation.EditFormula',  N'Calculation', 0),
  (N'Calculation.EditConstant', N'Calculation', 0), (N'Calculation.EditRule',     N'Calculation', 0),
  (N'Calculation.EditScript',   N'Calculation', 1), (N'Calculation.Publish',      N'Calculation', 1),
  (N'Calculation.Recalculate',  N'Calculation', 0),
  (N'Report.ViewRegulatory',    N'Report',      0), (N'Report.BuildSnapshot', N'Report',      0),
  (N'Report.MarkSubmitted',     N'Report',      0), (N'Report.Export',        N'Report',      0),
  (N'Integration.View',         N'Integration', 0), (N'Integration.Manage',   N'Integration', 1),
  (N'Integration.EditSchedule', N'Integration', 0),
  (N'Security.ManageUsers',     N'Security',    1), (N'Security.ManageRoles', N'Security',    1),
  (N'Security.ViewAudit',       N'Security',    0), (N'Security.Simulate',    N'Security',    1),
  (N'System.ViewHealth',        N'System',      0), (N'System.RunJob',        N'System',      1),
  (N'System.ManageLocalization', N'System',     0)
) AS s (Code, [Group], IsDangerous)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, [Group], NameL10n, IsDangerous)
     VALUES (s.Code, s.[Group], N'{"en":"' + s.Code + N'"}', s.IsDangerous);
GO

-- Розмірності
MERGE uom.Dimension AS t
USING (VALUES
  (1, N'Mass', 0, NULL, NULL),          (2, N'Volume', 0, NULL, NULL),
  (3, N'Energy', 0, NULL, NULL),        (4, N'Time', 0, NULL, NULL),
  (5, N'Temperature', 0, NULL, NULL),   (6, N'Amount', 0, NULL, NULL),
  (7, N'Dimensionless', 0, NULL, NULL),
  (8,  N'MassFlow',      1, 1, 4),      -- Mass / Time
  (9,  N'MassPerMass',   1, 1, 1),      -- Mass / Mass
  (10, N'MassPerEnergy', 1, 1, 3),      -- Mass / Energy
  (11, N'MassPerVolume', 1, 1, 2)       -- Mass / Volume
) AS s (Id, Code, IsDerived, Num, Den)
ON t.Id = s.Id
WHEN NOT MATCHED THEN INSERT (Id, Code, NameL10n, IsDerived, NumeratorDimensionId, DenominatorDimensionId)
     VALUES (s.Id, s.Code, N'{"en":"' + s.Code + N'"}', s.IsDerived, s.Num, s.Den);
GO

-- Базові одиниці. FactorToBase наявних одиниць МІНЯТИ ЗАБОРОНЕНО:
-- на них спираються фікстури і тести конверсій.
MERGE uom.Unit AS t
USING (VALUES
  (N'kg',   1, 1, 1.0,        0.0),     (N'm3',   2, 1, 1.0,      0.0),
  (N'J',    3, 1, 1.0,        0.0),     (N's',    4, 1, 1.0,      0.0),
  (N'K',    5, 1, 1.0,        0.0),     (N'mol',  6, 1, 1.0,      0.0),
  (N'one',  7, 1, 1.0,        0.0),
  (N't',    1, 0, 1000.0,     0.0),     (N'g',    1, 0, 0.001,    0.0),
  (N'mg',   1, 0, 0.000001,   0.0),
  (N'l',    2, 0, 0.001,      0.0),
  (N'GJ',   3, 0, 1000000000.0, 0.0),   (N'MWh',  3, 0, 3600000000.0, 0.0),
  (N'min',  4, 0, 60.0,       0.0),     (N'h',    4, 0, 3600.0,   0.0),
  (N'day',  4, 0, 86400.0,    0.0),     (N'year', 4, 0, 31536000.0, 0.0),
  (N'degC', 5, 0, 1.0,        273.15)
) AS s (Code, DimensionId, IsBase, Factor, [Offset])
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, SymbolL10n, NameL10n, DimensionId, IsBase, FactorToBase, OffsetToBase)
     VALUES (s.Code, N'{"en":"' + s.Code + N'"}', N'{"en":"' + s.Code + N'"}',
             s.DimensionId, s.IsBase, s.Factor, s.[Offset]);
GO

-- Похідні одиниці: складаються ПОСИЛАННЯМИ на чисельник і знаменник,
-- а не розбираються з рядка (ФВ-16.2)
MERGE uom.Unit AS t
USING (VALUES
  (N'g_per_s',    8,  N'g',  N's',    0.001),
  (N't_per_year', 8,  N't',  N'year', 0.0000317097919837646),   -- 1000 / 31536000
  (N'kg_per_t',   9,  N'kg', N't',    0.001),
  (N'g_per_GJ',   10, N'g',  N'GJ',   0.000000000001),
  (N'mg_per_m3',  11, N'mg', N'm3',   0.000001),
  (N'kg_per_m3',  11, N'kg', N'm3',   1.0)
) AS s (Code, DimensionId, NumCode, DenCode, Factor)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT
     (Code, SymbolL10n, NameL10n, DimensionId, IsBase, FactorToBase, OffsetToBase,
      NumeratorUnitId, DenominatorUnitId)
     VALUES (s.Code, N'{"en":"' + s.Code + N'"}', N'{"en":"' + s.Code + N'"}',
             s.DimensionId, 0, s.Factor, 0,
             (SELECT Id FROM uom.Unit WHERE Code = s.NumCode),
             (SELECT Id FROM uom.Unit WHERE Code = s.DenCode));
GO

UPDATE d SET BaseUnitId = u.Id
FROM uom.Dimension d
JOIN uom.Unit u ON u.DimensionId = d.Id AND u.IsBase = 1
WHERE d.BaseUnitId IS NULL;
GO

-- Політика паролів і вбудовані ролі
MERGE sec.PasswordPolicy AS t USING (VALUES (N'Default')) AS s (Code) ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code) VALUES (s.Code);
GO

-- ── Технічний обліковий запис інтеграції ─────────────────────────────────
--
-- ⛔ Матеріалізація точок AF пише в комірки ТИМ САМИМ шляхом, що й людина
-- (`D-118`), а цей шлях вимагає `ChangedByUserId` в аудиті. Тому запис у
-- `sec.User`, а не «системний нуль»: питання «хто змінив цю комірку» мусить
-- мати відповідь, і «нуль» відповіддю не є.
--
-- ⚠ Пароль — випадковий і НІКОМУ не відомий: увійти цим записом не можна й не
-- треба. Він не bootstrap і не отримує жодної ролі: перевірка доступу для
-- нього — лише стан періоду, а довіра походить від того, хто налаштував
-- мапінг (`Integration.Manage`, ФВ-12.10).
--
-- ⛔ Гранти на нього НЕ заводяться навмисно: їх довелося б виписувати на
-- кожен проєкт окремо, і забутий проєкт означав би мовчазну втрату даних
-- збору.
MERGE sec.[User] AS t
USING (VALUES (N'svc-integration')) AS s (UserName)
   ON t.UserName = s.UserName
WHEN NOT MATCHED THEN
    INSERT (UserName, DisplayName, Provider, PasswordHash, SecurityStamp,
            MustChangePassword, IsBootstrapAdmin, IsActive, CreatedAt)
    VALUES (s.UserName, N'Integration service', 1,
            CONVERT(nvarchar(400), HASHBYTES('SHA2_256', CAST(NEWID() AS nvarchar(64))), 2),
            REPLACE(CAST(NEWID() AS nvarchar(64)), N'-', N''),
            0, 0, 1, SYSUTCDATETIME());
GO

MERGE sec.Role AS t
USING (VALUES (N'SystemAdministrator'), (N'TemplateAdministrator'), (N'PeriodAdministrator'),
              (N'DataEntry'), (N'Approver'), (N'Viewer'), (N'Auditor'),
              (N'BootstrapAdministrator')) AS s (Code)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, NameL10n, IsBuiltIn)
     VALUES (s.Code, N'{"en":"' + s.Code + N'"}', 1);
GO

-- ── Права вбудованих ролей ───────────────────────────────────────────────
-- ⛔ НЕБЕЗПЕЧНІ права (IsDangerous = 1) сюди не потрапляють НІКОЛИ: фільтр
-- нижче стоїть у самому MERGE, а не в переліку пар. Різниця принципова —
-- перелік редагують руками і рано чи пізно допишуть у нього ще один рядок,
-- а фільтр не забудеш (ФВ-6.12, D-40). Такі права адміністратор додає
-- окремою свідомою дією, і в аудиті видно, хто це зробив.
--
-- ⚠ Без цього блоку жоден користувач не має ЖОДНОГО функціонального права —
-- включно з тим, кого щойно зробили SystemAdministrator. Ролі без прав
-- виглядають як робоча конфігурація і мовчки не працюють.
MERGE sec.RolePermission AS t
USING (
    SELECT r.Id AS RoleId, p.Code AS PermissionCode
    FROM (VALUES
        -- Системний адміністратор: усе, крім небезпечного.
        (N'SystemAdministrator', N'%'),

        -- Адміністратор шаблонів: структура і те, що потрібно її перевірити.
        (N'TemplateAdministrator', N'Template.%'),
        (N'TemplateAdministrator', N'Registry.View'),
        (N'TemplateAdministrator', N'Calculation.View'),
        (N'TemplateAdministrator', N'Document.View'),
        (N'TemplateAdministrator', N'System.ViewHealth'),

        -- Адміністратор періодів: календар і проєкт, без структури.
        (N'PeriodAdministrator', N'Period.%'),
        (N'PeriodAdministrator', N'Project.Manage'),
        (N'PeriodAdministrator', N'Document.View'),
        (N'PeriodAdministrator', N'System.ViewHealth'),

        -- Введення даних.
        (N'DataEntry', N'Document.View'),

        -- ⛔ `Template.View` роль мусить мати (`A7-54`). Створення документа
        -- починається з вибору шаблону і версії, а обидва переліки вимагають
        -- цього права. Без нього роль, вся суть якої — заповнювати документи,
        -- не могла створити ЖОДНОГО: діалог відкривався і показував порожні
        -- списки, бо сервер відповідав 403 на кожен із трьох запитів.
        (N'DataEntry', N'Template.View'),
        (N'DataEntry', N'Document.Create'),
        (N'DataEntry', N'Document.Import'),
        (N'DataEntry', N'Document.Export'),
        (N'DataEntry', N'Registry.View'),
        (N'DataEntry', N'Calculation.View'),
        (N'DataEntry', N'Report.Export'),

        -- Погоджувач: усе, що вміє DataEntry, плюс відповідальність за подане.
        (N'Approver', N'Document.View'),
        (N'Approver', N'Document.Export'),
        (N'Approver', N'Document.Reopen'),
        (N'Approver', N'Registry.View'),
        (N'Approver', N'Calculation.View'),
        (N'Approver', N'Report.%'),

        -- Перегляд.
        (N'Viewer', N'Document.View'),
        (N'Viewer', N'Registry.View'),
        (N'Viewer', N'Calculation.View'),
        (N'Viewer', N'Report.ViewRegulatory'),
        (N'Viewer', N'Report.Export'),

        -- Аудитор: бачить усе і не змінює нічого.
        (N'Auditor', N'Document.View'),
        (N'Auditor', N'Registry.View'),
        (N'Auditor', N'Calculation.View'),
        (N'Auditor', N'Template.View'),
        (N'Auditor', N'Integration.View'),
        (N'Auditor', N'Report.ViewRegulatory'),
        (N'Auditor', N'Security.ViewAudit'),
        (N'Auditor', N'System.ViewHealth')
    ) AS m (RoleCode, Pattern)
    JOIN sec.Role       AS r ON r.Code = m.RoleCode
    JOIN sec.Permission AS p ON p.Code LIKE m.Pattern
    WHERE p.IsDangerous = 0
) AS s
ON t.RoleId = s.RoleId AND t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN INSERT (RoleId, PermissionCode) VALUES (s.RoleId, s.PermissionCode);
GO

-- ── Роль первинного налаштування ─────────────────────────────────────────
-- ⚠ Роль лишається, а от НЕБЕЗПЕЧНІ ПРАВА їй більше не видаються (`D-121`).
-- Причина зникла разом із правилом «видати можна лише те, що маєш»: тепер
-- носій `Security.ManageRoles` видає будь-яке право, тому bootstrap-запису
-- досить самого `ManageRoles`, щоб передати систему людям.
--
-- ⛔ `Security.ManageRoles` сам небезпечний і в seed не дається НІКОМУ —
-- окрім цієї ролі. Саме тому коло не замикається: першим його має рівно один
-- запис, який вимикається, щойно з'являється доменний адміністратор.
MERGE sec.RolePermission AS t
USING (
    SELECT r.Id AS RoleId, p.Code AS PermissionCode
    FROM (VALUES (N'Security.ManageUsers'), (N'Security.ManageRoles')) AS m (Code)
    JOIN sec.Permission AS p ON p.Code = m.Code
    CROSS JOIN sec.Role AS r
    WHERE r.Code = N'BootstrapAdministrator'
) AS s
ON t.RoleId = s.RoleId AND t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN INSERT (RoleId, PermissionCode) VALUES (s.RoleId, s.PermissionCode);
GO
-- Політика періодів ECR
MERGE doc.PeriodPolicy AS t USING (VALUES (N'ECR-Standard', 0, 15, 45, 45))
      AS s (Code, O, G, H, Y) ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, OpenOffsetDays, GraceOffsetDays, HardCloseOffsetDays, YearGraceOffsetDays)
     VALUES (s.Code, s.O, s.G, s.H, s.Y);
GO

-- ── Каталог рядків інтерфейсу ────────────────────────────────────────────
-- ОБОВ'ЯЗКОВО: UiStringRevision має CHECK (Id = 1), тобто рівно один рядок.
-- Без нього запит версії поверне порожньо і ETag не сформується.
MERGE sys_ecr.UiStringRevision AS t USING (VALUES (1, 1)) AS s (Id, Rev)
      ON t.Id = s.Id
WHEN NOT MATCHED THEN INSERT (Id, Revision, ModifiedAt)
     VALUES (s.Id, s.Rev, SYSUTCDATETIME());
GO

-- Каталог рядків інтерфейсу мовою за замовчуванням.
--
-- ⛔ Тут ВЕСЬ набір ключів, які просить клієнт, а не «мінімальний». До `A7-12`
-- у seed були самі лише публічні рядки — та й ті під ключами `auth.*`,
-- яких клієнт не просить. Застосунок малював технічні ключі: кнопка
-- з написом `login.submit`, колонка `periods.grace`. Ключ без значення
-- показується як є (це навмисно — порожня кнопка гірша), тому дефект не
-- падав і не логувався: він просто був видимий усім.
--
-- ⚠ Область — це ВИДИМІСТЬ, а не рубрика, і ключ має рівно одну
-- (`PK_UiString ([Key], LanguageCode)`). Спільні ключі (`app.*`, `common.*`,
-- `err.*`) лежать у публічній: «Зберегти» і текст помилки не є
-- таємницею, а приватна область віддається разом із ними.
--
-- ⚠ Переклади іншими мовами — дані реєстру, а не збірки (D-95): вони
-- заводяться через `PUT /api/v1/ui-strings/{lang}/{key}` і сюди не потрапляють.
--
-- ⛔ Ключі `err.<код>` читає СЕРВЕР (ФВ-14.9a): `ExceptionHandlingMiddleware`
-- бере за ними `Title` відповіді `problem+json`. Доки цієї ланки не було, вони
-- лежали тут мертвими — і заголовком помилки їхав сам код, тобто користувач
-- бачив «ECR-AUTH-0423» замість «обліковий запис заблоковано», причому
-- будь-якою мовою однаково. Код при цьому нікуди не дівається: клієнт показує
-- його окремо і розрізняє причини САМЕ за ним, а не за текстом.
--
-- ⚠ Заведені лише коди, які видно ДО входу (ФВ-14.9b, публічна область).
-- Решту каталогу помилок заводить термінолог (`C-7`) записом у реєстр —
-- відсутній ключ повертає сам код, а не порожнечу, тому пропуск нічого не
-- ламає.
MERGE sys_ecr.UiString AS t
USING (VALUES
    (N'app.loading',       N'en', N'Loading...', 0),
    (N'common.save',       N'en', N'Save', 0),
    (N'common.cancel',     N'en', N'Cancel', 0),
    (N'common.retry',      N'en', N'Retry', 0),
    (N'common.delete',     N'en', N'Remove', 0),
    (N'common.loading',    N'en', N'Loading...', 0),
    (N'state.errorTitle',                N'en', N'The request failed', 0),
    (N'state.errorUnknown',              N'en', N'An unexpected error occurred. Retry; if it repeats, quote the code below to support.', 0),
    (N'state.emptyTitle',                N'en', N'Nothing here yet', 0),
    (N'login.title',       N'en', N'Environmental Compliance Reporting', 0),
    (N'login.windows',     N'en', N'Sign in with Windows', 0),
    (N'login.or',          N'en', N'or', 0),
    (N'login.user',        N'en', N'User name', 0),
    (N'login.password',    N'en', N'Password', 0),
    (N'login.submit',      N'en', N'Sign in', 0),
    (N'login.hint',        N'en', N'Use your Windows account, or the local account issued to you.', 0),
    (N'err.ECR-AUTH-0401', N'en', N'Sign in to continue.', 0),
    (N'err.ECR-AUTH-0403', N'en', N'You do not have permission for this action.', 0),
    (N'err.ECR-AUTH-0423', N'en', N'The account is locked.', 0),
    (N'err.ECR-PWD-0428',  N'en', N'Password change is required.', 0),
    (N'err.ECR-PWD-0422',  N'en', N'The new password does not meet the policy.', 0),

    -- Приватна область: усе, що видно лише після входу.
    (N'app.simulating',                  N'en', N'Viewing as {user}', 1),
    (N'nav.menu',                        N'en', N'Menu', 1),
    (N'nav.documents',                   N'en', N'Documents', 1),
    (N'nav.templates',                   N'en', N'Templates', 1),
    (N'nav.registries',                  N'en', N'Registries', 1),
    (N'nav.methodologies',               N'en', N'Methodologies', 1),
    (N'nav.expressions',                 N'en', N'Expressions', 1),
    (N'nav.security',                    N'en', N'Security', 1),
    (N'nav.periods',                     N'en', N'Periods', 1),
    (N'nav.sources',                     N'en', N'Sources', 1),
    (N'nav.jobs',                        N'en', N'Jobs', 1),
    (N'nav.health',                      N'en', N'Health', 1),
    (N'documents.title',                 N'en', N'Documents', 1),
    (N'documents.key',                   N'en', N'Key', 1),
    (N'documents.project',               N'en', N'Project', 1),
    (N'documents.period',                N'en', N'Period', 1),
    (N'documents.sheets',                N'en', N'Sheets', 1),
    (N'documents.state',                 N'en', N'State', 1),
    (N'documents.empty',                 N'en', N'No documents for this period.', 1),
    (N'documents.more',                  N'en', N'Load more', 1),
    (N'document.validate',               N'en', N'Validate', 1),
    (N'document.validationClean',        N'en', N'Validation passed with no errors.', 1),
    (N'document.validationErrors',       N'en', N'Validation found {count} error(s).', 1),
    (N'document.submit',                 N'en', N'Submit', 1),
    (N'document.submitted',              N'en', N'The sheet has been submitted.', 1),
    (N'document.export',                 N'en', N'Export to Excel', 1),
    (N'document.exportBuilding',         N'en', N'Building...', 1),
    (N'document.exportReady',            N'en', N'Download the workbook', 1),
    (N'document.noSheets',               N'en', N'This document has no sheets for the selected period.', 1),
    (N'grid.loading',                    N'en', N'Loading the table...', 1),
    (N'grid.loadFailed',                 N'en', N'The table could not be loaded.', 1),
    (N'grid.undo',                       N'en', N'Undo', 1),
    (N'grid.redo',                       N'en', N'Redo', 1),
    (N'grid.save',                       N'en', N'Save ({count})', 1),
    (N'grid.edit',                       N'en', N'Edit {column}', 1),
    (N'grid.paste',                      N'en', N'Paste {count} cell(s)', 1),
    (N'grid.conflictTitle',              N'en', N'Someone changed these cells', 1),
    (N'grid.conflictHint',               N'en', N'{count} cell(s) were changed by another user. Review them before saving again.', 1),
    (N'grid.rejectedTitle',              N'en', N'Some cells were not saved', 1),
    (N'grid.rejectedHint',               N'en', N'The cells below are read-only for you. Nothing from this paste was saved.', 1),
    (N'grid.unknownColumn',              N'en', N'There is no column {column} in this table.', 1),
    (N'grid.roundedTitle',               N'en', N'Rounded {count} value(s)', 1),
    (N'grid.roundedHint',                N'en', N'Extra decimals from the pasted sheet were rounded to the column scale. Nothing was rounded silently.', 1),
    (N'grid.roundedShow',                N'en', N'Show the list', 1),
    (N'deny.NoGrant',                    N'en', N'You do not have permission to edit this cell.', 1),
    (N'deny.PeriodNotOpenYet',           N'en', N'The period is not open yet: data entry starts on the opening date.', 1),
    (N'deny.PeriodClosed',               N'en', N'The period is closed: changes need a separate approval.', 1),
    (N'deny.OutOfAccessWindow',          N'en', N'The access window for this period and your role has already closed.', 1),
    (N'deny.DocumentSubmitted',          N'en', N'The document is submitted: it must be returned for rework first.', 1),
    (N'deny.DocumentApproved',           N'en', N'The document is approved: it must be returned for rework first.', 1),
    (N'deny.ColumnReadOnly',             N'en', N'The column is read-only by the template definition.', 1),
    (N'deny.RowReadOnly',                N'en', N'The row is read-only by the template definition.', 1),
    (N'deny.CalculatedCell',             N'en', N'The system computes this cell: its value changes on the next recalculation.', 1),
    (N'deny.ProjectArchived',            N'en', N'The project is archived: the data is read-only.', 1),
    (N'deny.ArchivingInProgress',        N'en', N'Archiving is running: writing is temporarily unavailable.', 1),
    (N'deny.BusinessRule',               N'en', N'A domain rule blocks this change.', 1),
    (N'deny.SimulationReadOnly',         N'en', N'Permission simulation: writing is disabled regardless of permissions.', 1),
    (N'deny.OutsidePermitWindow',        N'en', N'Outside the permit validity window: the permit did not cover this month.', 1),
    (N'deny.Unknown',                    N'en', N'Editing is blocked: {reason}.', 1),
    (N'password.title',                  N'en', N'Change password', 1),
    (N'password.current',                N'en', N'Current password', 1),
    (N'password.next',                   N'en', N'New password', 1),
    (N'password.repeat',                 N'en', N'Repeat the new password', 1),
    (N'password.submit',                 N'en', N'Change password', 1),
    (N'password.mismatch',               N'en', N'The two entries do not match.', 1),
    (N'password.policy',                 N'en', N'At least 12 characters, with upper case, lower case and a digit.', 1),
    (N'templates.title',                 N'en', N'Templates', 1),
    (N'templates.code',                  N'en', N'Code', 1),
    (N'templates.versions',              N'en', N'Versions', 1),
    (N'version.title',                   N'en', N'Template version', 1),
    (N'version.publish',                 N'en', N'Publish', 1),
    (N'version.published',               N'en', N'The version has been published.', 1),
    (N'version.column',                  N'en', N'Column', 1),
    (N'version.type',                    N'en', N'Type', 1),
    (N'version.unit',                    N'en', N'Unit', 1),
    (N'version.readOnly',                N'en', N'read-only', 1),
    (N'registries.title',                N'en', N'Registries', 1),
    (N'registries.pick',                 N'en', N'Pick a registry', 1),
    (N'registries.code',                 N'en', N'Code', 1),
    (N'registries.name',                 N'en', N'Name', 1),
    (N'registries.parent',               N'en', N'Parent', 1),
    (N'registries.fields',               N'en', N'Fields', 1),
    (N'registries.validity',             N'en', N'Valid', 1),
    (N'registries.hierarchical',         N'en', N'hierarchical', 1),
    (N'registries.temporal',             N'en', N'time-bound', 1),
    -- Редактор виразів (`ФВ-9.15a`, область 10).
    (N'expressions.title',               N'en', N'Expression editor', 1),
    (N'expressions.dialect',             N'en', N'Dialect', 1),
    (N'expressions.dialectTemplate',     N'en', N'Template formula', 1),
    (N'expressions.dialectMethodology',  N'en', N'Methodology formula', 1),
    (N'expressions.templateVersion',     N'en', N'Template version', 1),
    (N'expressions.methodologyVersion',  N'en', N'Methodology version', 1),
    (N'expressions.anyVersion',          N'en', N'Syntax only', 1),
    (N'expressions.editorLabel',         N'en', N'Expression', 1),

    -- ⛔ Позначка ярусу `Extension` у переліку автодоповнення (`02b` §8).
    -- Функція, якої чинний рушій не знає: у версії з `NumericMode = Legacy`
    -- вираз із нею не опублікується (`ECR-CALC-0433`), тому позначка стоїть
    -- у переліку, а не в описі під ним — рішення ухвалюють у мить вибору.
    (N'expressions.function.extension',   N'en', N'outside the current engine set', 1),
    (N'expressions.noFindings',          N'en', N'No findings', 1),
    (N'expressions.findings',            N'en', N'Findings: {count}', 1),
    (N'expressions.resultType',          N'en', N'Result type: {type}', 1),
    (N'expressions.startTyping',         N'en', N'Type an expression', 1),
    (N'expressions.startTypingHint',     N'en', N'It is checked exactly as publishing would check it.', 1),
    (N'expressions.skippedTitle',        N'en', N'Not checked here', 1),
    (N'expressions.runTests',            N'en', N'Run the golden set', 1),
    (N'expressions.testsGreen',          N'en', N'Green', 1),
    (N'expressions.testsRed',            N'en', N'Red', 1),
    (N'expressions.testCase',            N'en', N'Test', 1),
    (N'expressions.testOutput',          N'en', N'Output', 1),
    (N'expressions.testActual',          N'en', N'Actual', 1),
    (N'expressions.testExpected',        N'en', N'Expected', 1),
    (N'expressions.testTolerance',       N'en', N'Tolerance', 1),
    (N'expressions.testMissing',         N'en', N'not produced', 1),
    (N'expressions.noTestCases',         N'en', N'The version has no tests', 1),
    (N'expressions.noTestCasesHint',     N'en', N'An empty set is not a green one: publishing is refused without tests.', 1),
    (N'expressions.editorFailed',        N'en', N'The editor did not load', 1),
    (N'expressions.editorFailedHint',    N'en', N'Reload the page. Until it loads there is no expression checking, so entering text here would be unchecked.', 1),
    (N'expressions.check.References',    N'en', N'references to sheets, tables, rows and columns', 1),
    (N'expressions.check.Types',         N'en', N'type compatibility', 1),
    (N'expressions.check.Units',         N'en', N'unit compatibility', 1),
    (N'expressions.check.Cycle',         N'en', N'cycles between formulas — a property of the whole version, not of one expression', 1),
    (N'methodologies.title',             N'en', N'Methodologies', 1),
    (N'methodologies.code',              N'en', N'Code', 1),
    (N'methodologies.versions',          N'en', N'Versions', 1),
    (N'methodologies.publish',           N'en', N'Publish', 1),
    (N'methodologies.published',         N'en', N'The version has been published.', 1),
    (N'methodologies.publishTitle',      N'en', N'Publish methodology version', 1),
    (N'methodologies.reason',            N'en', N'Reason for the change', 1),
    (N'methodologies.reasonHint',        N'en', N'Recorded in the change log: it explains why past numbers were recalculated.', 1),
    (N'methodologies.effectiveFrom',     N'en', N'Effective from', 1),
    (N'methodologies.effectiveFromHint', N'en', N'Periods from this date on are calculated by this version; earlier ones keep the previous.', 1),
    (N'methodologies.versionsTitle',     N'en', N'Versions and formulas', 1),
    (N'methodologies.version',           N'en', N'Version', 1),
    (N'methodologies.status',            N'en', N'Status', 1),
    (N'methodologies.modes',             N'en', N'Modes', 1),
    (N'methodologies.openVersion',       N'en', N'Open', 1),
    (N'methodologies.noVersions',        N'en', N'This methodology has no versions', 1),
    (N'methodologies.noVersionsHint',    N'en', N'A version is what actually calculates. Create a draft to start.', 1),
    (N'methodologies.newVersion',        N'en', N'New version', 1),
    (N'methodologies.newVersionTitle',   N'en', N'New draft version', 1),
    (N'methodologies.createVersion',     N'en', N'Create draft', 1),
    (N'methodologies.versionCreated',    N'en', N'The draft has been created.', 1),
    (N'methodologies.versionNumber',     N'en', N'Version number', 1),
    (N'methodologies.versionNumberHint', N'en', N'Unique within the methodology: audit records refer to a version by this number.', 1),
    (N'methodologies.copyFrom',          N'en', N'Copy from', 1),
    (N'methodologies.copyFromHint',      N'en', N'Everything is copied: formulas, constants, rules, imports, substances, outputs and tests.', 1),
    (N'methodologies.emptyDraft',        N'en', N'Empty draft', 1),
    (N'methodologies.cloneHint',         N'en', N'A published version never changes: its numbers are already in submitted forms. Changing it means cloning it into a draft and publishing the draft with its own effective date.', 1),
    (N'methodologies.readOnly',          N'en', N'Read only', 1),
    (N'methodologies.readOnlyHint',      N'en', N'This version is published, so its content is fixed. To change it, create a new version copied from this one.', 1),
    (N'methodologies.formulas',          N'en', N'Formulas', 1),
    (N'methodologies.formulaTitle',      N'en', N'Formula', 1),
    (N'methodologies.formulaCode',       N'en', N'Code', 1),
    (N'methodologies.formulaCodeHint',   N'en', N'What expressions refer to as !Code. It cannot be renamed: other formulas point at it.', 1),
    (N'methodologies.expression',        N'en', N'Expression', 1),
    (N'methodologies.resultType',        N'en', N'Result type', 1),
    (N'methodologies.resultTypeHint',    N'en', N'Declared, not guessed: a formula may return a word, and that word must not land in a numeric column.', 1),
    (N'methodologies.resultNumber',      N'en', N'Number', 1),
    (N'methodologies.resultText',        N'en', N'Text', 1),
    (N'methodologies.outputUnit',        N'en', N'Result unit', 1),
    (N'methodologies.outputUnitHint',    N'en', N'Publishing checks dimensions against it; a text result has none.', 1),
    (N'methodologies.noUnit',            N'en', N'Dimensionless', 1),
    (N'methodologies.evaluationOrder',   N'en', N'Order', 1),
    (N'methodologies.addFormula',        N'en', N'Add formula', 1),
    (N'methodologies.editFormula',       N'en', N'Edit', 1),
    (N'methodologies.deleteFormula',     N'en', N'Remove', 1),
    (N'methodologies.saveFormula',       N'en', N'Save formula', 1),
    (N'methodologies.formulaSaved',      N'en', N'The formula has been saved.', 1),
    (N'methodologies.formulaDeleted',    N'en', N'The formula has been removed.', 1),
    (N'methodologies.noFormulas',        N'en', N'This version has no formulas', 1),
    (N'methodologies.noFormulasHint',    N'en', N'Add one, or copy the version from an existing one.', 1),
    (N'security.title',                  N'en', N'Security', 1),
    (N'security.roles',                  N'en', N'Roles', 1),
    (N'security.users',                  N'en', N'Users', 1),
    (N'security.grants',                 N'en', N'Grants', 1),
    (N'security.alerts',                 N'en', N'Alerts', 1),
    (N'security.alertsNeedEmail',        N'en', N'Set an email address first: there is nowhere to send alerts.', 1),
    (N'grants.pickRole',                 N'en', N'Pick a role', 1),
    (N'grants.pickRoleHint',             N'en', N'Pick a role to see what it opens access to.', 1),
    (N'grants.add',                      N'en', N'Add grant', 1),
    (N'grants.saved',                    N'en', N'Access updated; affected sessions revalidate immediately.', 1),
    (N'grants.kind',                     N'en', N'Resource kind', 1),
    (N'grants.resource',                 N'en', N'Resource id', 1),
    (N'grants.level',                    N'en', N'Level', 1),
    (N'grants.deny',                     N'en', N'Deny', 1),
    (N'grants.remove',                   N'en', N'Remove', 1),
    (N'security.role',                   N'en', N'Role', 1),
    (N'security.name',                   N'en', N'Name', 1),
    (N'security.login',                  N'en', N'Login', 1),
    (N'security.kind',                   N'en', N'Kind', 1),
    (N'security.userState',              N'en', N'State', 1),
    (N'security.builtIn',                N'en', N'built-in', 1),
    (N'security.dangerous',              N'en', N'{count} dangerous permission(s)', 1),
    (N'security.bootstrap',              N'en', N'bootstrap', 1),
    (N'security.lockedOut',              N'en', N'locked out', 1),
    (N'security.mustChangePassword',     N'en', N'must change password', 1),
    (N'periods.title',                   N'en', N'Periods', 1),
    (N'periods.pickProject',             N'en', N'Pick a project', 1),
    (N'periods.project',                 N'en', N'Project', 1),
    (N'periods.key',                     N'en', N'Period', 1),
    (N'periods.sequence',                N'en', N'Sequence', 1),
    (N'periods.range',                   N'en', N'Range', 1),
    (N'periods.state',                   N'en', N'State', 1),
    (N'periods.grace',                   N'en', N'Grace until', 1),
    (N'periods.activate',                N'en', N'Activate project', 1),
    (N'periods.activated',               N'en', N'The project is active: periods now follow their dates.', 1),
    (N'periods.draftHint',               N'en', N'The project is a draft: periods stay closed until it is activated.', 1),
    (N'sources.title',                   N'en', N'Sources', 1),
    (N'sources.entity',                  N'en', N'Entity', 1),
    (N'sources.transport',               N'en', N'Transport', 1),
    (N'sources.lastRun',                 N'en', N'Last run', 1),
    (N'sources.gap',                     N'en', N'Gaps', 1),
    (N'sources.never',                   N'en', N'never', 1),
    (N'sources.collect',                 N'en', N'Collect', 1),
    (N'sources.queued',                  N'en', N'Collection queued as job {job}.', 1),
    (N'jobs.title',                      N'en', N'Jobs', 1),
    (N'jobs.id',                         N'en', N'Job id', 1),
    (N'jobs.watch',                      N'en', N'Watch', 1),
    (N'health.title',                    N'en', N'Health', 1),
    (N'health.database',                 N'en', N'Database', 1),
    (N'documents.emptyHint',             N'en', N'Documents appear once the period is open and a template version is published.', 1),
    (N'document.noSheetsHint',           N'en', N'The period may not be open yet: sheet instances are created when it opens.', 1),
    (N'templates.empty',                 N'en', N'No templates yet', 1),
    (N'templates.emptyHint',             N'en', N'A template describes the sheets and columns operators fill in. Create one to start.', 1),
    (N'version.empty',                   N'en', N'This version has no sheets', 1),
    (N'version.emptyHint',               N'en', N'A version without sheets cannot be published: there is nothing to fill in.', 1),
    (N'registries.empty',                N'en', N'No registries yet', 1),
    (N'registries.emptyHint',            N'en', N'Registries hold the reference lists columns pick from: equipment, substances, units.', 1),
    (N'registries.pickHint',             N'en', N'Pick a registry above to see its entries and validity windows.', 1),
    (N'registries.noEntries',            N'en', N'This registry has no entries', 1),
    (N'registries.noEntriesHint',        N'en', N'Columns that look this registry up will offer nothing to choose from.', 1),
    (N'methodologies.empty',             N'en', N'No methodologies yet', 1),
    (N'methodologies.emptyHint',         N'en', N'A methodology holds the calculation rules a published version applies to a period.', 1),
    (N'security.noRoles',                N'en', N'No roles yet', 1),
    (N'security.noRolesHint',            N'en', N'A role groups permissions; grants then say which projects and sheets it opens.', 1),
    (N'security.noUsers',                N'en', N'No users yet', 1),
    (N'security.noUsersHint',            N'en', N'Domain users appear after their first sign-in; local accounts are created here.', 1),
    (N'grants.empty',                    N'en', N'This role has no grants', 1),
    (N'grants.emptyHint',                N'en', N'Permissions say what a person can do; grants say to which projects. Without a grant the role opens nothing.', 1),
    (N'periods.noProjects',              N'en', N'No projects yet', 1),
    (N'periods.noProjectsHint',          N'en', N'A project defines the reporting calendar: without one there are no periods.', 1),
    (N'periods.noPeriods',               N'en', N'This project has no periods', 1),
    (N'periods.noPeriodsHint',           N'en', N'Periods are generated from the project calendar; a draft project has none until it is activated.', 1),
    (N'sources.empty',                   N'en', N'No collection sources configured', 1),
    (N'sources.emptyHint',               N'en', N'Without sources the system works fine: data is entered by hand.', 1),
    (N'jobs.pick',                       N'en', N'Enter a job id', 1),
    (N'jobs.pickHint',                   N'en', N'Long operations return a job id; paste it here to follow the progress.', 1),
    (N'grid.emptyTable',                 N'en', N'This table has no columns for the selected period', 1),
    (N'grid.emptyTableHint',             N'en', N'The template version in force for this period defines no columns for the table.', 1),
    (N'health.noChecks',                 N'en', N'No health checks are registered', 1),
    (N'health.noChecksHint',             N'en', N'The server returned an empty report. That is a server configuration problem, not an empty system.', 1),
    (N'health.noDbDetails',              N'en', N'The database check returned no details', 1),
    (N'profile.theme',                   N'en', N'Theme', 1),
    (N'profile.themeAuto',               N'en', N'System', 1),
    (N'profile.themeLight',              N'en', N'Light', 1),
    (N'profile.themeDark',               N'en', N'Dark', 1),
    (N'profile.density',                 N'en', N'Row height', 1),
    (N'profile.densityCompact',          N'en', N'Compact', 1),
    (N'profile.densityComfortable',      N'en', N'Comfortable', 1),
    (N'profile.logout',                  N'en', N'Sign out', 1),

    -- Робочий процес аркуша: подання, погодження, повернення в роботу.
    --
    -- ⛔ Цих рядків не було, бо не було й кнопок: до аудиту (`A7-39`)
    -- затвердити документ через інтерфейс було неможливо, і робочий процес
    -- обривався на поданні.
    (N'workflow.approve',                N'en', N'Approve', 1),
    (N'workflow.reject',                 N'en', N'Reject', 1),
    (N'workflow.reopen',                 N'en', N'Return for edits', 1),
    (N'workflow.recalculate',            N'en', N'Recalculate', 1),
    (N'workflow.approved',               N'en', N'The sheet has been approved.', 1),
    (N'workflow.rejected',               N'en', N'The sheet has been returned to the author.', 1),
    (N'workflow.reopened',               N'en', N'The sheet is editable again.', 1),
    (N'workflow.recalcQueued',           N'en', N'Recalculation queued as job {job}.', 1),
    (N'workflow.reason',                 N'en', N'Reason', 1),
    (N'workflow.rejectTitle',            N'en', N'Reject the sheet', 1),
    (N'workflow.rejectHint',             N'en', N'Say what has to be corrected: the author sees this text and nothing else.', 1),
    (N'workflow.reopenTitle',            N'en', N'Return the sheet for edits', 1),
    (N'workflow.reopenHint',             N'en', N'Submitted figures are about to change. The reason stays in the audit trail for good.', 1),

    -- Імпорт із обов'язковим переглядом diff (модуль 6.10).
    (N'import.pick',                     N'en', N'Import from Excel', 1),
    (N'import.title',                    N'en', N'Review the import', 1),
    (N'import.changes',                  N'en', N'{count} change(s)', 1),
    (N'import.conflicts',                N'en', N'{count} conflict(s)', 1),
    (N'import.rejected',                 N'en', N'{count} rejected', 1),
    (N'import.blockedTitle',             N'en', N'This file cannot be applied as it is', 1),
    (N'import.blockedHint',              N'en', N'Partial application is not allowed: fix the file or refresh the sheet and import again.', 1),
    (N'import.noChanges',                N'en', N'The file matches the sheet: there is nothing to apply.', 1),
    (N'import.row',                      N'en', N'Row', 1),
    (N'import.column',                   N'en', N'Column', 1),
    (N'import.was',                      N'en', N'Was', 1),
    (N'import.becomes',                  N'en', N'Becomes', 1),
    (N'import.reason',                   N'en', N'Reason', 1),
    (N'import.apply',                    N'en', N'Apply', 1),
    (N'import.applied',                  N'en', N'The import has been applied.', 1),

    (N'grid.addRow',                     N'en', N'Add row', 1),

    -- Шаблони: створення, версії, презентаційний шар.
    (N'templates.create',                N'en', N'New template', 1),
    (N'templates.created',               N'en', N'The template has been created.', 1),
    (N'templates.name',                  N'en', N'Name', 1),
    (N'templates.nameHint',              N'en', N'Shown to operators; a language left blank falls back to the default one.', 1),
    (N'templates.codeHint',              N'en', N'The business key: projects refer to it, and it cannot be changed later.', 1),
    (N'templates.newVersion',            N'en', N'New version', 1),
    (N'templates.versionCreated',        N'en', N'The version has been created from the latest one.', 1),
    (N'templates.versionNumber',         N'en', N'Version number', 1),
    (N'templates.versionNumberHint',     N'en', N'Major.Minor.Patch.Build — the number says what kind of change this is.', 1),
    (N'version.clone',                   N'en', N'Clone version', 1),
    (N'version.diff',                    N'en', N'Compare versions', 1),
    (N'version.diffOther',               N'en', N'Compare with version id', 1),
    (N'version.diffOtherHint',           N'en', N'The other version to compare against; take the id from the template list.', 1),
    (N'version.diffPick',                N'en', N'Enter the other version', 1),
    (N'version.diffSame',                N'en', N'The versions are structurally identical', 1),
    (N'version.diffSameHint',            N'en', N'Nothing to migrate: documents can move between them freely.', 1),
    (N'version.diffElement',             N'en', N'Element', 1),
    (N'version.diffKind',                N'en', N'Change', 1),
    (N'version.diffClass',               N'en', N'Risk', 1),
    (N'version.diffAffected',            N'en', N'This version already has documents', 1),
    (N'version.diffAffectedHint',        N'en', N'{count} document(s) are bound to it. A Breaking change will be refused; a Guarded one needs a migration strategy.', 1),
    (N'version.accessMatrix',            N'en', N'Access matrix', 1),
    (N'version.accessMatrixSheet',       N'en', N'Sheet', 1),
    (N'version.accessMatrixHint',        N'en', N'Period x sheet, as the server will decide it', 1),
    (N'version.accessMatrixLegend',      N'en', N'Dot - open for entry; half circle - some tables of the sheet are locked; cross - the whole sheet is locked. Fix a wrong rule before publishing: after that the structure is frozen.', 1),
    (N'version.accessMatrixData',        N'en', N'depends on data', 1),
    (N'version.accessMatrixDataHint',    N'en', N'A rule of this sheet reads the document itself - the validity window of a registry entry chosen in the row, or a condition over row values. The picture below is therefore incomplete: in a real document some months may be locked.', 1),
    (N'version.accessEditable',          N'en', N'Open for entry', 1),
    (N'version.accessPartial',           N'en', N'Partly locked', 1),
    (N'version.accessBlocked',           N'en', N'Locked', 1),
    (N'version.deprecate',               N'en', N'Withdraw from use', 1),
    (N'version.deprecateHint',           N'en', N'The version is not deleted: projects already bound to it keep working. It simply stops being offered for new ones.', 1),
    (N'version.deprecated',              N'en', N'The version is withdrawn from use.', 1),
    (N'version.cloneHint',               N'en', N'A published version is frozen: structural changes go into a clone, keeping codes and row keys.', 1),
    (N'version.cloned',                  N'en', N'The clone is ready and open.', 1),
    (N'version.presentation',            N'en', N'Appearance', 1),
    (N'version.patched',                 N'en', N'Applied; the version is now at revision {revision}.', 1),
    (N'version.headerHint',              N'en', N'The heading operators see above the column.', 1),
    (N'version.displayFormat',           N'en', N'Display format', 1),
    (N'version.displayFormatHint',       N'en', N'How the number is shown; it does not change the stored value.', 1),
    (N'version.ordinal',                 N'en', N'Position', 1),
    (N'version.ordinalHint',             N'en', N'Display order only: formulas do not depend on it.', 1),
    (N'version.hidden',                  N'en', N'Hidden', 1),
    (N'version.hiddenHint',              N'en', N'The column stays in the structure and keeps its data; operators do not see it.', 1),

    -- Життєвий цикл проєкту і календар періодів.
    (N'periods.create',                  N'en', N'New project', 1),
    (N'periods.created',                 N'en', N'The project is created as a draft: activate it to open its periods.', 1),
    (N'periods.code',                    N'en', N'Code', 1),
    (N'periods.codeHint',                N'en', N'The business key: documents and reports refer to it and it cannot be changed later.', 1),
    (N'periods.name',                    N'en', N'Name', 1),
    (N'periods.kind',                    N'en', N'Reporting period', 1),
    (N'periods.kindHint',                N'en', N'Defines the whole calendar; it cannot be changed once periods exist.', 1),
    (N'periods.clone',                   N'en', N'Clone project', 1),
    (N'periods.cloneHint',               N'en', N'Registries, settings and the sheet composition are copied. Data is not.', 1),
    (N'periods.cloned',                  N'en', N'The clone is ready and selected.', 1),
    (N'periods.archive',                 N'en', N'Archive', 1),
    (N'periods.archived',                N'en', N'The project is archived. It is not deleted: submitted forms still refer to it.', 1),
    (N'periods.current',                 N'en', N'current', 1),
    (N'periods.pin',                     N'en', N'Make current', 1),
    (N'periods.pinHint',                 N'en', N'The calendar stops choosing the current period by itself. Say why.', 1),
    (N'periods.pinned',                  N'en', N'The current period is pinned.', 1),
    (N'periods.reopen',                  N'en', N'Reopen period', 1),
    (N'periods.reopenHint',              N'en', N'A closed period holds submitted reporting. The reason stays in the audit trail.', 1),
    (N'periods.reopened',                N'en', N'The period is open again.', 1),
    (N'periods.reopenedUntil',           N'en', N'open until {until}', 1),

    -- Безпека: ролі, користувачі, перегляд чужими правами.
    (N'security.createRole',             N'en', N'New role', 1),
    (N'security.roleCreated',            N'en', N'The role has been created. Grants say which projects it opens.', 1),
    (N'security.roleCode',               N'en', N'Code', 1),
    (N'security.roleCodeHint',           N'en', N'Used in grants and audit; it cannot be changed later.', 1),
    (N'security.roleName',               N'en', N'Name', 1),
    (N'security.permissions',            N'en', N'Permissions', 1),
    (N'security.permissionsHint',        N'en', N'What the role can do. Which projects it opens is a separate question — see Grants.', 1),
    (N'security.createUser',             N'en', N'New user', 1),
    (N'security.userCreated',            N'en', N'The user has been created.', 1),
    (N'security.kindHint',               N'en', N'A domain account is recognised by its SID; a local one signs in with a password.', 1),
    (N'security.sid',                    N'en', N'Domain SID', 1),
    (N'security.sidHint',                N'en', N'The account is recognised by the SID, not by the name: renaming in the directory keeps it working.', 1),
    (N'security.localHint',              N'en', N'A one-time password is issued by the server and must be changed at first sign-in.', 1),
    (N'security.simulate',               N'en', N'View as', 1),
    (N'security.simulateHint',           N'en', N'Read-only, and the session is recorded before any data is shown. Say why.', 1),
    (N'security.simulationStarted',      N'en', N'You are now viewing with this user''s permissions. Nothing can be changed.', 1),
    (N'security.simulationEnd',          N'en', N'Stop', 1),
    (N'security.simulationEnded',        N'en', N'Back to your own permissions.', 1),

    -- Створення документа.
    (N'documents.create',                N'en', N'New document', 1),
    (N'documents.created',               N'en', N'The document has been created.', 1),
    (N'documents.version',               N'en', N'Template version', 1),
    (N'documents.versionHint',           N'en', N'Only published versions: a draft has no frozen structure.', 1),
    (N'documents.pickVersion',           N'en', N'Pick a version', 1),
    (N'documents.sheetsHint',            N'en', N'Composition rules are checked by the server: a group may require all of its sheets, or exactly one.', 1),

    -- Записи довідників і вікна чинності.
    (N'registries.newEntry',             N'en', N'New entry', 1),
    (N'registries.editEntry',            N'en', N'Edit', 1),
    (N'registries.entryCreated',         N'en', N'The entry has been created.', 1),
    (N'registries.entrySaved',           N'en', N'The entry has been saved.', 1),
    (N'registries.entryCodeHint',        N'en', N'Cells store the entry id, so the code can change; the entry itself is never deleted.', 1),
    (N'registries.validFrom',            N'en', N'Valid from', 1),
    (N'registries.validTo',              N'en', N'Valid to', 1),
    (N'registries.validityHint',         N'en', N'This replaces deletion. Rows referring to the entry outside the window become orphaned and block submission.', 1),
    (N'registries.validitySaved',        N'en', N'Saved. Rows affected: {count}.', 1),

    -- Прогін методології.
    (N'methodologies.simulate',          N'en', N'Simulate', 1),
    (N'methodologies.simulatePeriodHint', N'en', N'Nothing is stored: this shows what publishing would produce.', 1),
    (N'methodologies.run',               N'en', N'Run', 1),
    (N'methodologies.outputs',           N'en', N'Outputs', 1),
    (N'methodologies.output',            N'en', N'Output', 1),
    (N'methodologies.value',             N'en', N'Value', 1),
    (N'methodologies.diff',              N'en', N'Difference from published', 1),
    (N'methodologies.trace',             N'en', N'Trace', 1),

    -- Зрізи регламентної звітності.
    (N'registries.sourceSwitch',         N'en', N'Switch master source', 1),
    (N'registries.sourceKind',           N'en', N'New master source', 1),
    (N'registries.sourceSwitched',       N'en', N'{count} registry(ies) switched.', 1),
    (N'registries.sourceSwitchHint',     N'en', N'The set is switched as one operation', 1),
    (N'registries.sourceSwitchWarning',  N'en', N'All or nothing: an unknown code rejects the whole operation, and the check for open periods is done once for the set. Not allowed while any period is open: part of the documents would be filled against one list of entries and part against another.', 1),
    (N'registries.sourceReasonHint',     N'en', N'A year from now this is the only question that will need an answer: why these registries were switched together.', 1),

    -- Конструктор довідника (ФВ-8.12): поля, зв'язки, правила, мапінг, історія.
    (N'registries.constructor',          N'en', N'Registry designer', 1),
    (N'registries.tabFields',            N'en', N'Fields', 1),
    (N'registries.tabRelations',         N'en', N'Relations', 1),
    (N'registries.tabRules',             N'en', N'Rules', 1),
    (N'registries.tabMapping',           N'en', N'Mapping', 1),
    (N'registries.tabHistory',           N'en', N'History', 1),
    (N'registries.yes',                  N'en', N'yes', 1),
    (N'registries.field',                N'en', N'Field', 1),
    (N'registries.dataType',             N'en', N'Type', 1),
    (N'registries.required',             N'en', N'Required', 1),
    (N'registries.keyField',             N'en', N'Key', 1),
    (N'registries.lookup',               N'en', N'Looks up', 1),
    (N'registries.relationKind',         N'en', N'Relation', 1),
    (N'registries.target',               N'en', N'Target', 1),
    (N'registries.links',                N'en', N'Links', 1),
    -- ⚠ Підказка каже прямо, що зв'язки ОБЧИСЛЕНІ: таблиці опису відношень у
    -- схемі немає (Q-027), і перелік, який виглядає як налаштування, спонукав
    -- би шукати, де його редагувати.
    (N'registries.relationsHint',        N'en', N'Relations are derived from what exists: a field pointing at another registry, and the many-to-many links present in the data.', 1),
    (N'registries.ruleCode',             N'en', N'Code', 1),
    (N'registries.ruleKind',             N'en', N'Kind', 1),
    (N'registries.expression',           N'en', N'Condition', 1),
    (N'registries.severity',             N'en', N'Severity', 1),
    (N'registries.ruleActive',           N'en', N'Active', 1),
    (N'registries.ruleIncomplete',       N'en', N'A rule without a condition never fires and still looks configured.', 1),
    -- ⛔ Число «чотири» названо прямо: перелік, у якому хтось побачить п'ятий
    -- вид, і є той дефект, від якого стереже H-10.
    (N'registries.rulesHint',            N'en', N'Four kinds, and exactly four. A validity window is not a rule: it is the entry''s own valid-from and valid-to.', 1),
    (N'registries.addRule',              N'en', N'Add rule', 1),
    (N'registries.saveDefinition',       N'en', N'Save definition', 1),
    (N'registries.definitionSaved',      N'en', N'Saved. Definition version: {version}.', 1),
    (N'registries.definitionVersion',    N'en', N'Definition v{version}', 1),
    (N'registries.reason',               N'en', N'Reason', 1),
    (N'registries.reasonHint',           N'en', N'The definition changes how already stored entries are read; a year from now this is the answer to "why is this field here".', 1),
    (N'registries.noRules',              N'en', N'This registry has no rules', 1),
    (N'registries.noRelations',          N'en', N'This registry is not linked to any other', 1),
    (N'registries.noMappings',           N'en', N'No field of this registry is filled from an external source', 1),
    (N'registries.noHistory',            N'en', N'The definition has not been changed yet', 1),
    (N'registries.source',               N'en', N'Source', 1),
    (N'registries.sourceField',          N'en', N'Source field', 1),
    (N'registries.transform',            N'en', N'Fold', 1),
    (N'registries.units',                N'en', N'Units', 1),
    (N'registries.changedAt',            N'en', N'Changed at', 1),
    (N'registries.operation',            N'en', N'Operation', 1),
    (N'registries.author',               N'en', N'Author', 1),
    (N'periods.timeZone',                N'en', N'Site time zone (IANA)', 1),
    -- ⚠ Підказка називає IANA і незмінність разом: поле обов'язкове і без
    -- початкового значення (H-13), тож користувач має знати обидві причини,
    -- перш ніж обере — після відкриття першого періоду вибір остаточний.
    (N'periods.timeZoneHint',            N'en', N'IANA identifier of the site, for example Asia/Aqtau. Period boundaries and late-edit marks are calculated in this zone, and it cannot be changed once the first period is open.', 1),
    (N'periods.templateVersion',         N'en', N'Template version', 1),
    (N'periods.templateVersionHint',     N'en', N'Published versions only: a draft has no frozen structure.', 1),
    (N'periods.policy',                  N'en', N'Period policy', 1),
    (N'periods.policyHint',              N'en', N'Grace and hard-close offsets in days; they define when a period stops accepting data.', 1),
    (N'workflow.route',                  N'en', N'Approval route', 1),
    (N'workflow.routeHint',              N'en', N'Who approves, and in what order', 1),
    (N'workflow.routeEmptyHint',         N'en', N'No route means single-stage approval: one holder of the Approve level is enough. Removing every step returns the project to that.', 1),
    (N'workflow.routeNone',              N'en', N'No steps: approval is single-stage.', 1),
    (N'workflow.routeAddStep',           N'en', N'Add a step', 1),
    (N'workflow.routeAddStepHint',       N'en', N'The role that approves at this step. The same role may appear twice, but not twice in a row.', 1),
    (N'workflow.routeSaved',             N'en', N'The route now has {count} step(s).', 1),
    (N'workflow.routeCleared',           N'en', N'The route is removed: approval is single-stage again.', 1),
    (N'security.access',                 N'en', N'Access', 1),
    (N'security.accessSaved',            N'en', N'Saved: {count} role(s) assigned.', 1),
    (N'security.rolesHint',              N'en', N'The whole set at once: these roles are the person''s authority, and it should be seen as a whole.', 1),
    (N'security.noRolesTitle',           N'en', N'No roles assigned', 1),
    (N'security.noRolesWarning',         N'en', N'The account will open, and every screen will be empty. Assign at least one role.', 1),
    (N'security.email',                  N'en', N'Email', 1),
    (N'security.emailHint',              N'en', N'Without it no notification reaches this person, and the alerts switch stays off.', 1),
    (N'security.oneTimePassword',        N'en', N'One-time password', 1),
    (N'security.oneTimePasswordHint',    N'en', N'You will have to pass it on yourself. The server neither generates nor returns passwords, and the account must change it at first sign-in.', 1),

    -- «Мої групи»: чому в мене немає доступу (`H-21`).
    (N'nav.myGroups',                    N'en', N'My groups', 1),
    (N'myGroups.title',                  N'en', N'My groups and roles', 1),
    (N'myGroups.hint',                   N'en', N'Roles of domain accounts are assigned to AD groups, and membership comes from your sign-in ticket. This page shows which of your groups produced roles, and which produced nothing.', 1),
    (N'myGroups.ticketGroups',           N'en', N'Groups in your sign-in ticket', 1),
    (N'myGroups.ticketGroupsHint',       N'en', N'Every group from the ticket, matched or not. A group that produced nothing is what the directory team needs from you.', 1),
    (N'myGroups.sid',                    N'en', N'Security identifier', 1),
    (N'myGroups.matched',                N'en', N'Produced a role', 1),
    (N'myGroups.yes',                    N'en', N'yes', 1),
    (N'myGroups.no',                     N'en', N'no', 1),
    (N'myGroups.effective',              N'en', N'Roles you actually have', 1),
    (N'myGroups.noRoles',                N'en', N'None. Every screen will be empty, and that is not a fault of the data.', 1),
    (N'myGroups.personal',               N'en', N'Assigned to you personally: {roles}', 1),
    (N'myGroups.expired',                N'en', N'Assigned but no longer in force: {roles}. A dated assignment has ended.', 1),
    (N'myGroups.noSidsTitle',            N'en', N'Your ticket carries no group at all', 1),
    (N'myGroups.noSidsHint',             N'en', N'That is a setup question rather than a permissions one: a local account has no groups, and a domain sign-in that carries none means the ticket was issued without them.', 1),
    (N'myGroups.unmatchedTitle',         N'en', N'{count} group(s) produced nothing', 1),
    (N'myGroups.unmatchedHint',          N'en', N'The system knows the groups but grants no role through them. Quote the identifiers below when you ask the directory team.', 1),
    (N'myGroups.notMineTitle',           N'en', N'Group membership of another account is unknown here', 1),
    (N'myGroups.notMineHint',            N'en', N'Membership arrives in the sign-in ticket, and this person''s ticket is not ours to read. What is listed instead is which groups grant roles at all.', 1),
    (N'myGroups.catalogue',              N'en', N'Groups that grant roles', 1),
    (N'myGroups.catalogueHint',          N'en', N'Add the person to one of these groups in the directory; nothing else here grants a role.', 1),
    (N'myGroups.other',                  N'en', N'Another account', 1),
    (N'myGroups.otherHint',              N'en', N'Answers "why do I have no access" without signing in as that person.', 1),
    (N'myGroups.otherPlaceholder',       N'en', N'Pick an account', 1),
    (N'nav.units',                       N'en', N'Units', 1),
    (N'units.title',                     N'en', N'Units of measure', 1),
    (N'units.value',                     N'en', N'Value', 1),
    (N'units.from',                      N'en', N'From', 1),
    (N'units.to',                        N'en', N'To', 1),
    (N'units.pickFrom',                  N'en', N'Pick the source unit first', 1),
    (N'units.convert',                   N'en', N'Convert', 1),
    (N'units.code',                      N'en', N'Unit', 1),
    (N'units.dimension',                 N'en', N'Dimension', 1),
    (N'units.factor',                    N'en', N'Factor to base', 1),
    (N'units.offset',                    N'en', N'Offset to base', 1),
    (N'units.base',                      N'en', N'base', 1),
    (N'units.empty',                     N'en', N'No units registered', 1),
    (N'units.emptyHint',                 N'en', N'Without units a formula cannot state what its numbers mean, and conversion is impossible.', 1),
    (N'nav.audit',                       N'en', N'Audit trail', 1),
    (N'audit.title',                     N'en', N'Audit trail', 1),
    (N'audit.from',                      N'en', N'From', 1),
    (N'audit.to',                        N'en', N'To', 1),
    (N'audit.document',                  N'en', N'Document', 1),
    (N'audit.documentHint',              N'en', N'Leave empty for all documents in the window.', 1),
    (N'audit.when',                      N'en', N'Changed at', 1),
    (N'audit.who',                       N'en', N'By user', 1),
    (N'audit.cell',                      N'en', N'Row and column', 1),
    (N'audit.origin',                    N'en', N'Origin', 1),
    (N'audit.late',                      N'en', N'late', 1),
    (N'audit.empty',                     N'en', N'No changes in this window', 1),
    (N'audit.emptyHint',                 N'en', N'The window is required: the journal is partitioned by change time, and a query without one would scan every partition.', 1),
    (N'nav.snapshots',                   N'en', N'Report snapshots', 1),
    (N'snapshots.title',                 N'en', N'Report snapshots', 1),
    (N'snapshots.build',                 N'en', N'Build snapshot', 1),
    (N'snapshots.buildHint',             N'en', N'A snapshot is immutable: building again creates a new one instead of overwriting.', 1),
    (N'snapshots.queued',                N'en', N'Build queued as job {job}.', 1),
    (N'snapshots.code',                  N'en', N'Report code', 1),
    (N'snapshots.codeHint',              N'en', N'The report definition to build from; definitions are data, not code.', 1),
    (N'snapshots.builtAt',               N'en', N'Built at', 1),
    (N'snapshots.rows',                  N'en', N'Rows', 1),
    (N'snapshots.status',                N'en', N'Status', 1),
    (N'snapshots.hash',                  N'en', N'Content hash', 1),
    (N'snapshots.current',               N'en', N'current', 1),
    (N'snapshots.empty',                 N'en', N'No snapshots built yet', 1),
    (N'snapshots.emptyHint',             N'en', N'SSRS reads snapshots, not live data: until one is built, the regulator sees nothing.', 1),

    -- Редактор рядків інтерфейсу.
    (N'nav.uiStrings',                   N'en', N'Interface texts', 1),
    (N'uiStrings.title',                 N'en', N'Interface texts', 1),
    (N'uiStrings.language',              N'en', N'Language', 1),
    (N'uiStrings.filter',                N'en', N'Filter by key', 1),
    (N'uiStrings.key',                   N'en', N'Key', 1),
    (N'uiStrings.original',              N'en', N'Default language', 1),
    (N'uiStrings.translation',           N'en', N'Translation', 1),
    (N'uiStrings.untranslated',          N'en', N'not translated', 1),
    (N'uiStrings.edit',                  N'en', N'Edit', 1),
    (N'uiStrings.saved',                 N'en', N'Saved; the catalogue is now at revision {revision}.', 1),
    (N'uiStrings.empty',                 N'en', N'No keys match', 1),
    (N'uiStrings.emptyHint',             N'en', N'The catalogue is filled from the default language; clear the filter to see everything.', 1),

    -- Перегляд мапінгу на реальних рядках джерела (`ФВ-13.14`).
    --
    -- ⛔ Половина цих рядків — про РОЗРИВИ, і формулювання тут важить не менше
    -- за код: «no data» нічого не пояснює, а «the mapping names a source field
    -- that never appears» називає дефект і його причину. Підпис, який не
    -- пояснює знахідку, перетворює перелік розривів на шум.
    (N'nav.mapping',                     N'en', N'Mapping preview', 1),
    (N'mapping.title',                   N'en', N'Mapping preview', 1),
    (N'mapping.entity',                  N'en', N'Source entity', 1),
    (N'mapping.pick',                    N'en', N'Pick an entity', 1),
    (N'mapping.pickHint',                N'en', N'Pick a source entity above to see where its rows land and what is missing.', 1),
    (N'mapping.window',                  N'en', N'Last {days} days; {points} collected rows.', 1),
    (N'mapping.truncated',               N'en', N'Only part of the window is shown', 1),
    (N'mapping.truncatedHint',           N'en', N'There are more rows than the preview reads, so the folded values are incomplete. Narrow the window before trusting a number.', 1),
    (N'mapping.gaps',                    N'en', N'Gaps', 1),
    (N'mapping.noGaps',                  N'en', N'No gaps: every source field lands somewhere, every mapping has rows behind it, and every column has something filling it.', 1),
    (N'mapping.broken',                  N'en', N'Mappings that will put nothing in the document', 1),
    (N'mapping.brokenHint',              N'en', N'A mapping with no rows behind it is usually a typo in the source path: collection succeeds, returns nothing, and looks healthy in the run log.', 1),
    (N'mapping.unmapped',                N'en', N'Source fields that land nowhere', 1),
    (N'mapping.unmappedHint',            N'en', N'These rows are collected and stored, and no mapping takes them. That is legal for control tags and a defect for everything else.', 1),
    (N'mapping.uncovered',               N'en', N'Columns with nothing behind them', 1),
    (N'mapping.uncoveredHint',           N'en', N'Neither a mapping, nor a methodology output, nor a template formula fills these columns.', 1),
    (N'mapping.maps',                    N'en', N'Mappings', 1),
    (N'mapping.rows',                    N'en', N'Real source rows', 1),
    (N'mapping.rowsHint',                N'en', N'Values are shown in the unit of the SOURCE: that is how they are stored, and conversion happens on the way into the document.', 1),
    (N'mapping.field',                   N'en', N'Source field', 1),
    (N'mapping.target',                  N'en', N'Row and column', 1),
    (N'mapping.aggregation',             N'en', N'Fold', 1),
    (N'mapping.units',                   N'en', N'Units', 1),
    (N'mapping.points',                  N'en', N'Rows', 1),
    (N'mapping.folded',                  N'en', N'Value in the cell', 1),
    (N'mapping.outcome',                 N'en', N'Outcome', 1),
    (N'mapping.timestamp',               N'en', N'Timestamp', 1),
    (N'mapping.value',                   N'en', N'Value', 1),
    (N'mapping.quality',                 N'en', N'Quality', 1),
    (N'mapping.lastSeen',                N'en', N'Last seen', 1),
    (N'mapping.column',                  N'en', N'Column', 1),
    (N'mapping.fill',                    N'en', N'Filled by', 1),
    (N'mapping.required',                N'en', N'Required', 1),
    (N'mapping.unfillable',              N'en', N'nobody', 1),
    (N'mapping.manual',                  N'en', N'a person', 1),
    (N'mapping.yes',                     N'en', N'yes', 1),
    (N'mapping.no',                      N'en', N'no', 1),
    (N'mapping.materialized',            N'en', N'lands in a cell', 1),
    (N'mapping.rawOnly',                 N'en', N'stays raw', 1),
    (N'mapping.unmappedRow',             N'en', N'lands nowhere', 1),
    (N'mapping.targetMissing',           N'en', N'column is gone', 1),
    (N'mapping.noData',                  N'en', N'no rows in the source', 1)
) AS s ([Key], Lang, Val, Scope)
   ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
WHEN NOT MATCHED THEN INSERT ([Key], LanguageCode, Value, Scope, ModifiedAt)
     VALUES (s.[Key], s.Lang, s.Val, s.Scope, SYSUTCDATETIME());
GO
