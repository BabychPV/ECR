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

MERGE sec.Role AS t
USING (VALUES (N'SystemAdministrator'), (N'TemplateAdministrator'), (N'PeriodAdministrator'),
              (N'DataEntry'), (N'Approver'), (N'Viewer'), (N'Auditor')) AS s (Code)
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

-- Мінімальний ПУБЛІЧНИЙ набір (Scope = 0) мовою за замовчуванням.
-- Без нього перший запуск покаже сирі ключі на сторінці входу — першому,
-- що бачить будь-хто. Решта ключів додається разом із областями UI.
MERGE sys_ecr.UiString AS t
USING (VALUES
    (N'auth.title',            N'en', N'Environmental Compliance Reporting', 0),
    (N'auth.windows',          N'en', N'Sign in with Windows',              0),
    (N'auth.local',            N'en', N'Sign in with account',              0),
    (N'auth.userName',         N'en', N'User name',                         0),
    (N'auth.password',         N'en', N'Password',                          0),
    (N'auth.submit',           N'en', N'Sign in',                           0),
    (N'auth.mustChange',       N'en', N'Change your password to continue',  0),
    (N'common.save',           N'en', N'Save',                              0),
    (N'common.cancel',         N'en', N'Cancel',                            0),
    (N'common.retry',          N'en', N'Retry',                             0),
    (N'common.loading',        N'en', N'Loading…',                          0),
    (N'err.ECR-AUTH-0401',     N'en', N'Sign in to continue.',              0),
    (N'err.ECR-AUTH-0403',     N'en', N'You do not have permission for this action.', 0),
    (N'err.ECR-AUTH-0423',     N'en', N'The account is locked.',            0),
    (N'err.ECR-PWD-0428',      N'en', N'Password change is required.',      0),
    (N'err.ECR-PWD-0422',      N'en', N'The new password does not meet the policy.', 0)
) AS s ([Key], Lang, Val, Scope)
   ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
WHEN NOT MATCHED THEN INSERT ([Key], LanguageCode, Value, Scope, ModifiedAt)
     VALUES (s.[Key], s.Lang, s.Val, s.Scope, SYSUTCDATETIME());
GO