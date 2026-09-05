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
MERGE sys_ecr.UiString AS t
USING (VALUES
    (N'app.loading',       N'en', N'Loading...', 0),
    (N'common.save',       N'en', N'Save', 0),
    (N'common.cancel',     N'en', N'Cancel', 0),
    (N'common.retry',      N'en', N'Retry', 0),
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
    (N'periods.reopenedUntil',           N'en', N'open until {until}', 1)
) AS s ([Key], Lang, Val, Scope)
   ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
WHEN NOT MATCHED THEN INSERT ([Key], LanguageCode, Value, Scope, ModifiedAt)
     VALUES (s.[Key], s.Lang, s.Val, s.Scope, SYSUTCDATETIME());
GO
