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

-- ── Прибирання прав, яких більше немає ───────────────────────────────────
-- ⚠ Єдиний виняток із «не чіпає наявних рядків»: MERGE нижче лише додає, тож
-- право, прибране з каталогу, у вже розгорнутій базі жило б далі — разом із
-- роздачами ролям. `Template.Migrate` знято рішенням (директива №15, рішення
-- 4: документ назавжди на своїй версії шаблону); право нічого не відкривало.
-- Спершу роздачі (`FK_RolePerm_Perm` без каскаду), потім саме право.
DELETE FROM sec.RolePermission WHERE PermissionCode = N'Template.Migrate';
DELETE FROM sec.Permission     WHERE Code           = N'Template.Migrate';
-- Рішення людини 2026-09-21 (сторож UncheckedPermissionTests): `Calculation.EditScript`
-- — скриптів у системі немає; `Report.MarkSubmitted` — зріз стає поданим лише як
-- наслідок подання аркуша, ручна позначка дала б позначити поданим неподане.
DELETE FROM sec.RolePermission WHERE PermissionCode IN (N'Calculation.EditScript', N'Report.MarkSubmitted');
DELETE FROM sec.Permission     WHERE Code           IN (N'Calculation.EditScript', N'Report.MarkSubmitted');
GO

-- Функціональні права
MERGE sec.Permission AS t
USING (VALUES
  (N'Template.View',            N'Template',    0), (N'Template.Edit',        N'Template',    0),
  (N'Template.Publish',         N'Template',    0),
  (N'Registry.View',            N'Registry',    0), (N'Registry.EditData',    N'Registry',    0),
  (N'Registry.EditDefinition',  N'Registry',    0), (N'Registry.Publish',     N'Registry',    0),
  (N'Document.View',            N'Document',    0), (N'Document.Create',      N'Document',    0),
  (N'Document.Delete',          N'Document',    0), (N'Document.Import',      N'Document',    0),
  (N'Document.Export',          N'Document',    0), (N'Document.Reopen',      N'Document',    0),
  (N'Project.Manage',           N'Project',     0),
  (N'Period.Configure',         N'Period',      0), (N'Period.Reopen',        N'Period',      1),
  (N'Calculation.View',         N'Calculation', 0), (N'Calculation.EditFormula',  N'Calculation', 0),
  (N'Calculation.EditConstant', N'Calculation', 0), (N'Calculation.EditRule',     N'Calculation', 0),
  (N'Calculation.Publish',      N'Calculation', 1),
  (N'Calculation.Recalculate',  N'Calculation', 0), (N'Calculation.ManageRequiredInputs', N'Calculation', 0),
  (N'Report.ViewRegulatory',    N'Report',      0), (N'Report.BuildSnapshot', N'Report',      0),
  (N'Report.Export',            N'Report',      0),
  -- ⚠ НЕБЕЗПЕЧНЕ (1) навмисно, і не через ризик втратити дані. Причина в
  -- фільтрі нижче: `Approver` має шаблон `Report.%`, виданий тоді, коли всі
  -- права цієї родини були «дивитися, будувати, подавати, вивантажувати».
  -- `Report.EditDefinition` — інша річ: це авторство ДЕРЖАВНОЇ ФОРМИ
  -- (`ФВ-10.4`), і мовчки роздати його кожному погоджувачу лише тому, що воно
  -- починається на `Report.`, означало б змінити повноваження людей правкою
  -- одного рядка каталогу. Адміністратор видає його свідомо, і в журналі
  -- безпеки видно, хто це зробив (`DangerousPermissionsGranted`).
  (N'Report.EditDefinition',    N'Report',      1),
  -- ⚠ НЕБЕЗПЕЧНЕ (1) з тієї самої причини, що й рядок вище, і це — механізм,
  -- яким виконано рішення людини на `Q15-07`: «окреме право, ВИДАЄТЬСЯ ЯВНО».
  -- Шаблон `Report.%` складеної ролі `Approver` бере лише `IsDangerous = 0`,
  -- тож огляд кампанії не приїде разом із рештою родини. Право відкриває коди
  -- й назви ВСІХ проєктів разом із лічильниками їхніх документів — без межі
  -- грантів (`BE-22`), і роздати його правкою одного рядка каталогу було б
  -- зміною повноважень людей.
  (N'Report.ViewCampaign',      N'Report',      1),
  (N'Integration.View',         N'Integration', 0), (N'Integration.Manage',   N'Integration', 1),
  (N'Integration.EditSchedule', N'Integration', 0),
  -- ⛔ UI-аудит, lane 4: жоден обліковий запис, включно з повноправним
  -- адміністратором, не мав шляху додати одиницю виміру — не спеціальне
  -- обмеження права (`IsDangerous`), а відсутність будь-якого ендпоінта.
  -- Не небезпечне: заведення одиниці не змінює наявні дані й не впливає на
  -- вже збережені числа (той самий клас, що `Registry.EditDefinition`).
  (N'Uom.EditCatalog',          N'Uom',         0),
  (N'Security.ManageUsers',     N'Security',    1), (N'Security.ManageRoles', N'Security',    1),
  (N'Security.ViewAudit',       N'Security',    0), (N'Security.Simulate',    N'Security',    1),
  (N'System.ViewHealth',        N'System',      0), (N'System.RunJob',        N'System',      1),
  (N'System.ManageLocalization', N'System',     0),
  -- НЕБЕЗПЕЧНЕ (1), як `Integration.Manage`: носій вирішує, КУДИ сервер шле
  -- повідомлення про збої (адресати SMTP, URL вебхука), і замінює секрети
  -- каналів. Тому шаблон `%` системного адміністратора його не роздає —
  -- видається свідомо, зі слідом у журналі безпеки (`BE-32`).
  (N'System.ManageNotifications', N'System',    1),
  -- НЕБЕЗПЕЧНЕ (1): зміна бізнес-ключа документа (ФВ-3.9) міняє те, під чим
  -- документ знають експорти й зовнішні системи; видається свідомо.
  (N'Document.ChangeKey',       N'Document',    1)
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

-- ── Змінені тексти наявних ключів ────────────────────────────────────────
-- ⛔ MERGE нижче лише ВСТАВЛЯЄ відсутні ключі, тож зміна тексту наявного ключа
-- доходила тільки до свіжих баз. Звідси оновлення «старе → нове», але лише
-- поки в базі стоїть САМЕ старе значення (порівняння побайтне): текст, який
-- адміністратор уже переписав на `/admin/ui-strings`, лишається його.
-- ⚠ Змінюєш текст наявного ключа в MERGE — додай сюди рядок (ключ, мова,
-- старе, нове). Ланцюг A → B → C: два рядки, обидва з новим C.
-- Сторож `SeedTextUpdateTests` тримає «нове» рівним значенню в MERGE.
DECLARE @textUpdates int, @removed int, @inserted int;
UPDATE t
   SET Value = s.NewVal, ModifiedAt = SYSUTCDATETIME()
  FROM sys_ecr.UiString AS t
  JOIN (VALUES
    (N'common.loading',                  N'en', N'Loading…', N'Loading...'),
    -- ФВ-4.2: кнопка більше не завжди Excel — формат обирається поруч.
    (N'document.export',                 N'en', N'Export to Excel', N'Export'),
    (N'periods.timeZone',                N'en', N'Site time zone', N'Site time zone (IANA)'),
    (N'periods.timeZoneHint',            N'en', N'Period boundaries and late-edit marks are calculated in this zone. It cannot be changed once the first period is open.',
                                                N'IANA identifier of the site, for example Asia/Aqtau. Period boundaries and late-edit marks are calculated in this zone, and it cannot be changed once the first period is open.'),
    (N'state.errorUnknown',              N'en', N'An unexpected error occurred. Retry; if it repeats, quote the code below to support.',
                                                N'An unexpected error occurred. Retry; if it repeats, contact support and describe what you were doing.'),
    (N'version.diffOtherHint',           N'en', N'The other version to compare against; take the id from the template list.',
                                                N'Another version of this template. Changes are always shown from the older version to the newer one.'),
    (N'columns.lookupRegistryDefIdHint', N'en', N'Identifier of the registry this column looks values up from.',
                                                N'The registry this column looks values up from.'),
    (N'workflow.recalculateHint',        N'en', N'Recalculates every sheet of this document for the shown period, not only this one.',
                                                N'Recalculates this sheet. Formulas may still read data from other sheets of the same document.'),
    (N'err.ECR-PRJ-0409',                N'en', N'A project with code "{code}" already exists.', N'Project code already in use'),
    (N'err.ECR-CFG-0422',                N'en', N'The code "{code}" is invalid: only Latin letters, digits, and underscores are allowed, the first character must be a letter, maximum length 64.',
                                                N'Invalid code'),
    (N'err.ECR-UOM-4091',                N'en', N'A unit with code "{code}" already exists (Id {id}).', N'Unit code already in use'),
    (N'err.ECR-REG-0409',                N'en', N'An entry with code "{code}" already exists in this registry (Id {id}).', N'Registry entry conflict'),
    (N'err.ECR-REG-0409',                N'en', N'Registry entry code already in use', N'Registry entry conflict'),
    (N'err.ECR-USR-0409',                N'en', N'A user named "{userName}" already exists.', N'User name already in use'),
    (N'err.ECR-REG-4091',                N'en', N'A registry with code "{code}" already exists (Id {id}): the code is what registry-lookup fields and template columns reference it by.',
                                                N'Registry code already in use'),
    (N'err.ECR-SEC-0409',                N'en', N'A role with code "{code}" already exists.', N'Conflicts with security settings'),
    (N'err.ECR-SEC-0409',                N'en', N'Role code already in use', N'Conflicts with security settings'),
    (N'tables.readOnlyHint',             N'en', N'A relation decides where a table takes its numbers from, so changing it would silently change forms already submitted. Clone the version to change it (ФВ-7.1).',
                                                N'A relation decides where a table takes its numbers from, so changing it would silently change forms already submitted. Clone the version to change it.'),
    (N'security.roleCodeHint',           N'en', N'Used in grants and audit; it cannot be changed later.', N'Used in grants and audit. Built-in role codes cannot be changed.'),
    (N'err.ECR-INT-0404',                N'en', N'Source entity not found', N'Source entity or field mapping not found'),
    (N'err.ECR-CALC-0409',               N'en', N'A second pair of eyes is required', N'Conflicting methodology state'),
    (N'err.ECR-UOM-0422',                N'en', N'Incompatible unit dimensions', N'Invalid unit conversion'),
    (N'err.ECR-CALC-0422',               N'en', N'The methodology version cannot be published', N'Invalid methodology request'),
    (N'err.ECR-REG-0422',                N'en', N'The registry source cannot be switched in an open period', N'Invalid registry change'),
    (N'err.ECR-REG-0404',                N'en', N'Registry entry not found', N'Registry item not found'),
    (N'err.ECR-PRD-0409',                N'en', N'The period is closed', N'Period state conflict'),
    (N'err.ECR-PRD-0422',                N'en', N'The period is outside the project', N'Invalid period request'),
    (N'err.ECR-AUTH-0403.jobNotYours',   N'en', N'This background job was started by someone else: permission {permission} is required to cancel it.', N'This background job was started by someone else: permission {permission} is required to act on it.'),
    (N'err.ECR-CALC-4221',               N'en', N'Recalculation of a closed period', N'Recalculation is not allowed'),
    -- U-17: заголовок банера відсилав «див. помилку вище» — а помилка і є цей
    -- самий банер; той самий текст стояв ще й позначкою над ним.
    (N'grid.saveError',                  N'en', N'Not saved — see the error above', N'The server rejected this change'),
    -- `U-09`, пунктуація порожніх станів зведена до ОДНОГО правила:
    -- ЗАГОЛОВОК порожнього стану — без крапки в кінці, ПІДКАЗКА під ним —
    -- повне речення з крапкою. Так уже написані 40+ заголовків каталогу
    -- («No registries yet», «Pick a project», «Enter a job id»); ці два були
    -- єдиними винятками, і на екрані `/admin/jobs` виняток стояв просто під
    -- правилом.
    (N'jobs.recentEmpty',                N'en', N'No jobs yet.', N'No jobs yet'),
    (N'documents.empty',                 N'en', N'No documents for this period.', N'No documents for this period'),
    -- V-09: роль у маршруті погодження чи правилі доступу до періоду теж «зайнята».
    (N'err.ECR-SEC-0409.roleInUse',      N'en', N'Role "{code}" is in use: {assignments} assignment(s), {grants} grant(s). Remove them first.',
                                                N'Role "{code}" is in use: {assignments} assignment(s), {grants} grant(s), {approvalSteps} approval route step(s), {periodAccessRules} period access rule(s). Remove them first.'),
    -- V-16: сервер перевіряє лише довжину (`ChangePasswordHandler`,
    -- `PasswordPolicy.MinLength`); прапорці складності не вмикаються (`P-1`).
    (N'password.policy',                 N'en', N'At least 12 characters, with upper case, lower case and a digit.', N'At least 12 characters.'),
    -- V-08: відмова видалення запису довідника рахує тепер не лише комірки, а
    -- підказка коду обіцяла, що запис «ніколи не видаляється» — неправда.
    (N'err.ECR-REG-0409.entryReferenced', N'en', N'Entry "{code}" cannot be deleted: {referenceCount} cells reference it. Close it with an end date instead: history stays readable and new periods will not offer it.',
                                                N'Entry "{code}" cannot be deleted: it is still referenced {referenceCount} time(s) — by document cells, other registry entries or methodology constants. Close it with an end date instead: history stays readable and new periods will not offer it.'),
    (N'registries.entryCodeHint',        N'en', N'Cells store the entry id, so the code can change; the entry itself is never deleted.',
                                                N'Cells store the entry id, so the code can change. An entry that anything still references cannot be removed — close it with an end date instead.'),
    -- V-18: цикл формул — поіменно і з чесною кількістю.
    (N'err.ECR-TMPL-4221.formulaCycle',   N'en', N'The formulas form a dependency cycle ({cycleLength} formula(s) involved).',
                                                N'The formulas form a dependency cycle: {cyclePath} ({cycleLength} formula(s)).'),
    -- R-09/R-10 (четвертий раунд UX): друга версія порівняння вибирається зі
    -- списку версій свого шаблону, а не вводиться ідентифікатором.
    (N'version.diffOther',               N'en', N'Compare with version id', N'Compare with version'),
    (N'version.diffOtherHint',           N'en', N'The other version to compare against — open it and copy the id from its URL (…/versions/{id}).',
                                                N'Another version of this template. Changes are always shown from the older version to the newer one.'),
    (N'version.diffPick',                N'en', N'Enter the other version', N'Pick the other version'),
    -- B-09: у конфлікту з'явився вихід — підказка його називає; рядок переліку — і моє значення.
    (N'grid.conflictHint', N'en', N'{count} cell(s) were changed by another user. Review them before saving again.',
                                                N'{count} cell(s) were changed by someone else after this table was loaded. Keep your values to overwrite theirs, or discard yours to see theirs.'),
    (N'grid.conflictItem', N'en', N'Row {row}, column {column}: their value {value} — {user}, {time}',
                                                N'Row {row}, column {column}: yours {yours}, theirs {value} — {user}, {time}'),
    -- B-02: тим самим кодом тепер відмовляє й неіснуюча одиниця в колонці `Unit`.
    (N'err.ECR-CELL-4223',               N'en', N'Reference to a missing registry entry', N'Reference to a missing registry entry or unit'),
    -- F-15/B-12 (четвертий раунд UX, лінія B1): відмова золотого набору
    -- називає тести, що розійшлися.
    (N'err.ECR-CALC-0422.goldenSetDiverged', N'en', N'Version {version} cannot be published: {count} values diverged on the golden set.',
                                                N'Version {version} cannot be published: {count} value(s) diverged on the golden set (tests: {tests}).'),
    -- F-22: обчислювану комірку рахує або формула шаблону, або методологія —
    -- «відкрийте версію шаблону» для колонки методології хибне.
    (N'grid.formulaBarNoExpression',     N'en', N'The expression is not sent with the table: open the template version to read it',
                                                N'Calculated by the system — by a template formula or by the methodology bound to this column. The expression is not sent with the table.')
  ) AS s ([Key], Lang, OldVal, NewVal)
    ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
 WHERE t.Value = s.OldVal COLLATE Latin1_General_BIN2;
SET @textUpdates = @@ROWCOUNT;

-- ── Прибрані ключі ───────────────────────────────────────────────────────
-- ⛔ MERGE і не видаляє: ключ, який прибрали з сіду, лишається в розгорнутій
-- базі й видний у редакторі рядків. Видаляється на тій самій умові — поки
-- значення досі дефолтне; переписаний адміністратором рядок лишається.
-- ⚠ Прибираєш ключ із MERGE — додай сюди (ключ, мова, останнє значення).
-- Сторож `SeedTextUpdateTests`: ключа звідси не може бути в MERGE.
DELETE t
  FROM sys_ecr.UiString AS t
  JOIN (VALUES
    (N'campaign.truncatedHint',                    N'en', N'The server returned only part of the list. A project holding up the campaign may be among those not shown, and the totals cover only the projects shown.'),
    (N'campaign.laggingCount',                     N'en', N'{lagging} of {shown} projects are not finished: no documents, not everything approved, or no snapshot yet.'),
    (N'campaign.nobodyLaggingHint',                N'en', N'Every project shown has all documents approved and a report snapshot.'),
    (N'registries.usageKind.templateColumn',       N'en', N'Template column'),
    (N'registries.usageKind.registryField',        N'en', N'Registry field'),
    (N'registries.usageKind.methodologySubstance', N'en', N'Methodology substance'),
    (N'registries.usageKind.sourceEntity',         N'en', N'Source entity'),
    (N'registries.usageKind.data',                 N'en', N'Values in documents'),
    -- ФВ-3.6: правки більше не «втрачені» — їх зберігає браузер і відновлює
    -- екран документа; ключі замінено на `login.restoreEdits.*`.
    (N'login.lostEdits.title',                     N'en', N'Unsaved changes were lost'),
    (N'login.lostEdits.text',                      N'en', N'{count} unsaved change(s) in document #{documentId} were lost — your session ended. Please re-enter them.'),
    (N'login.lostEdits.continue',                  N'en', N'Continue'),
    -- BE-24 крок 2: збереження опису довідника завжди йде в чернетку, а опис
    -- змінює лише публікація — обидва ключі втратили місце на екрані.
    (N'registries.saveDefinition',                 N'en', N'Save definition'),
    (N'registries.definitionSaved',                N'en', N'Saved. Definition version: {version}.'),
    -- U-16: головна кнопка сітки суперечила моделі роботи — збереження
    -- автоматичне, а «Save (N)» рахувала не «чекає збереження», а «збереження
    -- не пройшло». Замінена на `grid.retrySave`, видиму лише після відмови.
    (N'grid.save',                                 N'en', N'Save ({count})'),
    -- U-18: рядок «Still needed» став спільним для діалогів створення —
    -- ключ `common.stillNeeded`.
    (N'periods.stillNeeded',                       N'en', N'Still needed: {fields}'),
    -- Відмова типу поля шапки: один ключ з `{expected}` підставляв українське
    -- слово в англійське речення; замінено на `err.ECR-HDR-0422.expects*`.
    (N'err.ECR-HDR-0422.typeMismatch',             N'en', N'Header field "{headerFieldCode}" expects a {expected}.'),
    -- X-02 (четвертий раунд UX): форма колонки більше не відкривається на
    -- неповних даних — попереджати «збереження зітре» нема про що. Обидва
    -- тексти, що побували в базах.
    (N'columns.partialDataWarning',                N'en', N'This column carries fields not shown here (precision, lookup, unit, default value). Saving will clear them unless you already edited this column in this session.'),
    (N'columns.partialDataWarning',                N'en', N'This column carries fields not shown here (precision, lookup, unit, default value, style). Saving will clear them unless you already edited this column in this session.'),
    -- X-32: «ще не перевіряли» — `200` з `validated: false`, а не відмова `404`.
    (N'err.ECR-DOC-0404.notValidated',             N'en', N'Document {documentId} has not been validated for period {periodKey} yet.')
  ) AS s ([Key], Lang, OldVal)
    ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
 WHERE t.Value = s.OldVal COLLATE Latin1_General_BIN2;
SET @removed = @@ROWCOUNT;

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
    -- Q-298 / U-18: підказка біля недоступної кнопки підтвердження форми
    -- створення — перелік бракуючих полів (`StillNeeded`). Спільна для всіх
    -- діалогів створення, тому `common.*`; до U-18 жила як `periods.stillNeeded`
    -- лише в діалозі проєкту (прибраний ключ — у секції вище).
    (N'common.stillNeeded', N'en', N'Still needed: {fields}', 0),
    (N'state.errorTitle',                N'en', N'The request failed', 0),
    (N'state.errorUnknown',              N'en', N'An unexpected error occurred. Retry; if it repeats, contact support and describe what you were doing.', 0),
    (N'state.emptyTitle',                N'en', N'Nothing here yet', 0),
    (N'login.title',       N'en', N'Environmental Compliance Reporting', 0),
    (N'login.windows',     N'en', N'Sign in with Windows', 0),
    (N'login.or',          N'en', N'or', 0),
    (N'login.user',        N'en', N'User name', 0),
    (N'login.password',    N'en', N'Password', 0),
    (N'login.submit',      N'en', N'Sign in', 0),
    (N'login.hint',        N'en', N'Use your Windows account, or the local account issued to you.', 0),
    -- ⚠ Публічна область (0): банер показано ДО входу, коли приватний зріз
    -- каталогу ще недоступний.
    (N'login.sessionInvalidated', N'en', N'Your session has ended because your permissions or password changed. Sign in again.', 0),
    -- Незбережені правки, що пережили обрив сесії (ФВ-3.6) — банер показано до
    -- входу, тож область 0. ⚠ Правки тепер не просто названі втраченими: вони
    -- лежать у цьому браузері й відновлюються на екрані документа, тому
    -- колишні `login.lostEdits.*` прибрані (нижче в «Прибраних ключах»).
    (N'login.restoreEdits.title',    N'en', N'You are signed in again', 0),
    (N'login.restoreEdits.text',     N'en', N'{count} unsaved change(s) in document #{documentId} were kept in this browser. They never reached the server; open the document to restore them.', 0),
    (N'login.restoreEdits.continue', N'en', N'Open the document', 0),
    (N'login.restoreEdits.discard',  N'en', N'Discard them', 0),
    (N'err.ECR-AUTH-0401', N'en', N'Sign in to continue.', 0),
    (N'err.ECR-AUTH-0403', N'en', N'You do not have permission for this action.', 0),
    (N'err.ECR-AUTH-0403.requiresPermission', N'en', N'Requires permission', 1),
    (N'err.ECR-AUTH-0423', N'en', N'The account is locked.', 0),
    -- ⛔ ECR-AUTH-0429 і ECR-AUTH-0423 — РІЗНІ стани, і доки першого не було,
    -- обмежувач частоти (`LoginRateLimiting`, S-10) відповідав другим: заголовок
    -- казав «The account is locked.», хоча межу вичерпала АДРЕСА, а обліковку
    -- ніхто не блокував — і заблокувати не міг, бо межа ріже ще до того, як
    -- стане відомо, чи існує назване ім'я. Ціна була не в тексті: користувач
    -- дзвонив у підтримку через блокування, якого немає.
    -- ⚠ Публічна область (0): цей стан видно ДО входу (ФВ-14.9b).
    (N'err.ECR-AUTH-0429', N'en', N'Too many sign-in attempts', 0),
    (N'err.ECR-AUTH-0429.tooManyAttempts', N'en',
     N'Too many sign-in attempts from this address. Try again later; the Retry-After header says how long.', 0),
    (N'err.ECR-REQ-0429', N'en', N'Too many requests', 1),
    (N'err.ECR-REQ-0429.tooManySearches', N'en',
     N'Too many searches in a short time. Wait a moment and try again; the Retry-After header says how long.', 1),
    (N'err.ECR-PWD-0428',  N'en', N'Password change is required.', 0),
    (N'err.ECR-PWD-0422',  N'en', N'The new password does not meet the policy.', 0),

    -- ⛔ ECR-TMPL-4227 — два різні входи в один стан, тому два ключі: формула
    -- шаблону і прив'язка виходу методології. Текст мусить називати КОНКРЕТНУ
    -- колонку і що з нею робити: «тип колонки незмінний» — це не деталь, а
    -- єдина дія, яка лишається конфігураторові.
    (N'err.ECR-TMPL-4227.formulaOnManualColumn', N'en',
     N'Column {tableCode}.{columnCode} is {dataType}, a manual-entry column: a formula on it would silently overwrite what the operator typed at the next recalculation. Create a Formula column (the type cannot be changed) or remove the formula.', 1),
    (N'err.ECR-TMPL-4227.bindingOnManualColumn', N'en',
     N'Column {columnCode} is {dataType}, a manual-entry column. The operator edits it by hand while the report takes the methodology result, and no screen shows that the two disagree. Bind the output to a Calculated column (the type cannot be changed).', 1),
    -- ⛔ Q-30x: EcrCode.Create (Ecr.Domain — без доступу до IUiStringCatalog)
    -- будує лише ключ + підстановку `{code}`, не готове речення; це
    -- речення резолвить ExceptionHandlingMiddleware.LocalizedDetailAsync.
    -- Приватна область: код валідується лише на автентифікованих
    -- адмін-екранах (роль, проєкт, шаблон, довідник, методологія).
    -- ⛔ Сім ключів нижче були ОДНИМИ І ТИМИ САМИМИ для заголовка й подробиці,
    -- і саме тому містили плейсхолдер. `LocalizedTitleAsync` підстановок не
    -- робить — заголовком плашки їхав сирий `{code}`, а другим рядком те саме
    -- речення вдруге, уже з підставленим значенням. Тепер речення живе на
    -- суфіксованому ключі (`err.<код>.<що саме>`, як для `ECR-TMPL-4227` і
    -- `ECR-DOC-0404`), а під `err.<код>` лишилася коротка називна фраза.
    -- ⚠ Перейменування ключа тут НЕ самодостатнє: кожен із семи названий у
    -- `Details["messageKey"]` конкретного кидка в `src/`, і обидві половини
    -- правляться разом. Розрив між ними тепер червонить
    -- `ErrorTitleCatalogTests.Кожен_messageKey_із_коду_заведено_в_сіді`.
    (N'err.ECR-CFG-0422',  N'en', N'Invalid code', 1),
    (N'err.ECR-CFG-0422.invalidCode', N'en', N'The code "{code}" is invalid: only Latin letters, digits, and underscores are allowed, the first character must be a letter, maximum length 64.', 1),
    -- ⛔ `StyleDef.SetAppearance` кидав `err.ECR-CFG-0422.styleAlign`, якого в
    -- сіді НЕ БУЛО ЖОДНОГО РАЗУ, тож подробиця падала на український фолбек
    -- незалежно від мови інтерфейсу. Знайшов це новий сторож
    -- `Кожен_messageKey_із_коду_заведено_в_сіді` — дефект був невидимий саме
    -- тому, що незаведений ключ нічого не ламає. Ключів два, а не один:
    -- горизонталь має діапазон 0..3, вертикаль 0..2, і один спільний текст
    -- назвав би хибний діапазон половині випадків.
    (N'err.ECR-CFG-0422.horizontalAlign', N'en', N'Horizontal alignment {value} is unknown: 0..3 (Left/Center/Right/Justify).', 1),
    (N'err.ECR-CFG-0422.verticalAlign',   N'en', N'Vertical alignment {value} is unknown: 0..2 (Top/Center/Bottom).', 1),
    -- ⛔ Q-30x: чотири варіанти «код/ім'я вже зайняте», кожен — своя сутність
    -- (роль/проєкт/довідниковий запис/користувач), кожен свій messageKey,
    -- усі приватної області (лише автентифіковані адмін-екрани).
    (N'err.ECR-SEC-0409',  N'en', N'Conflicts with security settings', 1),
    (N'err.ECR-SEC-0409.roleCodeTaken', N'en', N'A role with code "{code}" already exists.', 1),
    -- ⛔ `BE-14`: та сама родина, інші причини — роль не видаляється, доки на
    -- ній щось тримається, а вбудована не видаляється й не перейменовується.
    (N'err.ECR-SEC-0409.roleInUse', N'en', N'Role "{code}" is in use: {assignments} assignment(s), {grants} grant(s), {approvalSteps} approval route step(s), {periodAccessRules} period access rule(s). Remove them first.', 1),
    (N'err.ECR-SEC-0409.roleBuiltIn', N'en', N'Role "{code}" is built in: it cannot be renamed or deleted.', 1),
    (N'err.ECR-SEC-0404.roleNotFound', N'en', N'Role {roleId} does not exist.', 1),
    -- Ролі групам каталогу (ФВ-6.15): небезпечна роль — лише з підтвердженням.
    (N'err.ECR-SEC-0409.dangerousRoleNeedsConfirmation', N'en', N'Role "{code}" carries dangerous permissions ({permissions}). Confirm to assign it to a group.', 1),
    (N'err.ECR-SEC-0409.groupAssignmentExists', N'en', N'Role "{code}" is already assigned to group {sid}.', 1),
    (N'err.ECR-SEC-0404.groupAssignmentNotFound', N'en', N'Group assignment {id} does not exist.', 1),
    -- `BE-12`: адміністрування облікових записів — скидання пароля, блокування.
    (N'err.ECR-SEC-0404.userNotFound', N'en', N'User {userId} does not exist.', 1),
    (N'err.ECR-SEC-0409.cannotTargetSelf', N'en', N'You cannot lock your own account or reset its password here. Change your own password from your profile.', 1),
    (N'err.ECR-SEC-0409.lastAdministrator', N'en', N'"{userName}" is the last active administrator: nobody would be left to manage users.', 1),
    (N'err.ECR-USR-0422.domainPasswordReset', N'en', N'"{userName}" is a domain account: its password is managed in the domain, not here.', 1),
    (N'err.ECR-USR-0422.lockReasonRequired', N'en', N'A reason of up to {max} characters is required: it is recorded in the security journal.', 1),
    (N'err.ECR-REQ-0422.principalNotResolved', N'en', N'Group "{principal}" was not found in the directory. Check the name or enter its SID.', 1),
    (N'err.ECR-REQ-0422.principalSidMalformed', N'en', N'"{principal}" is not a valid SID.', 1),
    (N'err.ECR-REQ-0422.validityOrder', N'en', N'The start of the validity window is later than its end.', 1),
    (N'err.ECR-PRJ-0409',  N'en', N'Project code already in use', 1),
    (N'err.ECR-PRJ-0409.projectCodeTaken', N'en', N'A project with code "{code}" already exists.', 1),
    -- Покриває і зайнятий код, і видалення запису, на який посилаються.
    (N'err.ECR-REG-0409',  N'en', N'Registry entry conflict', 1),
    (N'err.ECR-REG-0409.entryCodeTaken', N'en', N'An entry with code "{code}" already exists in this registry (Id {id}).', 1),
    (N'err.ECR-REG-0409.entryCodeTakenConcurrently', N'en', N'An entry with code "{code}" was just created in this registry by another request.', 1),
    (N'err.ECR-USR-0409',  N'en', N'User name already in use', 1),
    (N'err.ECR-USR-0409.userNameTaken', N'en', N'A user named "{userName}" already exists.', 1),
    -- ⛔ Аудит-пас 4: ще шість джерел, той самий клас дефекту (Q-303/Q-304)
    -- — готове українське речення доходило до клієнта незалежно від мови.
    (N'err.ECR-REG-4091',  N'en', N'Registry code already in use', 1),
    (N'err.ECR-REG-4091.registryCodeTaken', N'en', N'A registry with code "{code}" already exists (Id {id}): the code is what registry-lookup fields and template columns reference it by.', 1),
    -- ⛔ UI-аудит, lane 4: заведення одиниці виміру не мало жодного шляху,
    -- доступного людині — той самий клас дефекту, що вже виправлений для
    -- довідників (`Q-200`).
    (N'err.ECR-UOM-4091',  N'en', N'Unit code already in use', 1),
    (N'err.ECR-UOM-4091.unitCodeTaken', N'en', N'A unit with code "{code}" already exists (Id {id}).', 1),
    (N'err.ECR-UOM-0404.unitId', N'en', N'There is no unit with Id {id}.', 1),
    (N'err.ECR-UOM-0409',  N'en', N'Unit is in use', 1),
    (N'err.ECR-UOM-0409.unitInUse', N'en', N'Unit "{code}" cannot be removed: it is referenced in {total} place(s).', 1),
    -- BE-15: зміна одиниці (`PUT /api/v1/units/{id}`).
    (N'err.ECR-UOM-0409.unitChanged', N'en', N'Someone else changed unit "{code}" after you read it: reload it and repeat the change.', 1),
    (N'err.ECR-UOM-0409.unitFactorInUse', N'en', N'The factor and offset of unit "{code}" cannot change: it is referenced in {total} place(s), and stored values would silently convert to different numbers.', 1),
    (N'err.ECR-REQ-0422.unitIfMatch', N'en', N'This request needs an If-Match header carrying the rowVersion of the unit you read.', 1),
    (N'err.ECR-REQ-0422.unitInvalid', N'en', N'Unit "{code}" needs a symbol and a name in at least one language, each up to 200 characters.', 1),
    -- ECR-UOM-0422: the code title is neutral, each refusal carries its own detail.
    (N'err.ECR-UOM-0422.factorMustBePositive', N'en', N'The factor to the base unit of unit "{code}" must be greater than zero, not {factorToBase}: zero turns every conversion into a constant, a negative factor flips the sign.', 1),
    (N'err.ECR-UOM-0422.incompatibleDimensions', N'en', N'{from} cannot be converted to {to}: the units measure different dimensions. A context coefficient such as density belongs to the methodology, not to the unit catalog.', 1),
    (N'err.ECR-UOM-0422.zeroFactor', N'en', N'Unit "{code}" has a zero factor to the base unit, so values cannot be converted from or to it.', 1),
    (N'err.ECR-UOM-0422.explicitConversionMismatch', N'en', N'The explicit conversion rule does not describe the requested conversion {from} to {to}.', 1),
    (N'err.validityWindowEmpty', N'en', N'Empty validity window: the exclusive end {to} is not later than the start {from}.', 1),
    (N'err.ECR-REQ-0422.auditWindowOrder',   N'en', N'The end of the audit window must be later than the start.', 1),
    (N'err.ECR-REQ-0422.auditWindowTooWide', N'en', N'The audit window is wider than {maxDays} days: the request would scan every partition.', 1),
    -- ⚠ `BE-03`: ключ рядка унікальний у межах екземпляра таблиці, а не
    -- системи — «R1» є в кожному документі. Фільтр за ним без documentId
    -- зібрав би рядки чужих документів і виглядав би як відповідь.
    (N'err.ECR-REQ-0422.auditCellNeedsDocument', N'en', N'A row key or column filter needs a document: without one the cell address is not an address.', 1),
    -- ⚠ ПОДРОБИЦЯ відмови (`Detail`), не заголовок: заголовок `err.ECR-REQ-0422`
    -- уже заведений нижче. Ключ із підстановкою `{max}` — рівно та форма, що й
    -- `auditWindowTooWide` вище (`Q-341`).
    (N'err.ECR-REQ-0422.pageSizeOutOfRange', N'en', N'The page size must be between 1 and {max}.', 1),
    (N'err.ECR-REQ-0422.auditExportTooLarge', N'en', N'The export would contain {total} rows, the limit is {max}: narrow the window or the filters.', 1),
    -- BE-09b: фільтр стану переліку документів. Невідомий стан — відмова, а не «усі».
    (N'err.ECR-REQ-0422.documentState',      N'en', N'There is no document state "{state}".', 1),
    (N'err.ECR-REQ-0422.documentStateNeedsPeriod', N'en', N'Filtering by document state needs a period: outside a period the state is not defined.', 1),
    -- ⚠ Період у ТІЛІ запису комірок проти періоду екземпляра таблиці
    -- (`DAT-04`). Без цієї відмови розбіжність доїжджала до порушення
    -- зовнішнього ключа і виходила назовні голим `500` — тобто дефект даних
    -- виглядав як збій сервера.
    (N'err.ECR-REQ-0422.periodMismatch',     N'en', N'The period {periodKey} in the request does not match period {expectedPeriodKey} of table instance {tableInstanceId}.', 1),
    -- B-05: походження правки з тіла `PATCH …/cells` — лише людське.
    (N'err.ECR-REQ-0422.cellOriginNotAllowed', N'en', N'Origin "{origin}" cannot be set by a client: an edit made here is recorded as {allowed}.', 1),
    (N'err.ECR-IMP-0422.notAWorkbook',       N'en', N'The file cannot be read as an .xlsx workbook.', 1),
    -- ⚠ Збір із SQL-джерела, яке не є PI (`ФВ-11.8`, транспорт `Sql`). Усі
    -- чотири подробиці кажуть, ЩО саме поправити: ключ налаштування, поле
    -- Endpoint, облікові дані. «Джерело недоступне» без цього відправляє
    -- шукати мережу навіть тоді, коли джерело просто не налаштоване.
    (N'err.ECR-INT-0503.queryNotConfigured',    N'en', N'Source "{dataSource}" has no query configured ({settingKey}): there is nothing to collect with. The table and column names of the source belong to the customer, so there is deliberately no default.', 1),
    (N'err.ECR-INT-0503.connectionStringBroken', N'en', N'The connection string of source "{dataSource}" cannot be parsed: fix the Endpoint field of the source.', 1),
    (N'err.ECR-INT-0503.connectFailed',         N'en', N'The SQL source "{dataSource}" cannot be reached.', 1),
    (N'err.ECR-INT-0503.sourceMissing',         N'en', N'Data source {dataSourceId} does not exist or is switched off: there is nothing to collect.', 1),
    (N'err.ECR-INT-0503.sourcePathTooLong',     N'en', N'The entity path is longer than {maxLength} characters, so it cannot be sent to source "{dataSource}". A truncated path is not an error but silence: the source would answer "no such entity".', 1),
    (N'err.ECR-INT-0502.credentialsRefused',    N'en', N'The SQL source "{dataSource}" refused the service credentials. This is not a temporary outage: the collection will not retry.', 1),
    (N'err.ECR-CALC-0422.constantNoValue',   N'en', N'A numeric constant needs a value: an empty number is not "zero by default" — it is a decision nobody made.', 1),
    (N'err.ECR-CALC-0422.constantNoUnit',    N'en', N'A numeric constant needs a unit: the dimension check cannot run without it.', 1),
    -- V-17(b): речовина константи — живий запис довідника.
    (N'err.ECR-CALC-0422.constantSubstanceNotFound', N'en', N'Constant "{constantCode}": substance (registry entry) {substanceEntryId} does not exist.', 1),
    -- V-17(c): симуляція читає ключ періоду як місяць.
    (N'err.ECR-CALC-0422.simulatePeriodInvalid', N'en', N'Period key {periodKey} is not a month: expected YYYYMM, for example 202609.', 1),
    -- D-52a: опис звіту керує побудовою зрізу, тож колонка, якої джерело не
    -- має, відмовляє вже при створенні версії опису.
    (N'err.ECR-RPT-0422.unknownColumn',      N'en', N'Row source "{rowSource}" has no column "{columnCode}".', 1),
    (N'err.ECR-RPT-0422.columnKindMismatch', N'en', N'Column "{columnCode}" is "{expectedKind}" in the row source, not "{kind}".', 1),
    (N'err.ECR-RPT-0422.rulesSchema',        N'en', N'Rules schema {schema} is not supported: the current one is {currentSchema}.', 1),
    (N'err.ECR-RPT-0422.rule',               N'en', N'Rule {ruleNo} ({part}) is invalid: {reason}.', 1),
    -- R6: параметри звіту (`@Name`). Оголошення перевіряється при створенні
    -- версії, значення — при побудові зрізу; обидві відмови адресують параметр
    -- його іменем, бо іншого способу знайти його в описі немає.
    (N'err.ECR-RPT-0422.parameter',          N'en', N'Report parameter "{code}" is not declared correctly: {reason}.', 1),
    (N'err.ECR-RPT-0422.parameterUnknown',   N'en', N'The report version declares no parameter "{code}".', 1),
    (N'err.ECR-RPT-0422.parameterRequired',  N'en', N'Report parameter "{code}" is required: it has neither a value nor a default.', 1),
    (N'err.ECR-RPT-0422.parameterType',      N'en', N'Report parameter "{code}" ({part}) expects a value of type {expectedType}.', 1),
    -- R8: макет зрізу (одна група й підсумки). Перевіряється при створенні
    -- версії тим самим кодом, яким його застосує видача, тож підсумок над
    -- колонкою, якої в описі немає, не доживає до екрана.
    (N'err.ECR-RPT-0422.layout',             N'en', N'The report layout ({part}) is invalid: {reason}.', 1),
    -- R9: підписи колонок мовами каталогу. Порожня назва не є «назви немає»:
    -- вона доїхала б до заголовка книги порожньою коміркою, тобто колонка
    -- держформи лишилася б без підпису. Відсутність виражається відсутністю ключа.
    (N'err.ECR-RPT-0422.columnName',         N'en', N'Column "{columnCode}" has an invalid name for language "{language}" ({part}).', 1),
    (N'err.ECR-RPT-0404.snapshot',           N'en', N'Snapshot {snapshotId} does not exist.', 1),
    -- R7: книга зрізу будується в пам'яті цілком, тому стеля рядків — відмова,
    -- а не мовчазне обрізання: книга з «майже всіма» рядками виглядає повною.
    (N'err.ECR-RPT-0422.exportTooLarge',     N'en', N'Snapshot {snapshotId} has more than {limit} rows: a workbook that large is not built. Use the rows endpoint or the rpt.v_* view.', 1),
    -- Борг локалізації (contracts/localization-debt.md, ReportDefHandlers.cs):
    -- заведення й публікація опису звіту та його версій.
    (N'err.ECR-RPT-0422.noColumns',          N'en', N'A report version needs at least one column: there would be nothing to show in the snapshot.', 1),
    (N'err.ECR-RPT-0422.columnKind',         N'en', N'Column kind "{kind}" is unknown: the snapshot row stores only {allowedKinds}.', 1),
    (N'err.ECR-RPT-0422.duplicateColumn',    N'en', N'Column "{code}" is described twice: the column code is part of the snapshot row''s key.', 1),
    (N'err.ECR-RPT-0422.rowSource',          N'en', N'Row source "{rowSource}" is not supported by the snapshot builder: today there is one — "{supportedSource}" (the current run''s results).', 1),
    (N'err.ECR-RPT-0422.nameRequired',       N'en', N'Give the report a name in at least one language: it is addressed by that name in the list.', 1),
    (N'err.ECR-RPT-0422.version',            N'en', N'The report version number must be from 1 to {maxLength} characters.', 1),
    (N'err.ECR-RPT-4091.code',               N'en', N'A report with code "{code}" is already described.', 1),
    (N'err.ECR-RPT-0404.def',                N'en', N'Report definition {reportDefId} does not exist.', 1),
    (N'err.ECR-RPT-0404.version',            N'en', N'Report version {reportVersionId} does not exist.', 1),
    (N'err.ECR-RPT-0404.versionWrongDef',    N'en', N'Version {reportVersionId} belongs to definition {versionDefId}, not {reportDefId}.', 1),
    -- ⛔ `Q-341`, перший зріз: відмови збереження комірки (`PatchCellsHandler`)
    -- — найгарячіший шлях продукту, бо через нього йде КОЖНЕ збереження в
    -- сітці. Ключі мають суфікс (`err.<код>.<що саме>`), а не форму рівно
    -- `err.<код>`: останню читає `LocalizedTitleAsync` для ЗАГОЛОВКА, і збіг
    -- надрукував би той самий текст двічі — заголовком і подробицею.
    -- Приватна область: сітка доступна лише після входу.
    (N'err.ECR-AUTH-0401.anonymousWrite',    N'en', N'An anonymous request cannot change data: sign in again.', 1),
    (N'err.ECR-ROW-0409.fixedRowMode',       N'en', N'Table "{tableCode}" has RowMode = {rowMode}: its rows come from the template, so an arbitrary key is not accepted.', 1),
    (N'err.ECR-ROW-0409.dynamicRowLimit',    N'en', N'Creating {adding} row(s) would exceed the dynamic-row limit of table "{tableCode}": {existing} existing + {adding} new > {max}.', 1),
    (N'err.ECR-ROW-0409.rowKeysExist',       N'en', N'Rows with these keys already exist.', 1),
    (N'err.ECR-CELL-0409.batchStale',        N'en', N'The batch was rejected: {rowCount} row(s) changed since you loaded them.', 1),
    (N'err.ECR-ACCS-0403.deniedCells',       N'en', N'Cells you may not edit in this batch: {deniedCount}. Reason for the first: {reason}.', 1),
    (N'err.ECR-CELL-0422.validationBlocked', N'en', N'Validation rejected the save: {cellCount} cell(s) with an error.', 1),
    (N'err.ECR-CELL-0422.unknownColumn',     N'en', N'There is no column "{columnCode}" in this template version.', 1),
    -- ⛔ `U-02`: найчастіша інтерактивна відмова продукту — набране в комірку
    -- не того типу. `CellValueReader.Mismatch` збирав речення рядком у коді
    -- («Колонка «C5» очікує число.»), і воно доїжджало на екран українською
    -- під англійським заголовком. Ключів ЧОТИРИ, а не один із підстановкою
    -- `{expected}`: резолвер (`UiStringResolver.Format`) підставляє рядки як
    -- є і другого рівня розв'язання ключів не має, тож слово «число» лишилося
    -- б українським усередині англійського речення. Окремий ключ на тип дає
    -- ще й граматично правильну фразу в кожній мові.
    -- ⚠ Поле `expected` у `Details` тепер СТАЛЕ кодове слово (`Number`,
    -- `Boolean`, `Date`, `Identifier`) — як `{status}` і `{reason}` у сусідніх
    -- шаблонах; у тексті воно не підставляється.
    -- Приватна область: сітка доступна лише після входу.
    (N'err.ECR-CELL-0422.expectsNumber',     N'en', N'Column "{columnCode}" expects a number.', 1),
    (N'err.ECR-CELL-0422.expectsBoolean',    N'en', N'Column "{columnCode}" expects true or false.', 1),
    (N'err.ECR-CELL-0422.expectsDate',       N'en', N'Column "{columnCode}" expects a date.', 1),
    (N'err.ECR-CELL-0422.expectsIdentifier', N'en', N'Column "{columnCode}" expects the identifier of a registry entry.', 1),
    -- X-31: колонка одиниць очікує ідентифікатор ОДИНИЦІ, а не запису довідника.
    (N'err.ECR-CELL-0422.expectsUnitIdentifier', N'en', N'Column "{columnCode}" expects the identifier of a unit of measure.', 1),
    -- ⛔ `U-23`: число з більшою кількістю знаків після коми, ніж тримає
    -- сховище (`decimal(34,16)`), доти приймалося й мовчки округлювалося на
    -- клієнті SqlClient, а запит закінчувався «Saved». Тепер — відмова тим
    -- самим механізмом, що й `expects*` вище (правило `ФВ-9.16c`/`D-116`:
    -- округлює лише вставка, видимо, на клієнті). `{maxScale}` — число рядком.
    (N'err.ECR-CELL-0422.tooManyDecimals',   N'en', N'Column "{columnCode}" keeps at most {maxScale} digits after the decimal point.', 1),
    (N'err.ECR-CELL-0422.tooManyIntegerDigits', N'en', N'Column "{columnCode}" keeps at most {maxIntegerDigits} digits before the decimal point.', 1),
    (N'err.ECR-CALC-0437.requiredInputs',    N'en', N'Required methodology input columns are empty: {rowCount} row(s) with an error.', 1),
    (N'err.ECR-CELL-4223.missingEntry',      N'en', N'Reference to a registry entry that does not exist: {cellCount} cell(s).', 1),
    -- B-02: той самий код для колонки `Unit`, власне речення.
    (N'err.ECR-CELL-4223.missingUnit',       N'en', N'Reference to a unit of measure that does not exist: {cellCount} cell(s).', 1),
    -- ⛔ `Q-341`, другий зріз: УСІ кидки `ECR-DOC-0404` — «документа/аркуша/
    -- екземпляра таблиці немає». Це найчастіший 404 продукту: код лежить на
    -- шляху відкриття сітки (`GetTableSliceHandler`, `RowStore`,
    -- `AccessDecisionService` — кожне читання і кожне збереження), на подачі
    -- аркуша, на перерахунку та на обміні книгами. Зріз узятий по КОДУ, а не
    -- по файлу: код — саме та одиниця, яку читає клієнт, і один текст на подію
    -- незалежно від того, яким шляхом код до неї дійшов.
    -- Приватна область: документи видно лише після входу.
    (N'err.ECR-DOC-0404.document',           N'en', N'Document {documentId} was not found.', 1),
    (N'err.ECR-DOC-0404.tableInstance',      N'en', N'Table instance {tableInstanceId} was not found.', 1),
    (N'err.ECR-DOC-0404.tableInstanceNotInDocument', N'en', N'Table instance {tableInstanceId} does not belong to document {documentId}.', 1),
    (N'err.ECR-DOC-0404.sheetNotInDocument', N'en', N'Sheet {sheetDefId} is not part of document {documentId}.', 1),
    (N'err.ECR-DOC-0404.periodEmpty',        N'en', N'Document {documentId} for period {periodKey} does not exist or is empty.', 1),
    (N'err.ECR-DOC-0404.exportExpired',      N'en', N'The workbook is gone or has expired: build the export again.', 1),
    (N'err.ECR-DOC-0404.version',             N'en', N'Version {versionId} of document {documentId} was not found.', 1),
    (N'err.ECR-DOC-0422.compareVersion',      N'en', N'A version must be a number or "current".', 1),
    (N'err.ECR-DOC-0422.comparePeriods',      N'en', N'Versions from different periods cannot be compared.', 1),

    -- ⛔ `BE-02`, скасування фонової задачі. Три подробиці однієї дії, і всі
    -- три людина бачить у момент, коли ТІЛЬКИ ЩО натиснула кнопку: задачі
    -- немає, задача чужа, задача вже завершилась. Без ключа сюди приїхало б
    -- українське речення — мови продукту `en`/`ru`/`kz` (`ФВ-14.9`).
    -- ⚠ `{state}` лишається кодовим словом (`Succeeded`/`Failed`/`Cancelled`):
    -- це те саме слово, що бейдж у переліку задач, і перекладати його тут
    -- означало б розійтися з екраном.
    -- Приватна область: задачі видно лише після входу.
    (N'err.ECR-JOB-0404.job',                 N'en', N'Background job {jobId} does not exist.', 1),
    (N'err.ECR-JOB-0409.notActive',           N'en', N'Job {jobId} is in state {state}: there is nothing to cancel.', 1),
    (N'err.ECR-AUTH-0403.jobNotYours',        N'en', N'This background job was started by someone else: permission {permission} is required to act on it.', 1),
    -- T10 #40, UX-09: ручний перезапуск проваленої задачі (`RestartJobHandler`).
    (N'err.ECR-JOB-0409.notFailed',           N'en', N'Job {jobId} is in state {state}: only a failed job can be restarted.', 1),
    (N'err.ECR-JOB-0404.restartUnavailable',  N'en', N'Job {jobId} cannot be restarted: its details did not survive a server restart.', 1),

    -- ⛔ `BE-08`, перелік задач із фільтрами. Дві подробиці — про ФІЛЬТР, а не
    -- про задачу: невідомий стан і розмір поза межами відхиляються, бо мовчазна
    -- порожня видача на друкарську помилку читається як «таких задач немає».
    -- ⚠ `err.ECR-AUTH-0401.anonymous` — окремо від `anonymousWrite` вище: там
    -- «анонім не може ЗМІНИТИ дані», а тут анонім не має «своїх» задач узагалі,
    -- і порада «увійдіть знову» доречна в обох, а речення — ні.
    (N'err.ECR-AUTH-0401.anonymous',          N'en', N'An anonymous request has no jobs of its own: sign in again.', 1),
    (N'err.ECR-REQ-0422.jobState',            N'en', N'There is no job state "{state}".', 1),
    (N'err.ECR-REQ-0422.jobLimit',            N'en', N'The number of jobs requested is out of range: {limit}.', 1),
    -- ⛔ `BE-30`: прогін перевірки узгодженості на вимогу (`System.RunJob`).
    -- Причина обов'язкова, бо прогін іде в журнал безпеки: найважча операція
    -- системи не має бути анонімною.
    -- ⚠ Конфлікт називає ЗАДАЧУ, а не просто «вже виконується»: інакше єдина
    -- дія у відповідь — тикати кнопку доти, доки не спрацює.
    (N'err.ECR-REQ-0422.consistencyRunReasonRequired', N'en', N'A reason is required to run the consistency check on demand: the run is recorded in the security journal.', 1),
    (N'err.ECR-REQ-0422.consistencyRunReasonTooLong',  N'en', N'The reason must be no longer than {max} characters.', 1),
    (N'err.ECR-REQ-0422.exportFormatUnknown',          N'en', N'There is no export format "{format}": use xlsx, csv or json.', 1),
    (N'err.ECR-JOB-0409.consistencyCheckRunning',      N'en', N'A consistency check is already in progress as job {jobId} ({state}): watch that job instead of starting a second full scan.', 1),
    -- ⚠ `BE-13`: у цьому реченні фігурні дужки лише довкола справжніх
    -- підстановок — інакше рядок сам не пройшов би перевірку, яку описує.
    (N'err.ECR-REQ-0422.placeholderMismatch', N'en', N'The placeholders of "{key}" differ from the default language: expected [{expected}], got [{actual}].', 1),
    -- BE-13 ч.2: імпорт перекладу з CSV. Відмови рядків приходять у звіті без підстановок.
    (N'err.ECR-REQ-0422.uiStringCsvLanguage',  N'en', N'"{lang}" cannot be imported or exported: the default language is the reference, and any other language must be in the language registry.', 1),
    (N'err.ECR-REQ-0422.uiStringCsvHeader',    N'en', N'The first row of the file must name a "key" column and a "{lang}" column.', 1),
    (N'err.ECR-REQ-0422.uiStringCsvTooLarge',  N'en', N'The file takes {size} bytes; the limit is {max}.', 1),
    (N'err.ECR-REQ-0422.uiStringUnknownKey',   N'en', N'This key does not exist in the default language.', 1),
    (N'err.ECR-REQ-0422.uiStringEmptyValue',   N'en', N'The translation is empty.', 1),
    (N'err.ECR-REQ-0422.uiStringTooLong',      N'en', N'The translation is longer than 1000 characters.', 1),
    (N'err.ECR-REQ-0422.uiStringDuplicateKey', N'en', N'This key already appears earlier in the file.', 1),
    -- BE-33: канали сповіщень. У відмові вебхука немає ні URL, ні хоста — URL є секретом.
    (N'err.ECR-REQ-0422.notificationChannelInvalid',   N'en', N'A channel needs a name of up to 100 characters; an SMTP channel also needs at least one recipient.', 1),
    (N'err.ECR-REQ-0422.notificationChannelNameTaken', N'en', N'A channel named "{name}" already exists.', 1),
    (N'err.ECR-REQ-0422.webhookUrlNotAllowed',         N'en', N'The webhook address must use https and point to an allowed host.', 1),
    -- ⛔ 2026-09-20: транспорт SMTP задає застосунок, не канал. Речення має
    -- сказати це прямо: користувач, який щойно ввів адресу сервера, інакше
    -- шукатиме друкарську помилку там, де її немає.
    (N'err.ECR-REQ-0422.notificationChannelTransportFromConfiguration', N'en', N'The SMTP server, port, TLS and sender address come from the application settings; a channel cannot set them.', 1),
    (N'err.ECR-REQ-0422.notificationChannelRecipientInvalid',           N'en', N'One of the recipients is not an email address.', 1),
    (N'err.ECR-INT-0404.notificationChannel',          N'en', N'Notification channel {id} does not exist.', 1),
    (N'err.ECR-REQ-0422.notificationRuleInvalid',      N'en', N'A rule matrix accepts a known event and severity, and at most one rule per event and channel.', 1),
    -- ⚠ Той самий вибір, що в `jobState`: невідомий фільтр — відмова, а не
    -- мовчазне «усі». Порожній перелік на друкарську помилку читався б як
    -- «таких доставок не було».
    (N'err.ECR-REQ-0422.notificationDeliveryStatus',   N'en', N'There is no delivery outcome "{status}".', 1),

    -- BE-21b: розклад збору редагується з інтерфейсу.
    -- ⚠ Cron перевіряється ДО запису, тому відмова називає і сам вираз, і
    -- причину, яку повернув планувальник: без причини «invalid cron» не
    -- підказує, що бракує саме знака «?» в одному з полів дня.
    -- ⚠ `collectionScheduleNotApplied` — випадок, коли рядок УЖЕ збережено, а
    -- планувальник його не взяв; мовчазне «ок» тут показувало б увімкнений
    -- збір, якого не відбудеться жодного разу.
    (N'err.ECR-INT-0404.collectionSchedule',              N'en', N'Collection schedule {id} does not exist.', 1),
    (N'err.ECR-REQ-0422.collectionScheduleCron',          N'en', N'The cron expression "{cron}" was refused by the scheduler: {reason}', 1),
    (N'err.ECR-REQ-0422.collectionScheduleCronLength',    N'en', N'The cron expression must be between 1 and {max} characters long.', 1),
    (N'err.ECR-REQ-0422.collectionScheduleIfMatch',       N'en', N'This request needs an If-Match header carrying the rowVersion of the schedule you read.', 1),
    (N'err.ECR-REQ-0422.collectionScheduleNotApplied',    N'en', N'The schedule was saved, but the scheduler did not accept it: {reason}', 1),
    (N'err.ECR-JOB-0409.collectionScheduleChanged',       N'en', N'Someone else changed this schedule after you read it: reload the list and repeat the change.', 1),
    -- ⚠ Створення розкладу. Дублікат — це 409, а не мовчазне створення другого
    -- рядка: два розклади на одну сутність означають два тригери планувальника
    -- з тим самим завданням, тобто подвійний збір, якого не видно ніде.
    (N'err.ECR-INT-0404.sourceEntity',                    N'en', N'Source entity {id} does not exist.', 1),
    (N'err.ECR-JOB-0409.collectionScheduleExists',        N'en', N'This source entity already has schedule {scheduleId}: edit it instead of adding a second one.', 1),

    -- BE-21: самі джерела даних.
    -- ⛔ Поля секрету в цій формі немає — рішення людини на Q15-06: джерела
    -- ходять під службовим обліковим записом. Саме тому потрібен
    -- `dataSourceEndpointCarriesSecret`: коли сховища секретів немає, єдиний
    -- спосіб покласти пароль у базу — вписати його в адресу, а для транспорту
    -- Sql адреса і є рядком з'єднання. Відмова НЕ повторює введеного.
    -- ⚠ Видалення — заборона, не каскад: відмова називає числа, бо єдина
    -- корисна дія у відповідь — прибрати саме їх або вимкнути джерело.
    (N'err.ECR-INT-0404.dataSource',                      N'en', N'Data source {id} does not exist.', 1),
    (N'err.ECR-REQ-0422.dataSourceInvalid',               N'en', N'A data source needs a name in at least one language, a known transport, an address of up to 400 characters and a parallelism ceiling between 1 and 32.', 1),
    (N'err.ECR-REQ-0422.dataSourceCodeTaken',             N'en', N'A data source with code "{code}" already exists.', 1),
    (N'err.ECR-REQ-0422.dataSourceEndpointCarriesSecret', N'en', N'The address of a data source must not carry credentials: sources connect under the service account.', 1),
    (N'err.ECR-REQ-0422.dataSourceTestReason',            N'en', N'A reason of up to 400 characters is required to test the connection: the attempt is recorded in the security journal.', 1),
    (N'err.ECR-JOB-0409.dataSourceInUse',                 N'en', N'This data source still carries {sourceEntities} collection entities and {collectionSchedules} schedules: disable it instead of deleting it.', 1),
    (N'err.ECR-JOB-0409.dataSourceTestRunning',           N'en', N'A connection test for data source "{code}" is already running: wait for it to finish.', 1),
    -- ⚠ Версія рядка з'єднання: той самий контракт If-Match, що в розкладах.
    (N'err.ECR-REQ-0422.dataSourceIfMatch',               N'en', N'This request needs an If-Match header carrying the rowVersion of the data source you read.', 1),
    (N'err.ECR-JOB-0409.dataSourceChanged',               N'en', N'Someone else changed this data source after you read it: reload it and repeat the change.', 1),
    -- ФВ-13.13: каталог імен джерела для мапінгу; межа очікування коротка.
    (N'err.ECR-REQ-0422.catalogQueryInvalid',             N'en', N'A catalog page holds 1 to 200 items, a search is up to 200 characters, and the cursor must come from the previous page.', 1),
    (N'err.ECR-INT-0503.catalogTimeout',                  N'en', N'Data source "{code}" did not return its catalog within {timeoutSeconds} s. Try again later.', 1),
    (N'err.ECR-INT-0503.catalogUnavailable',              N'en', N'Data source "{code}" is unavailable, so its catalog could not be read. Try again later.', 1),
    -- ФВ-13.17: «Перевірити конфігурацію» до першого збору — пробне читання
    -- одного значення; шлях, якого немає в каталозі, дає підказку схожих імен.
    (N'err.ECR-REQ-0422.probePathInvalid',                N'en', N'A probe path is required, from 1 to 500 characters.', 1),
    (N'err.ECR-INT-0503.probeTimeout',                    N'en', N'Data source "{code}" did not answer the probe within {timeoutSeconds} s. Try again later.', 1),
    (N'err.ECR-INT-0503.probeUnavailable',                N'en', N'Data source "{code}" is unavailable, so the probe could not run. Try again later.', 1),
    (N'err.ECR-INT-0404.sourcePathNotFound',              N'en', N'The path "{path}" was not found in data source "{code}". Check the suggested names.', 1),
    -- Заведення мапінгу поля джерела (`CreateEntityFieldMapHandler`, директива
    -- №15, «Прогалина 1» — доти EntityFieldMap заводився лише двома
    -- статичними фабриками домену, і жодного шляху АПІ до створення не було).
    (N'err.ECR-REQ-0422.entityFieldMapSourceField',            N'en', N'The source field (sourceField) cannot be empty.', 1),
    (N'err.ECR-REQ-0422.entityFieldMapColumnExtraField',       N'en', N'A mapping to a column (targetKind=Column) does not accept targetRegistryFieldDefId.', 1),
    (N'err.ECR-REQ-0422.entityFieldMapColumnRequired',         N'en', N'A mapping to a column (targetKind=Column) requires targetColumnDefId.', 1),
    (N'err.ECR-INT-0405.column',                               N'en', N'Column {columnDefId} does not exist, or it was deleted.', 1),
    (N'err.ECR-REQ-0422.entityFieldMapRegistryFieldExtraColumn', N'en', N'A mapping to a registry field (targetKind=RegistryField) does not accept targetColumnDefId.', 1),
    (N'err.ECR-REQ-0422.entityFieldMapRegistryFieldRequired',  N'en', N'A mapping to a registry field (targetKind=RegistryField) requires targetRegistryFieldDefId.', 1),
    (N'err.ECR-INT-0405.registryField',                        N'en', N'Registry field {registryFieldDefId} does not exist.', 1),
    (N'err.ECR-REQ-0422.entityFieldMapTargetKindUnknown',      N'en', N'Unknown mapping target kind: {targetKind}.', 1),
    -- Перегляд мапінгу на реальних рядках джерела (`PreviewMappingHandler`, ФВ-13.14).
    (N'err.ECR-REQ-0422.mappingPreviewWindow',                 N'en', N'The preview window is empty: start {fromUtc} is not before end {toUtc}.', 1),
    -- ⛔ `BE-27`: дії над мапінгом. Пауза існує, щоб мапінг можна було спинити,
    -- НЕ стираючи пояснення вже зібраних точок, — тому речення про видалення
    -- мусить назвати її прямо, інакше відмова виглядає глухим кутом.
    -- ⚠ Зміну одиниці приймає лише людина (ФВ-16.9): мовчазна конверсія дає
    -- правдоподібні числа, помилку в яких знаходять на звірці через місяць.
    (N'err.ECR-INT-0404.fieldMap',                        N'en', N'Field mapping {fieldMapId} does not exist.', 1),
    (N'err.ECR-INT-0409.mappingAlreadyPaused',            N'en', N'The mapping of field "{sourceField}" is already paused.', 1),
    (N'err.ECR-INT-0409.mappingNotPaused',                N'en', N'The mapping of field "{sourceField}" is not paused: there is nothing to resume.', 1),
    (N'err.ECR-INT-0409.mappingUnitNotDeclared',          N'en', N'The mapping of field "{sourceField}" declares no source unit, so there is no change to accept. Set the unit by editing the mapping instead.', 1),
    (N'err.ECR-INT-0409.mappingUnitUnchanged',            N'en', N'The mapping of field "{sourceField}" already declares that unit.', 1),
    (N'err.ECR-INT-0409.mappingHasCollectedData',         N'en', N'{collectedPoints} points have already been collected through the mapping of field "{sourceField}". Deleting it would leave those points without the unit and the target that explain them: pause the mapping instead.', 1),
    -- BE-20: власні налаштування інтерфейсу (`/me/preferences`).
    (N'err.ECR-REQ-0422.preferenceKeyInvalid',            N'en', N'"{key}" is not a known preference key, or it is longer than {max} characters.', 1),
    (N'err.ECR-REQ-0422.preferenceValueInvalid',          N'en', N'The value of preference "{key}" is not valid JSON.', 1),
    (N'err.ECR-REQ-0422.preferenceValueTooLarge',         N'en', N'The value of preference "{key}" takes {size} bytes; the limit is {max}.', 1),
    (N'err.ECR-REQ-0422.preferenceLimitReached',          N'en', N'You already keep {max} preferences: delete one before adding "{key}".', 1),
    -- ФВ-16.9: зміна одиниці ставить на паузу лише свій мапінг і чекає рішення.
    (N'err.ECR-INT-0409.mappingUnitChangeNotPending',     N'en', N'The mapping of field "{sourceField}" is not waiting for a decision about its unit.', 1),
    (N'err.ECR-INT-0409.mappingUnitChangePending',        N'en', N'The mapping of field "{sourceField}" is paused because its source now reports unit "{actualUnitCode}". Resolve the unit change first: accepting it resumes collection.', 1),
    (N'err.ECR-INT-0422.pendingUnitNotInCatalog',         N'en', N'The source of field "{sourceField}" now reports unit "{unitCode}", which is not in the unit catalog. Add the unit first, then accept the change.', 1),
    -- FV-5.23: журнал прогонів збору. Невідомий стан — відмова, а не порожній
    -- перелік, що читався б як «збоїв не було».
    (N'err.ECR-INT-0404.collectionRun',                   N'en', N'Collection run {id} does not exist.', 1),
    (N'err.ECR-REQ-0422.collectionRunState',              N'en', N'There is no collection run state "{state}".', 1),
    (N'err.ECR-REQ-0422.collectionRunRange',              N'en', N'The start of the period must be earlier than its end.', 1),

    -- ⛔ Узагальнений репозиторій (`Repository<T,TId>.GetAsync`) будував
    -- повідомлення з ІМЕНІ КЛАСУ .NET: «TemplateVersion з ідентифікатором 5
    -- не знайдено». Для оператора це не назва нічого — у продукті немає
    -- сутності «TemplateVersion», є «версія шаблону». Ключ окремий на КОЖЕН
    -- тип, хоч код у двох із них спільний: один ключ на код сказав би «не
    -- знайдено шаблон» там, де немає ВЕРСІЇ, і людина шукала б не те.
    (N'err.ECR-TMPL-0404.template',          N'en', N'Template {templateId} was not found.', 1),
    (N'err.ECR-TMPL-0404.templateVersion',   N'en', N'Template version {versionId} was not found.', 1),

    -- ⛔ `BE-26`: картка шаблону — перейменування й архівування. Архів каже
    -- «нового на цьому шаблоні не заводимо», а не «старе зникло»: документ
    -- назавжди лишається на своїй версії (рішення людини на `Q15-05`), тож
    -- речення мусить це сказати — інакше адміністратор боятиметься кнопки.
    (N'err.ECR-TMPL-0422.templateNameRequired',  N'en', N'A template needs a name in at least one language.', 1),
    -- V-19: відмова збереження/публікації виразу. Коли зауваження несе власний
    -- ключ (`expr.*`), відмова бере його; ці два — для решти.
    (N'err.ECR-TMPL-0422.expressionInvalid',     N'en', N'The expression is not valid: check {code} at position {position}.', 1),
    (N'err.ECR-TMPL-0422.publishRejected',       N'en', N'The template version cannot be published: {count} problem(s) found; the first is {code} at position {position}.', 1),
    (N'err.ECR-TMPL-0422.publishReasonRequired', N'en', N'A publication reason is required: an empty line explains nothing to whoever later asks why this version was put into use.', 1),
    (N'err.ECR-TMPL-0409.templateArchived',      N'en', N'Template "{code}" is archived: new documents are no longer created from it, while existing ones keep working.', 1),
    (N'err.ECR-TMPL-0409.templateAlreadyArchived', N'en', N'Template "{code}" is already archived.', 1),
    (N'err.ECR-TMPL-0409.templateNotArchived',     N'en', N'Template "{code}" is not archived: there is nothing to bring back.', 1),
    -- B-04 / X-30: номер нової версії шаблону і джерело клону.
    (N'err.ECR-TMPL-0422.versionNumberRequired',   N'en', N'A version number is required, in the form Major.Minor.Patch.Build.', 1),
    (N'err.ECR-TMPL-0422.versionNumberFormat',     N'en', N'Version number "{version}" does not match the form Major.Minor.Patch.Build.', 1),
    (N'err.ECR-TMPL-0422.cloneSourceOtherTemplate', N'en', N'Version {sourceVersionId} belongs to another template: a version can only be cloned within its own template ({templateId}).', 1),
    (N'err.ECR-TMPL-0409.versionNumberTaken',      N'en', N'Version {version} already exists in this template.', 1),

    -- Борг локалізації: правила доступу до періоду й зв'язки між таблицями
    -- (`PeriodAccessRuleHandlers`/`PeriodAccessRuleDef`,
    -- `TableRelationHandlers`/`TableRelationDef`). `tableNotInVersion` спільний
    -- для обох обробників — той самий факт («таблиця не в цій версії»),
    -- незалежно від того, звідки код до нього дійшов.
    (N'err.ECR-TMPL-0422.periodAccessRuleNoTarget',  N'en', N'A period access rule must apply to a sheet or a table: leaving both empty means a rule that blocks nothing.', 1),
    (N'err.ECR-TMPL-0422.sheetNotInVersion',         N'en', N'Sheet {sheetDefId} does not belong to template version {versionId}.', 1),
    (N'err.ECR-TMPL-0422.tableNotInVersion',         N'en', N'Table {tableDefId} does not belong to template version {versionId}.', 1),
    (N'err.ECR-TMPL-0422.sourceWindowRequiresColumn', N'en', N'The SourceWindow kind requires a source column: without it the rule looks configured but blocks nothing.', 1),
    (N'err.ECR-TMPL-0422.unknownPeriodAccessRuleKind', N'en', N'Period access rule kind "{ruleKind}" does not exist.', 1),
    (N'err.ECR-TMPL-0404.periodAccessRule',          N'en', N'Rule {ruleId} was not found in template version {versionId}.', 1),
    (N'err.ECR-TMPL-0404.tableRelation',             N'en', N'Relation "{relationCode}" was not found in template version {versionId}.', 1),
    (N'err.ECR-SCHM-0409.templateRelationBreaking',  N'en', N'This change to relation "{relationCode}" is breaking: documents are already attached to this template version. Values in them were computed using the relation, and removing or changing it now would silently alter what was already submitted.', 1),
    (N'err.ECR-TMPL-0422.relationSelfLink',          N'en', N'Relation "{relationCode}" links table {tableDefId} to itself: this is not allowed (CK_Rel_NotSelf).', 1),
    (N'err.ECR-TMPL-0422.relationMatchRequired',     N'en', N'Relation "{relationCode}" has no row match (MatchJson): without it, it connects no rows while looking configured.', 1),
    (N'err.ECR-TMPL-0422.relationMatchNotObject',    N'en', N'The row match (MatchJson) of relation "{relationCode}" must be a JSON object.', 1),
    (N'err.ECR-TMPL-0422.relationMatchInvalidJson',  N'en', N'The row match (MatchJson) of relation "{relationCode}" is not valid JSON.', 1),
    (N'err.ECR-TMPL-0422.relationUnknownOnSourceChange', N'en', N'Unknown reaction to a source change: {onSourceChange}. Allowed values are 0 (Recalc), 1 (Warn), 2 (Block).', 1),
    (N'err.ECR-TMPL-0422.relativeWindowOffsetNotPositive', N'en', N'The relative window offset must be positive; got {offset}. Zero is EditablePeriodOnly — use that kind instead.', 1),
    (N'err.ECR-TMPL-0422.expressionRequired',        N'en', N'An Expression rule without a condition does nothing: an empty condition here is the same as no rule.', 1),
    (N'err.ECR-CFG-0422.hideRetired',                N'en', N'The "Hide" behavior can no longer be set: hiding is not one of the three allowed reactions. Use "ReadOnly" instead — it means the same "not allowed".', 1),

    -- Борг локалізації: конструктор колонки й таблиці (`ColumnDefHandlers`/
    -- `ColumnDef`, `TableDefHandlers`/`TableDef`). `table`/`sheet` — той самий
    -- факт, звідки б до нього не дійшли (створення/зміна чи видалення).
    (N'err.ECR-TMPL-0404.table',                     N'en', N'Table {tableDefId} does not exist in template version {versionId}.', 1),
    (N'err.ECR-TMPL-0404.columnCode',                N'en', N'Column "{columnCode}" does not exist in table {tableDefId}.', 1),
    (N'err.ECR-TMPL-0404.sheet',                     N'en', N'Sheet "{sheetCode}" does not exist in template version {versionId}.', 1),
    (N'err.ECR-TMPL-0404.tableByCode',               N'en', N'Table "{tableCode}" does not exist on sheet "{sheetCode}".', 1),
    (N'err.ECR-TMPL-0422.columnCodeTakenByDeleted',  N'en', N'Column code "{columnCode}" in table {tableDefId} is taken by a deleted column: cells still reference it by code, so it cannot be reused in this version. Use a different code or clone the version.', 1),
    (N'err.ECR-TMPL-0422.columnDataTypeImmutable',   N'en', N'The data type of column "{columnCode}" cannot change after creation ({oldDataType} -> {newDataType}). Create a new column or clone the version.', 1),
    (N'err.ECR-TMPL-0422.scaleExceedsPrecision',     N'en', N'Scale ({scale}) cannot exceed precision ({precision}) in column "{columnCode}".', 1),
    (N'err.ECR-TMPL-0422.lookupRequiresLookupType',  N'en', N'A registry can only be attached to a Lookup column; column "{columnCode}" has type {dataType}.', 1),
    (N'err.ECR-TMPL-0422.unitColumnHasRowUnit',      N'en', N'Column "{columnCode}" has type Unit: its unit is set per row (doc.CellValue.ValueUnitId), not on the column.', 1),
    (N'err.ECR-TMPL-0422.tableCodeTakenByDeleted',   N'en', N'Table code "{tableCode}" on sheet "{sheetCode}" is taken by a deleted table: it cannot be reused in this version. Use a different code or clone the version.', 1),
    (N'err.ECR-TMPL-0422.maxDynamicRowsNotPositive', N'en', N'MaxDynamicRows must be positive; got {value}.', 1),
    (N'err.ECR-TMPL-0422.maxDynamicRowsNeedsDynamicMode', N'en', N'MaxDynamicRows only makes sense where users add rows; table "{tableCode}" is in {rowMode} mode.', 1),
    (N'err.ECR-TMPL-0422.tableIsDynamic',            N'en', N'Table "{tableCode}" is dynamic: its rows are created at runtime, not in the template.', 1),
    (N'err.ECR-TMPL-0409.columnCodeTaken',           N'en', N'A column with code "{columnCode}" already exists in table "{tableCode}".', 1),
    (N'err.ECR-TMPL-0409.rowKeyTaken',               N'en', N'A row with key "{rowKey}" already exists in table "{tableCode}".', 1),
    (N'err.ECR-TMPL-0409.headerFieldCodeTaken',      N'en', N'A header field with code "{headerFieldCode}" already exists in this template version.', 1),

    -- Поля шапки документа (foundation): той самий draft->publish шлях, що
    -- колонки таблиці, тому подробиці — під ECR-TMPL-0422/0404, як у колонок.
    (N'err.ECR-TMPL-0422.headerFieldTypeNotAllowed',            N'en', N'A document header field cannot have type {dataType}: the header stores an entered value, it does not compute one.', 1),
    (N'err.ECR-TMPL-0422.headerFieldLookupRequiresLookupType',  N'en', N'A registry can only be attached to a Lookup header field; field "{headerFieldCode}" has type {dataType}.', 1),
    (N'err.ECR-TMPL-0422.headerFieldCodeTakenByDeleted',        N'en', N'Header field code "{headerFieldCode}" is taken by a deleted field: document values still reference it by code, so it cannot be reused in this version. Use a different code or clone the version.', 1),
    (N'err.ECR-TMPL-0422.headerFieldDataTypeImmutable',         N'en', N'The data type of header field "{headerFieldCode}" cannot change after creation ({oldDataType} -> {newDataType}). Create a new field or clone the version.', 1),
    (N'err.ECR-HDR-0404.headerField',                N'en', N'Header field "{headerFieldCode}" does not exist in this document''s template version.', 1),
    (N'err.ECR-HDR-0422.validationBlocked',          N'en', N'The value for header field "{headerFieldCode}" does not match its type or required setting.', 1),
    -- ⛔ Було одним ключем `typeMismatch` з `{expected}`, а значенням
    -- `expected` їхало українське слово: «Header field "QTY" expects a
    -- число.». Резолвер другого рівня ключів не має — тип тепер частина
    -- ключа, як `err.ECR-CELL-0422.expects*` (`U-02`). Старий ключ — у
    -- «Прибраних ключах» вище.
    (N'err.ECR-HDR-0422.expectsNumber',              N'en', N'Header field "{headerFieldCode}" expects a number.', 1),
    (N'err.ECR-HDR-0422.expectsBoolean',             N'en', N'Header field "{headerFieldCode}" expects true or false.', 1),
    (N'err.ECR-HDR-0422.expectsDate',                N'en', N'Header field "{headerFieldCode}" expects a date.', 1),
    (N'err.ECR-HDR-0422.expectsIdentifier',          N'en', N'Header field "{headerFieldCode}" expects the identifier of a registry entry or unit.', 1),
    -- `U-23` для шапки: той самий `decimal(34,16)`, та сама відмова.
    (N'err.ECR-HDR-0422.tooManyDecimals',            N'en', N'Header field "{headerFieldCode}" keeps at most {maxScale} digits after the decimal point.', 1),
    (N'err.ECR-HDR-0422.tooManyIntegerDigits',       N'en', N'Header field "{headerFieldCode}" keeps at most {maxIntegerDigits} digits before the decimal point.', 1),

    (N'err.ECR-ROW-0404.tableRow',           N'en', N'Table row {rowId} was not found.', 1),
    (N'err.ECR-REG-0404.registryEntry',      N'en', N'Registry entry {entryId} was not found.', 1),

    -- ⚠ `BE-24`: НЕ те саме, що рядок вище. Там немає ЗАПИСУ, тут немає самого
    -- довідника — і найчастіша причина друга: друкарська помилка в коді.
    (N'err.ECR-REG-0404.registry',           N'en', N'Registry "{registryCode}" was not found.', 1),

    -- Конструктор довідника й перемикання master: заголовок `ECR-REG-0422`
    -- нейтральний, причину каже подробиця.
    (N'err.ECR-REG-0404.field',              N'en', N'Field {fieldId} was not found in registry "{registryCode}".', 1),
    (N'err.ECR-REG-0404.rule',               N'en', N'Rule {ruleId} was not found in registry "{registryCode}".', 1),
    (N'err.ECR-REG-0422.definitionReasonRequired', N'en', N'Give a reason for the change: the definition changes how entries already saved are read.', 1),
    (N'err.ECR-REG-0422.noKeyField',         N'en', N'Registry "{registryCode}" would be left without a key field, so an entry business key could not be built.', 1),
    (N'err.ECR-REG-0422.fieldRemoved',       N'en', N'Registry fields cannot be removed: entries reference their values. Make the field optional instead. Fields missing from the request: {missingCount}.', 1),
    (N'err.ECR-REG-0422.fieldCodeImmutable', N'en', N'The code of field "{fieldCode}" cannot be changed: expressions and mappings reference it.', 1),
    (N'err.ECR-REG-0422.fieldTypeImmutable', N'en', N'The type of field "{fieldCode}" cannot be changed: it defines how saved values are read.', 1),
    (N'err.ECR-REG-0422.unknownFieldType',   N'en', N'Field type "{dataType}" does not exist.', 1),
    (N'err.ECR-REG-0422.newFieldRequired',   N'en', N'New field "{fieldCode}" cannot be required: existing entries have no value for it. Add it as optional, fill it in, then make it required.', 1),
    (N'err.ECR-REG-0422.unknownSeverity',    N'en', N'Severity "{severity}" does not exist.', 1),
    (N'err.ECR-REG-0422.ruleKindImmutable',  N'en', N'The kind of rule "{ruleCode}" cannot be changed: add a new rule of the kind you need.', 1),
    (N'err.ECR-REG-0422.unknownRuleKind',    N'en', N'Rule kind "{ruleKind}" does not exist: registry rules are RequiredWhen, UniqueWithin, Expression or CrossRegistry.', 1),
    (N'err.ECR-REG-0422.emptySwitchSet',     N'en', N'The set of registries is empty: there is nothing to switch.', 1),
    (N'err.ECR-REG-0422.duplicateCodes',     N'en', N'Codes repeat in the set: {codes}.', 1),
    (N'err.ECR-REG-0422.switchReasonRequired', N'en', N'Give a reason for switching the master source.', 1),
    (N'err.ECR-REG-0422.openPeriod',         N'en', N'The registry source cannot be switched while periods are open: some documents would be filled from one list of entries and some from another.', 1),
    (N'err.ECR-REG-0409.entryReferenced',    N'en', N'Entry "{code}" cannot be deleted: it is still referenced {referenceCount} time(s) — by document cells, other registry entries or methodology constants. Close it with an end date instead: history stays readable and new periods will not offer it.', 1),
    -- BE-24 крок 2: чернетка опису довідника і її публікація.
    (N'err.ECR-REG-0404.definitionDraft',    N'en', N'Registry "{registryCode}" has no draft definition.', 1),
    (N'err.ECR-REG-0409.definitionDraftChanged', N'en', N'The draft definition of registry "{registryCode}" was changed or published after you opened it. Reload it and repeat your changes.', 1),
    (N'err.ECR-REG-0409.definitionDraftStale', N'en', N'The definition of registry "{registryCode}" changed (version {baseVersion} to {currentVersion}) after the draft was last saved. Reload the draft and save it again before publishing.', 1),

    -- Запис довідника (збереження, вікно дії, перелік) і доменні відмови
    -- значень, зв'язків, правил і полів.
    (N'err.ECR-REG-0404.registryId',         N'en', N'Registry {registryDefId} was not found.', 1),
    (N'err.ECR-REQ-0422.asOfRequired',       N'en', N'The asOf parameter is required: registries are temporal, and the list of entries depends on the period date, not on today.', 1),
    (N'err.ECR-REG-0422.unknownFields',      N'en', N'Registry "{registryCode}" has no fields: {fields}.', 1),
    (N'err.ECR-REG-0422.requiredFieldsMissing', N'en', N'Required fields of registry "{registryCode}" are not filled in: {fields}.', 1),
    (N'err.ECR-REG-0422.entryWrongRegistry', N'en', N'Entry {entryId} belongs to registry {ownerRegistryDefId}, not {registryDefId}.', 1),
    (N'err.ECR-REG-0422.unitOnNonNumeric',   N'en', N'A unit of measure was given to a field of type {dataType}: only numeric fields have units.', 1),
    (N'err.ECR-REG-0422.fieldTypeNotAllowed', N'en', N'A registry field cannot have type {dataType}.', 1),
    (N'err.ECR-REG-0422.valueNotString',     N'en', N'The value cannot be converted to text.', 1),
    (N'err.ECR-REG-0422.valueNotDecimal',    N'en', N'The value of a {dataType} field was passed as {valueType}: numbers are stored only as decimal.', 1),
    (N'err.ECR-REG-0422.valueNotNumber',     N'en', N'The value "{value}" is not a number for a field of type {dataType}.', 1),
    (N'err.ECR-REG-0422.valueNotBoolean',    N'en', N'The value "{value}" is not a boolean.', 1),
    (N'err.ECR-REG-0422.valueNotDate',       N'en', N'The value "{value}" is not a date.', 1),
    (N'err.ECR-REG-0422.valueNotEntryId',    N'en', N'The value "{value}" is not a registry entry identifier.', 1),
    -- V-08(b), V-17(a): значення поля Lookup — живий запис оголошеного довідника.
    (N'err.ECR-REG-0422.lookupEntryNotFound', N'en', N'Field "{field}": registry entry {value} does not exist.', 1),
    (N'err.ECR-REG-0422.lookupWrongRegistry', N'en', N'Field "{field}" looks up registry "{expectedRegistry}", but entry {value} ("{entryCode}") belongs to another registry.', 1),
    (N'err.ECR-REG-0422.selfLink',           N'en', N'Entry {entryId} cannot be linked to itself.', 1),
    (N'err.ECR-REG-0422.linkPayloadNotObject', N'en', N'Link attributes must be a JSON object.', 1),
    (N'err.ECR-REG-0422.linkPayloadInvalidJson', N'en', N'Link attributes are not valid JSON: {reason}', 1),
    (N'err.ECR-REG-0422.ruleParametersNotObject', N'en', N'Rule parameters must be a JSON object.', 1),
    (N'err.ECR-REG-0422.ruleParametersInvalidJson', N'en', N'Rule parameters are not valid JSON: {reason}', 1),
    (N'err.ECR-REG-0422.fieldWrongRegistry', N'en', N'Field "{fieldCode}" belongs to registry {ownerRegistryDefId}, not {registryDefId}.', 1),
    (N'err.ECR-REG-0422.fieldCodeTaken',     N'en', N'Registry "{registryCode}" already has a field with code "{fieldCode}".', 1),

    -- BE-24 крок 3: імпорт записів довідника з CSV (той самий патерн, що
    -- err.ECR-REQ-0422.uiStringCsv* для перекладів, BE-13 ч.2).
    (N'err.ECR-REQ-0422.registryEntriesCsvTooLarge', N'en', N'The file takes {size} bytes; the limit is {max}.', 1),
    (N'err.ECR-REG-0422.entriesCsvHeaderCode', N'en', N'The first row of the file must name a "code" column.', 1),
    (N'err.ECR-REG-0422.entriesCsvUnknownColumn', N'en', N'Registry "{registryCode}" has no field "{column}": the column is unknown.', 1),
    (N'err.ECR-REG-0422.entryCodeRequired',  N'en', N'The code column is empty.', 1),
    (N'err.ECR-REG-0422.entryCodeDuplicateInFile', N'en', N'This code already appears earlier in the file.', 1),
    (N'err.ECR-REG-0422.entryRefNotFound',   N'en', N'No entry with this code exists in the referenced registry.', 1),
    (N'err.ECR-REG-0422.entryImportRowFailed', N'en', N'The row was rejected: see the detail of the underlying rule.', 1),

    -- ⛔ Головні шляхи користувача: вхід і зміна пароля, подання / погодження /
    -- відхилення / повернення аркуша, створення документа й рядка, періоди,
    -- обмін книгами. Доти подробицею цих відмов їхало українське речення —
    -- мови, якої серед мов продукту немає.
    -- ⚠ `{reason}`, `{status}`, `{state}`, `{from}`, `{to}`, `{rowMode}` — кодові
    -- слова (`PeriodClosed`, `Submitted`): ті самі, що клієнт показує бейджами
    -- й читає з поля `reason`; їхній переклад — справа каталогу статусів.
    -- ⚠ Публічна область (0) — лише те, що видно ДО входу або замість екрана
    -- (ФВ-14.9b): вимога увійти, блокування, разовий пароль, 500.
    (N'err.ECR-AUTH-0401.signInRequired',       N'en', N'You are not signed in or your session has ended: sign in again.', 0),
    -- ⛔ `U-01`: відмова входу не мала ключа, тому `ErrorAlert.tsx` (рішення
    -- 2026-09-20 — подробиця без `messageKey` не показується) не друкував
    -- НІЧОГО, і користувач із хибним паролем бачив саму лише вказівку
    -- «Sign in to continue.». Область публічна (0): це екран ДО входу.
    -- ⚠ Текст навмисно не розрізняє «немає такого користувача» і «пароль не
    -- той» — інакше форма входу перелічує чужі облікові записи (ФВ-6.11).
    (N'err.ECR-AUTH-0401.invalidCredentials',   N'en', N'The user name or password is incorrect.', 0),
    (N'err.ECR-AUTH-0401.accountMissing',       N'en', N'Your account no longer exists: sign in again.', 1),
    (N'err.ECR-AUTH-0401.currentPasswordWrong', N'en', N'The current password is incorrect.', 1),
    -- V-06: сеанс симуляції «очима користувача» — лише читання.
    (N'err.ECR-SIM-0403.readOnly',              N'en', N'You are viewing as another user: nothing can be changed. Stop viewing to make changes.', 1),
    (N'err.ECR-SIM-0422.noSession',             N'en', N'There is no active viewing session to stop.', 1),
    (N'err.ECR-AUTH-0403.domainPassword',       N'en', N'The password of a domain account is changed in the domain, not here.', 1),
    (N'err.ECR-AUTH-0423.lockedAfterFailures',  N'en', N'The account is temporarily locked after failed sign-in attempts. Try again later.', 0),
    (N'err.ECR-AUTH-0423.lockedByAdministrator', N'en', N'The account has been locked by an administrator. Contact your administrator.', 0),
    (N'err.ECR-PWD-0428.oneTimePassword',       N'en', N'Your password was issued for one-time use: until you change it, only changing the password and signing out are available.', 0),
    (N'err.ECR-PWD-0422.tooShort',              N'en', N'The new password is shorter than {minLength} characters.', 1),
    -- ⚠ Слово в слово як старший точковий шлях (`requiresPermission` + код
    -- права): той самий факт читається однаково, яким би шляхом не дійшов.
    (N'err.ECR-AUTH-0403.permission',           N'en', N'Requires permission {permission}', 1),
    (N'err.ECR-ACCS-0403.permission',           N'en', N'Requires permission {permission}', 1),
    (N'err.ECR-AUTH-0403.noDocumentAccess',     N'en', N'You have no access to document {documentId}: {reason}.', 1),
    (N'err.ECR-AUTH-0403.noProjectGrant',       N'en', N'You have no grant on project {projectId}.', 1),
    (N'err.ECR-AUTH-0403.noProjectWriteGrant',  N'en', N'You have no write grant on project {projectId}.', 1),
    (N'err.ECR-AUTH-0403.noProjectManageGrant', N'en', N'You have no Manage grant on project {projectId}.', 1),
    (N'err.ECR-ACCS-0403.submitDenied',         N'en', N'Sheet {sheetDefId} cannot be submitted: {reason}.', 1),
    (N'err.ECR-ACCS-0403.approveDenied',        N'en', N'Sheet {sheetDefId} cannot be approved or rejected: {reason}.', 1),
    -- F-25: the same person cannot both submit and approve a sheet (four-eyes rule).
    (N'err.ECR-ACCS-0403.approveOwnSubmission', N'en', N'Sheet {sheetDefId} cannot be approved by the same person who submitted it.', 1),
    (N'err.ECR-ACCS-0403.reopenDenied',         N'en', N'Sheet {sheetDefId} cannot be returned to work: {reason}.', 1),
    (N'err.ECR-ACCS-0403.addRowDenied',         N'en', N'A row cannot be added to this table: {reason}.', 1),
    (N'err.ECR-SUB-4221.orphanedRows',          N'en', N'The sheet cannot be submitted: {rowCount} row(s) lost their registry entry.', 1),
    (N'err.ECR-SUB-4221.validationBlocked',     N'en', N'The sheet cannot be submitted: {messageCount} blocking validation error(s).', 1),
    (N'err.ECR-PRD-4223.reopenPeriodFirst',     N'en', N'Period {periodKey} is closed: reopen the period first, then the sheet.', 1),
    (N'err.ECR-DOC-0409.reopenWrongState',      N'en', N'Only a submitted or approved sheet can be returned to work; the sheet is {status}.', 1),
    (N'err.ECR-DOC-0409.submitWrongState',      N'en', N'Only a draft or rejected sheet can be submitted; the sheet is {status}.', 1),
    (N'err.ECR-DOC-0409.approveWrongState',     N'en', N'Only a submitted sheet can be approved; the sheet is {status}.', 1),
    (N'err.ECR-DOC-0409.rejectWrongState',      N'en', N'Only a submitted sheet can be rejected; the sheet is {status}.', 1),
    (N'err.ECR-DOC-0422.reopenReasonRequired',  N'en', N'A reason is required to return the sheet to work.', 1),
    (N'err.ECR-DOC-0422.rejectCommentRequired', N'en', N'A comment is required to reject the sheet.', 1),

    -- BE-31: recall of a submitted sheet by its author.
    (N'err.ECR-ACCS-0403.recallDenied',         N'en', N'Sheet {sheetDefId} cannot be recalled: the Submit grant level is required.', 1),
    (N'err.ECR-ACCS-0403.recallNotAuthor',      N'en', N'Only the person who submitted the sheet can recall it.', 1),
    (N'err.ECR-DOC-0409.recallWrongState',      N'en', N'Only a submitted sheet can be recalled; the sheet is {status}.', 1),
    (N'err.ECR-DOC-0409.recallStepSigned',      N'en', N'The sheet can no longer be recalled: approval has already started.', 1),
    -- Document.Delete: only a draft document can be deleted (decision 2026-09-21).
    (N'err.ECR-DOC-0409.deleteNotDraft',        N'en', N'Only a draft document can be deleted; sheet {sheetDefId} for period {periodKey} is {reason}.', 1),
    (N'err.ECR-DOC-0409.deleteHasHistory',      N'en', N'Only a draft document can be deleted; this document has already been through approval.', 1),
    -- Document.ChangeKey: controlled business key change (FV-3.9).
    (N'err.ECR-DOC-0409.rekeyLocked',           N'en', N'The document key cannot be changed: sheet {sheetDefId} for period {periodKey} is {reason}.', 1),
    (N'err.ECR-DOC-0409.rekeyDuplicate',        N'en', N'Another document of this project already has the key "{businessKey}".', 1),
    (N'err.ECR-DOC-0409.rekeyStale',            N'en', N'The document key has changed since it was read; it is now "{businessKey}".', 1),
    (N'err.ECR-DOC-0422.rekeyReasonRequired',   N'en', N'A reason is required to change the document key.', 1),
    (N'err.ECR-DOC-0422.rekeyKeyInvalid',       N'en', N'The new key must be 1 to {maxLength} characters and differ from the current key.', 1),
    (N'err.ECR-DOC-0422.recallReasonRequired',  N'en', N'A reason is required to recall the sheet.', 1),
    (N'err.ECR-DOC-0422.unknownSheets',         N'en', N'The document includes sheets that are not in the template version.', 1),
    (N'err.ECR-DOC-0422.sheetGroupRules',       N'en', N'The selected sheets break the sheet group rules.', 1),
    -- Sheet lock (SheetEditGate) not acquired in time: the other action is still running.
    (N'err.ECR-DOC-4091.sheetBeingSubmitted',   N'en', N'This sheet is being submitted right now. Your changes were not saved; try again in a moment.', 1),
    (N'err.ECR-DOC-4091.sheetBeingEdited',      N'en', N'This sheet is being saved or recalculated right now. The sheet was not submitted; try again in a moment.', 1),
    (N'err.ECR-ROW-0409.rowsFromTemplate',      N'en', N'Table "{tableCode}" has RowMode = {rowMode}: its rows come from the template, so rows cannot be added.', 1),
    (N'err.ECR-ROW-0409.rowLimitReached',       N'en', N'Table "{tableCode}" has reached its dynamic-row limit: {max}.', 1),
    (N'err.ECR-ROW-0409.rowKeyExists',          N'en', N'A row with key "{rowKey}" already exists in this table.', 1),
    (N'err.ECR-PRJ-0404.project',               N'en', N'Project {projectId} was not found.', 1),
    (N'err.ECR-PRJ-0404.projectOfPeriod',       N'en', N'The project of period {periodId} was not found.', 1),
    (N'err.ECR-PRD-0404.period',                N'en', N'Period {periodId} was not found.', 1),
    (N'err.ECR-PRD-0409.projectArchived',      N'en', N'Project "{projectCode}" is archived: its periods cannot be reopened.', 1),
    (N'err.ECR-PRD-0409.transitionNotAllowed',  N'en', N'Period {periodKey} cannot go from {from} to {to}.', 1),
    (N'err.ECR-PRD-0409.reopenOnlyClosed',      N'en', N'Only a closed period can be reopened; the period is {state}.', 1),
    (N'err.ECR-PRD-0422.reopenReasonRequired',  N'en', N'A reason is required to reopen the period.', 1),
    (N'err.ECR-PRD-0422.periodNotInProject',    N'en', N'Period {periodId} does not belong to project "{projectCode}".', 1),
    (N'err.ECR-PRD-0422.pinReasonRequired',     N'en', N'A reason is required to pin the current period.', 1),
    (N'err.ECR-PRD-0409.timeZoneLocked',        N'en', N'The site time zone cannot be changed once the first period has been opened.', 1),
    -- Борг локалізації (ProjectQueryHandlers.cs): перелік, створення й
    -- перехід стану проєкту, CRUD політик періодів.
    (N'err.ECR-PRD-4091.code',                  N'en', N'A period policy with code "{code}" already exists.', 1),
    (N'err.ECR-TMPL-0404.versionRequired',      N'en', N'A project cannot be created without a template version.', 1),
    (N'err.ECR-PRD-0422.periodPolicyRequired',  N'en', N'A project cannot be created without a period policy.', 1),
    (N'err.ECR-PRJ-0422.notDraft',              N'en', N'Only a draft can be activated; the project is in state {status}.', 1),
    (N'err.ECR-PRJ-0422.noPeriods',             N'en', N'The calendar of project {projectId} produced no period: check the period kind, the reporting year and the offset policy.', 1),
    (N'err.ECR-PRD-0409.openPeriods',           N'en', N'Project {projectId} has periods that are not closed: archiving is not possible.', 1),
    -- F-11 / F-12: архівований проєкт — кінцевий стан.
    (N'err.ECR-PRD-0409.projectAlreadyArchived', N'en', N'Project "{projectCode}" is already archived.', 1),
    (N'err.ECR-PRD-0409.archiveNotActive',      N'en', N'Only an active project can be archived; the project is {status}.', 1),
    (N'err.ECR-PRD-0409.projectArchivedCurrentPeriod', N'en', N'Project "{projectCode}" is archived: its current period cannot be changed.', 1),
    (N'err.ECR-PRD-0409.projectArchivedNoDocuments', N'en', N'Project {projectId} is archived: new documents cannot be created in it.', 1),
    (N'err.ECR-PRD-4225.graceAfterHardClose',   N'en', N'The grace period ({graceOffsetDays} days) cannot be longer than the hard close ({hardCloseOffsetDays} days): the period would close for good before its own grace period ends.', 1),
    (N'err.ECR-PRD-4225.negativeYearGrace',     N'en', N'The year-end grace period ({yearGraceOffsetDays} days) cannot be negative.', 1),
    -- BE-25: only a never-published, never-used methodology version can be deleted.
    (N'err.ECR-CALC-0404.version',              N'en', N'Methodology version {methodologyVersionId} does not exist in this methodology.', 1),
    (N'err.ECR-CALC-0404.constant',             N'en', N'Methodology version {methodologyVersionId} has no constant {code}.', 1),
    (N'err.ECR-CALC-0404.methodology',          N'en', N'Methodology {methodologyId} does not exist.', 1),
    (N'err.ECR-CALC-0404.formula',              N'en', N'Methodology version {methodologyVersionId} has no formula "{formulaCode}".', 1),
    (N'err.ECR-TMPL-0404.column',               N'en', N'Column {columnDefId} does not exist or has been deleted.', 1),
    (N'err.ECR-CALC-0409.versionNotDraft',      N'en', N'Only a draft methodology version can be deleted; version {version} is {reason}.', 1),
    (N'err.ECR-CALC-0409.versionUsedInCalculations', N'en', N'Methodology version {version} has already been used in calculations and cannot be deleted.', 1),
    -- D-40: with a neutral code title, the four-eyes refusals carry their own detail.
    (N'err.ECR-CALC-0409.authorCannotPublish',  N'en', N'You are the author of version {version}: a second pair of eyes is required, so another user has to publish it.', 1),
    (N'err.ECR-CALC-0409.ownRecalculationApproval', N'en', N'You cannot approve your own recalculation of a closed period: a second pair of eyes is required.', 1),
    -- Round 3 of the localization debt (Methodology authoring): container code
    -- clash, ambiguous constant narrowing, clone source mismatch, version
    -- number/effective-date clashes, draft-only edits, and child ownership.
    (N'err.ECR-CALC-0409.codeTaken',            N'en', N'Methodology code "{code}" is already used by methodology {existingId}.', 1),
    (N'err.ECR-CALC-0409.constantVariantsAmbiguous', N'en', N'Constant "{constantCode}" has {variantCount} narrowed variants in version {methodologyVersionId}: the code alone does not say which one to edit.', 1),
    (N'err.ECR-CALC-0409.versionWrongMethodology', N'en', N'Version {versionId} belongs to methodology {sourceMethodologyId}, not {targetMethodologyId}: it cannot be cloned here.', 1),
    (N'err.ECR-CALC-0409.versionNumberTaken',   N'en', N'Version "{version}" already exists in methodology "{code}".', 1),
    (N'err.ECR-CALC-0409.effectiveDateTaken',   N'en', N'Version "{version}" is already in effect from {effectiveFrom}: two published versions with the same start date make the methodology choice ambiguous.', 1),
    (N'err.ECR-CALC-0409.draftRequired',        N'en', N'Version "{version}" is {status}: {what} cannot be changed. A published version is immutable — changing it means a clone with a new effective window.', 1),
    (N'err.ECR-CALC-0409.formulaWrongVersion',  N'en', N'Formula "{formulaCode}" belongs to version {ownerVersionId}, not {versionId}: it cannot be edited through this version.', 1),
    (N'err.ECR-CALC-0409.childWrongVersion',    N'en', N'{what} "{code}" belongs to version {ownerVersionId}, not {versionId}: it cannot be edited through this version.', 1),
    -- ECR-CALC-4221 has a neutral title: closed period, submitted sheets, approval without a reason.
    (N'err.ECR-CALC-4221.periodClosed',         N'en', N'Period {period} is closed: closed periods are not recalculated automatically, a separate approval is required.', 1),
    (N'err.ECR-CALC-4221.sheetsSubmitted',      N'en', N'Period {period} has submitted sheets: recalculation would change numbers already sent for approval. Reopen the period first.', 1),
    (N'err.ECR-CALC-4221.approvalReasonRequired', N'en', N'An approval to recalculate a closed period is not accepted without a reason.', 1),
    (N'err.ECR-SYS-0500.contactAdmin',         N'en', N'Internal error. Contact your administrator and quote the correlation ID.', 0),
    -- FR-13.9: rule coverage matrix over real rows.
    (N'err.ECR-CALC-0422.coverageWindow',       N'en', N'The period window is empty: periodFrom {periodFrom} is after periodTo {periodTo}.', 1),
    -- ECR-CALC-0422 has a neutral title: every reason carries its own detail.
    (N'err.ECR-CALC-0422.publishNoEffectiveDate', N'en', N'Version {version} cannot be published without an effective date: it is unclear which periods it should calculate.', 1),
    (N'err.ECR-CALC-0422.publishNoReason',      N'en', N'Version {version} cannot be published without a reason for the change.', 1),
    (N'err.ECR-CALC-0422.publishNoGreenTest',   N'en', N'Version {version} cannot be published without a passing test.', 1),
    (N'err.ECR-CALC-0422.publishChecksFailed',  N'en', N'The version failed pre-publication checks ({count} problems).', 1),
    (N'err.ECR-CALC-0422.formulasNotSaved',     N'en', N'Save the formulas of the version before publishing it.', 1),
    (N'err.ECR-CALC-0422.goldenSetEmpty',       N'en', N'Version {version} cannot be published: its golden set has no cases, so nothing was checked.', 1),
    (N'err.ECR-CALC-0422.goldenSetDiverged',    N'en', N'Version {version} cannot be published: {count} value(s) diverged on the golden set (tests: {tests}).', 1),
    (N'err.ECR-CALC-0422.deprecateNotPublished', N'en', N'Version {version} is not published, so there is nothing to withdraw.', 1),
    (N'err.ECR-CALC-0422.versionNotInMethodology', N'en', N'Version {version} does not belong to methodology {code}.', 1),
    (N'err.ECR-CALC-0422.noEffectiveVersion',   N'en', N'The methodology has no version in effect on {date}.', 1),
    (N'err.ECR-CALC-0422.noModule',             N'en', N'No calculation module is available for methodology {code} at level {level}.', 1),
    (N'err.ECR-CALC-0422.runNotFinished',       N'en', N'The calculation run has not finished yet, so it cannot be made current.', 1),
    (N'err.ECR-CALC-0422.constantAmbiguous',    N'en', N'Constant "{code}" has {count} candidates on {date}: the choice is ambiguous.', 1),
    (N'err.ECR-CALC-0422.constantNotNumeric',   N'en', N'Constant "{code}" is declared numeric, but its value is not a number.', 1),
    (N'err.ECR-CALC-0422.constantIsCategoryLabel', N'en', N'Constant "{code}" is a category label and cannot be used in expressions.', 1),
    (N'err.ECR-CALC-0422.constantKindNeedsNumber', N'en', N'Constant "{code}" is numeric and must be given a number, not text.', 1),
    (N'err.ECR-CALC-0422.constantNoText',       N'en', N'Constant "{code}" needs text: an empty string is neither a category label nor a value.', 1),
    (N'err.ECR-CALC-0422.formulaNoExpression',  N'en', N'Formula "{code}" needs an expression: an empty one would silently yield zero.', 1),
    (N'err.ECR-CALC-0422.textFormulaUnit',      N'en', N'Formula "{code}" returns text, so it cannot have a result unit.', 1),
    (N'err.ECR-CALC-0422.ruleNoPredicate',      N'en', N'Rule "{code}" needs a predicate; to match the whole table, use an empty JSON object.', 1),
    -- V-18: правила відбору рядків методології — при збереженні й публікації.
    (N'err.ECR-CALC-0422.ruleMatchInvalid',       N'en', N'The predicate of rule "{code}" is not a flat JSON object of column–value pairs; such a predicate matches no row. To match the whole table, use an empty JSON object.', 1),
    (N'err.ECR-CALC-0422.rulePriorityDuplicate',  N'en', N'Rules {rules} share priority {priority}: which one matches first would depend on storage order. Give each active rule its own priority.', 1),
    (N'err.ECR-CALC-0422.ruleCatchAllNotLast',    N'en', N'Rule "{rule}" (empty predicate — the whole table) has priority {priority}, higher than the specific rules {shadowed}: they would never apply. An empty predicate needs the lowest priority (the largest number).', 1),
    (N'err.ECR-CALC-0422.unknownConstants',       N'en', N'The formulas reference constants that this version does not define ({count}): {constants}. Each of them would evaluate to #REF.', 1),
    (N'err.ECR-CALC-0422.selfDependency',       N'en', N'A methodology cannot depend on itself.', 1),
    (N'err.ECR-CALC-0422.selfImport',           N'en', N'A methodology cannot import itself: its own formulas are already visible.', 1),
    (N'err.ECR-CALC-0432.undeclaredArguments',  N'en', N'The formula expression uses {undeclaredCount} token(s) missing from its declared argument list.', 1),
    (N'err.ECR-CALC-0433.legacyExtensionFunction', N'en', N'The version uses {functionCount} function(s) not available in Legacy mode: switch it to Strict mode from a new effective date.', 1),
    (N'err.ECR-CALC-0438.missingColumns',       N'en', N'A formula argument has no matching column in {tableCount} bound table(s).', 1),
    (N'err.ECR-TMPL-4221.formulaCycle',         N'en', N'The formulas form a dependency cycle: {cyclePath} ({cycleLength} formula(s)).', 1),

    -- ⛔ `RoleAndUserHandlers.cs` (23 кидки, найбільший файл боргу локалізації
    -- на замір 254/74): ролі, користувачі, межі чинності призначення
    -- (ФВ-6.16). Невідомі права/ролі лишаються рядком через кому — самі коди,
    -- а не переклад, як і в `dangerousRoleNeedsConfirmation` вище.
    -- ⚠ `err.ECR-SEC-0404.userNotFound` уже заведений вище (`BE-12`) —
    -- перевикористаний, новий рядок не додається.
    (N'err.ECR-SEC-0404.permissionsUnknown',      N'en', N'Some permissions do not exist in the catalog: {permissions}.', 1),
    (N'err.ECR-SEC-0404.rolesUnknown',            N'en', N'Some roles do not exist: {roles}.', 1),
    (N'err.ECR-REQ-0422.validityRoleNotAssigned', N'en', N'A validity window was given for role "{code}", which is not part of the roles being assigned.', 1),
    (N'err.ECR-USR-0422.windowsSidRequired',      N'en', N'A domain account requires a SID.', 1),
    (N'err.ECR-USR-0422.initialPasswordRequired', N'en', N'A local account requires a one-time password.', 1),

    -- ── ЗАГОЛОВКИ відмов: ключ рівно `err.<код>`, без суфікса ────────────
    --
    -- ⛔ Це рівно та форма ключа, яку читає
    -- `ExceptionHandlingMiddleware.LocalizedTitleAsync`, і ЄДИНА, яку він
    -- читає. Ключа немає — заголовком плашки їде сам код. Замір до цього
    -- запису: 76 кодів у `ErrorCodes.cs` проти 11 заголовків тут, тобто для
    -- 65 кодів `ErrorAlert.tsx` малював першим рядком «ECR-CALC-0437», а
    -- людське речення йшло під ним подробицею. Це майже кожна відмова
    -- продукту. Сторож на розрив — `ErrorTitleCatalogTests`.
    --
    -- ⚠ Заголовок — КОРОТКА називна фраза і НЕ має плейсхолдерів: у момент
    -- резолву заголовка підстановок немає взагалі (`LocalizedTitleAsync`
    -- бере текст як є, без `Format`). Конкретику несе `Detail` — заголовок
    -- її не повторює.
    --
    -- ⚠ Область — приватна скрізь, крім `ECR-SYS-*`: решта кодів
    -- породжується лише на екранах за входом, а 500 і архівація трапляються
    -- і на публічних шляхах (`GET /ui-strings`, `/health`), тобто їхній
    -- заголовок мусить бути в публічному зрізі, як і `err.ECR-AUTH-0401`.
    --
    -- ⚠ `ru`/`kz` тут немає НАВМИСНО (ФВ-14.9): переклади — дані реєстру,
    -- і незаведена мова підміняється мовою за замовчуванням.

    -- Доступ і облікові записи.
    (N'err.ECR-ACCS-0403',  N'en', N'Access denied', 1),
    (N'err.ECR-SEC-0404',   N'en', N'User or role not found', 1),
    (N'err.ECR-USR-0422',   N'en', N'Invalid account data', 1),
    (N'err.ECR-REQ-0422',   N'en', N'Invalid request parameter', 1),
    (N'err.ECR-SIM-0403',   N'en', N'Simulation session is read-only', 1),
    (N'err.ECR-SIM-0422',   N'en', N'Invalid simulation request', 1),

    -- Шаблони і схема.
    (N'err.ECR-TMPL-0404',  N'en', N'Template not found', 1),
    (N'err.ECR-TMPL-0409',  N'en', N'The template version is published', 1),
    (N'err.ECR-TMPL-0422',  N'en', N'The template does not pass validation', 1),
    (N'err.ECR-TMPL-4221',  N'en', N'Cycle in the formula graph', 1),
    (N'err.ECR-TMPL-4222',  N'en', N'Unresolved reference', 1),
    (N'err.ECR-TMPL-4223',  N'en', N'Incompatible units', 1),
    (N'err.ECR-TMPL-4224',  N'en', N'Conflicting validation rules', 1),
    (N'err.ECR-TMPL-4225',  N'en', N'A required column is not covered', 1),
    (N'err.ECR-TMPL-4226',  N'en', N'Computed column without a source', 1),
    (N'err.ECR-TMPL-4227',  N'en', N'Computation on a manual-entry column', 1),
    (N'err.ECR-SCHM-0409',  N'en', N'Breaking change in a version with documents', 1),
    (N'err.ECR-SCHM-0422',  N'en', N'A migration strategy is required', 1),

    -- Документи, рядки, комірки.
    (N'err.ECR-DOC-0404',   N'en', N'Document not found', 1),
    (N'err.ECR-DOC-0409',   N'en', N'The document is submitted', 1),
    (N'err.ECR-DOC-4091',   N'en', N'The sheet is busy', 1),
    (N'err.ECR-DOC-0422',   N'en', N'Invalid document composition', 1),
    (N'err.ECR-ROW-0404',   N'en', N'Row not found', 1),
    (N'err.ECR-ROW-0409',   N'en', N'Row key conflict', 1),
    (N'err.ECR-CELL-0409',  N'en', N'Edit conflict', 1),
    (N'err.ECR-CELL-0422',  N'en', N'Invalid cell value', 1),
    (N'err.ECR-CELL-4221',  N'en', N'The cell is computed', 1),
    (N'err.ECR-CELL-4222',  N'en', N'Value out of range', 1),
    (N'err.ECR-CELL-4223',  N'en', N'Reference to a missing registry entry or unit', 1),
    (N'err.ECR-HDR-0404',   N'en', N'Header field not found', 1),
    (N'err.ECR-HDR-0422',   N'en', N'Invalid header value', 1),
    (N'err.ECR-SUB-4221',   N'en', N'Orphaned rows block submission', 1),

    -- Періоди і проєкти.
    -- Фрази `ECR-PRD-0409` і `ECR-PRD-0422` нейтральні: у обох кодів кілька
    -- причин (перехід стану, архів, пояс; чужий період, порожня причина).
    (N'err.ECR-PRD-0409',   N'en', N'Period state conflict', 1),
    (N'err.ECR-PRD-0404',   N'en', N'Period not found', 1),
    (N'err.ECR-PRD-0422',   N'en', N'Invalid period request', 1),
    (N'err.ECR-PRD-4223',   N'en', N'Reopen is blocked by a closed period', 1),
    (N'err.ECR-PRD-4224',   N'en', N'Invalid period sequence', 1),
    (N'err.ECR-PRD-4225',   N'en', N'Invalid period policy', 1),
    (N'err.ECR-PRD-4091',   N'en', N'The period policy code is taken', 1),
    (N'err.ECR-PRJ-0404',   N'en', N'Project not found', 1),
    (N'err.ECR-PRJ-0422',   N'en', N'The project cannot be activated', 1),
    (N'err.ECR-CFG-4221',   N'en', N'Invalid project time zone', 1),

    -- Реєстри і одиниці.
    -- Фраза `ECR-REG-0404` нейтральна: ним відмовляють і для довідника, запису,
    -- поля, правила. Що саме не знайдено — каже подробиця.
    (N'err.ECR-REG-0404',   N'en', N'Registry item not found', 1),
    -- Фраза `ECR-REG-0422` покриває всі його випадки (опис довідника, набір
    -- перемикання, відкритий період). Який саме — каже подробиця.
    (N'err.ECR-REG-0422',   N'en', N'Invalid registry change', 1),
    (N'err.ECR-UOM-0404',   N'en', N'Unit not found', 1),
    -- Фраза `ECR-UOM-0422` покриває всі його випадки: різні розмірності,
    -- множник ≤ 0 на заведенні й зміні одиниці. Який саме — каже подробиця.
    (N'err.ECR-UOM-0422',   N'en', N'Invalid unit conversion', 1),
    (N'err.ECR-UOM-4221',   N'en', N'Contextual conversion coefficient', 1),
    (N'err.ECR-UOM-4041',   N'en', N'Unit dimension not found', 1),

    -- Розрахунки і методології.
    (N'err.ECR-CALC-0404',  N'en', N'Methodology version not found', 1),
    -- Фраза `ECR-CALC-0409` покриває всі його стани: чотири очі, видалення
    -- версії, зміна не-чернетки, зайнята дата. Який саме — каже подробиця.
    (N'err.ECR-CALC-0409',  N'en', N'Conflicting methodology state', 1),
    (N'err.ECR-CALC-0422',  N'en', N'Invalid methodology request', 1),
    (N'err.ECR-CALC-0431',  N'en', N'Unsupported operator in a formula', 1),
    (N'err.ECR-CALC-0432',  N'en', N'Undeclared formula argument', 1),
    (N'err.ECR-CALC-0433',  N'en', N'Extension function in Legacy mode', 1),
    (N'err.ECR-CALC-0437',  N'en', N'Required methodology inputs are empty', 1),
    (N'err.ECR-CALC-0438',  N'en', N'Formula argument has no matching column', 1),
    -- Фраза `ECR-CALC-4221` покриває всі причини; яку саме — каже подробиця.
    (N'err.ECR-CALC-4221',  N'en', N'Recalculation is not allowed', 1),

    -- Імпорт та інтеграція.
    (N'err.ECR-IMP-0422',   N'en', N'The workbook does not match the template', 1),
    -- ⚠ Заголовок покриває ОБИДВА стани коду: немає сутності джерела і немає
    -- самого мапінгу поля (`BE-27`). Який саме — каже подробиця; заголовок
    -- «Source entity not found» над реченням про мапінг відправляв би людину
    -- шукати не те.
    (N'err.ECR-INT-0404',   N'en', N'Source entity or field mapping not found', 1),
    (N'err.ECR-INT-0405',   N'en', N'Mapping target not found', 1),
    (N'err.ECR-INT-0409',   N'en', N'The mapping is not in that state', 1),
    (N'err.ECR-INT-0422',   N'en', N'The source unit of measure changed', 1),
    (N'err.ECR-INT-0502',   N'en', N'The data source refused authentication', 1),
    (N'err.ECR-INT-0503',   N'en', N'The data source is unavailable', 1),
    -- Фонові задачі. Фраза `ECR-JOB-0409` покриває всі його стани: задача не в
    -- тому стані для дії, перевірка чи тест джерела вже йде, розклад змінено
    -- паралельно або вже є, джерело ще в ужитку. Який саме — каже подробиця.
    (N'err.ECR-JOB-0404',   N'en', N'Background job not found', 1),
    (N'err.ECR-JOB-0409',   N'en', N'Conflicting state', 1),

    -- Звіти.
    (N'err.ECR-RPT-0404',   N'en', N'Report not found', 1),
    (N'err.ECR-RPT-0409',   N'en', N'Already published: a new version is needed', 1),
    (N'err.ECR-RPT-4091',   N'en', N'The report code is taken', 1),
    (N'err.ECR-RPT-0422',   N'en', N'Invalid report definition', 1),

    -- ⚠ Системні. Подробиця для 500 стала й беззмістовна НАВМИСНО (ФВ-6.11),
    -- тому й заголовок тут загальний — але саме тут сирий код найгірший:
    -- людина, яка бачить «ECR-SYS-0500» першим рядком, не має жодної підказки,
    -- що сталося і що робити. Область публічна: 500 і архівація трапляються
    -- і до входу (`GET /ui-strings`, `/health`).
    (N'err.ECR-SYS-0500',   N'en', N'Internal error', 0),
    (N'err.ECR-SYS-0503',   N'en', N'The system is archiving', 0),

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
    -- Пошук даних у шапці (BE-19): кнопка й палітра Ctrl+K.
    (N'search.open',                     N'en', N'Search data', 1),
    (N'search.placeholder',              N'en', N'Documents, templates, registries…', 1),
    (N'search.minLength',                N'en', N'Type at least {min} characters', 1),
    (N'search.empty',                    N'en', N'Nothing found', 1),
    (N'search.rateLimited',              N'en', N'Too many searches — retrying in {seconds}s', 1),
    (N'documents.title',                 N'en', N'Documents', 1),
    (N'documents.key',                   N'en', N'Key', 1),
    (N'documents.project',               N'en', N'Project', 1),
    (N'documents.period',                N'en', N'Period', 1),
    -- Кроки вибору періоду (UI-06). ⚠ Підписи лише для читалки: на кнопках
    -- стрілки ‹ ›, і без aria-label вони були б безіменними. Крок — сусідній
    -- КАЛЕНДАРНИЙ місяць, а не periodKey ± 1 (R-A6): після 202512 йде 202601.
    (N'period.previous',                 N'en', N'Previous period', 1),
    (N'period.next',                     N'en', N'Next period', 1),
    (N'documents.sheets',                N'en', N'Sheets', 1),
    (N'documents.state',                 N'en', N'State', 1),
    (N'documents.empty',                 N'en', N'No documents for this period', 1),
    (N'documents.more',                  N'en', N'Load more', 1),
    -- ⛔ `U-06`: підпис над деревом аркушів. Голий дріб «0 / 91» стояв тут
    -- без жодного слова і читався як «не введено нічого» на документі,
    -- заповненому на ~90 %: у чисельник потрапляють лише таблиці, у яких
    -- закриті ВСІ вхідні комірки, тож таблиця з 710 заповненими з 774 не
    -- рахується зовсім. Текст називає саме це, а не «заповненість» узагалі —
    -- інакше підпис лише замінив би одну двозначність іншою.
    (N'document.tablesFilled',           N'en', N'Tables filled completely: {filled} of {total}', 1),
    (N'document.validate',               N'en', N'Validate', 1),
    (N'document.validationClean',        N'en', N'Validation passed with no errors.', 1),
    (N'document.validationErrors',       N'en', N'Validation found {count} error(s).', 1),

    -- ⛔ Перелік зауважень — НА ЕКРАНІ, а не числом у тості (`ФВ-14.24`,
    -- директива №09 `W8` п.3). Число без переліку не веде до жодної дії:
    -- оператор дізнавався, що щось не так, і не дізнавався ні що саме, ні де.
    (N'document.validationTitle',        N'en', N'Validation findings', 1),
    (N'document.validationCleanHint',    N'en', N'The last run found nothing to fix for this period.', 1),
    (N'document.validationHint',         N'en', N'An error blocks submitting the sheet; a warning does not.', 1),
    (N'document.validationSeverity',     N'en', N'Level', 1),
    (N'document.validationRow',          N'en', N'Row', 1),
    (N'document.validationColumn',       N'en', N'Column', 1),
    (N'document.validationRule',         N'en', N'Rule', 1),
    (N'document.validationMessage',      N'en', N'What is wrong', 1),
    -- Шапка документа (GET/PATCH …/header). Lookup-поле показує НАЗВУ запису
    -- довідника: значення шапки тепер несе `lookupRegistryDefId` (`4f167396`),
    -- тож клієнт резолвить запис тим самим способом, що й комірка сітки.
    -- ⚠ `lookupHint` лишився ФОЛБЕКОМ: поле без `lookupRegistryDefId` нема за
    -- чим резолвити, і тоді воно чесно просить ідентифікатор замість того, щоб
    -- показувати порожній список. `lookupEmpty` — про порожній довідник, а не
    -- про «нічого не знайдено за запитом».
    (N'document.header.title',           N'en', N'Document header', 1),
    (N'document.header.saved',           N'en', N'Header saved.', 1),
    (N'document.header.lookupHint',      N'en', N'Registry entry ID', 1),
    (N'document.header.lookupLoading',   N'en', N'Directory is loading…', 1),
    (N'document.header.lookupEmpty',     N'en', N'Directory has no entries', 1),
    -- Відновлення незбережених правок на екрані документа (ФВ-3.6, D14-12).
    -- ⚠ `partial` називає різницю вголос: у слід вміщається не все, і мовчазне
    -- «відновити N» там, де правок було більше, — та сама тиха втрата.
    -- ⚠ `conflictRow`/`unavailableRow` — поіменно, бо «2 з 3» без переліку не
    -- веде до дії: людина не знає, яку комірку вводити заново.
    (N'document.restoreEdits.title',     N'en', N'Unsaved changes were kept in this browser', 1),
    (N'document.restoreEdits.text',      N'en', N'{count} change(s) never reached the server. Restore them into the sheet, or discard them.', 1),
    (N'document.restoreEdits.partial',   N'en', N'Only {count} of {total} changes were kept; the rest have to be entered again.', 1),
    (N'document.restoreEdits.apply',     N'en', N'Restore changes', 1),
    (N'document.restoreEdits.discard',   N'en', N'Discard', 1),
    (N'document.restoreEdits.applied',   N'en', N'{count} change(s) restored', 1),
    (N'document.restoreEdits.close',     N'en', N'Close', 1),
    (N'document.restoreEdits.conflicts', N'en', N'{count} change(s) were not restored — enter them again:', 1),
    (N'document.restoreEdits.conflictRow', N'en', N'Row {rowKey}, column {columnCode}: the cell changed after your session ended.', 1),
    (N'document.restoreEdits.unavailableRow', N'en', N'Row {rowKey}, column {columnCode}: the cell is no longer in the sheet.', 1),
    (N'document.restoreEdits.more',      N'en', N'and {count} more', 1),
    (N'document.submit',                 N'en', N'Submit', 1),
    (N'document.submitted',              N'en', N'The sheet has been submitted.', 1),
    -- ⚠ Текст без «to Excel»: кнопка експортує у формат, обраний поруч
    -- (ФВ-4.2). Стара фраза оновлюється в секції «Змінені тексти» вище.
    (N'document.export',                 N'en', N'Export', 1),
    (N'document.exportFormat',           N'en', N'Export format', 1),
    (N'document.exportFormatXlsx',       N'en', N'Excel', 1),
    (N'document.exportFormatCsv',        N'en', N'CSV', 1),
    (N'document.exportFormatJson',       N'en', N'JSON', 1),
    (N'document.exportBuilding',         N'en', N'Building...', 1),
    (N'document.exportReady',            N'en', N'Download the workbook', 1),
    -- V-10: підпис посилання — за форматом побудованого файлу.
    (N'document.exportReadyCsv',         N'en', N'Download the CSV archive', 1),
    (N'document.exportReadyJson',        N'en', N'Download the JSON file', 1),
    (N'document.exportFailed',           N'en', N'Export failed.', 1),
    -- Меню рідкісних і небезпечних дій документа (зміна ключа, видалення):
    -- поза рядком щоденних кнопок, праворуч.
    (N'document.moreActions',            N'en', N'More', 1),
    (N'document.noSheets',               N'en', N'This document has no sheets for the selected period.', 1),
    -- Порівняння версій подання документа. ⚠ Перелік змін обрізає сервер, тож
    -- банер каже прямо: за показаним можуть бути ще зміни.
    (N'document.compare',                N'en', N'Compare versions', 1),
    (N'document.compareFrom',            N'en', N'From version', 1),
    (N'document.comparePick',            N'en', N'Pick a version', 1),
    (N'document.compareTo',              N'en', N'To version', 1),
    (N'document.compareCurrent',         N'en', N'Current state', 1),
    (N'document.compareRun',             N'en', N'Compare', 1),
    (N'document.compareNoVersions',      N'en', N'This document has never been submitted for this period.', 1),
    (N'document.compareIdentical',       N'en', N'The two versions are identical: nothing changed.', 1),
    (N'document.compareRowKey',          N'en', N'Row', 1),
    (N'document.compareColumn',          N'en', N'Column', 1),
    (N'document.compareOldValue',        N'en', N'Was', 1),
    (N'document.compareNewValue',        N'en', N'Became', 1),
    (N'document.compareBoolYes',         N'en', N'Yes', 1),
    (N'document.compareBoolNo',          N'en', N'No', 1),
    (N'document.compareAddedTitle',      N'en', N'Rows added', 1),
    (N'document.compareAdded',           N'en', N'new', 1),
    (N'document.compareRemovedTitle',    N'en', N'Rows removed', 1),
    (N'document.compareRemoved',         N'en', N'removed', 1),
    -- Окремий блок змін шапки документа (ФВ-9.4): назву поля беремо з
    -- визначення шапки, а якщо його там немає — показуємо код як є.
    (N'document.compareHeaderTitle',     N'en', N'Header fields', 1),
    (N'document.compareField',           N'en', N'Field', 1),
    (N'document.compareTruncatedTitle',  N'en', N'Not everything is shown', 1),
    (N'document.compareTruncatedHint',   N'en', N'The server stopped at {changes} changed cell(s), {added} added and {removed} removed row(s); more may exist.', 1),
    (N'grid.loading',                    N'en', N'Loading the table...', 1),
    (N'grid.loadFailed',                 N'en', N'The table could not be loaded.', 1),
    (N'grid.undo',                       N'en', N'Undo', 1),
    (N'grid.redo',                       N'en', N'Redo', 1),
    -- ⛔ `U-16`: кнопка називає те, що справді робить. Ключа `grid.save`
    -- («Save ({count})») більше немає: збереження в сітці — автоматичне
    -- (`autosave.ts`), і лічильник у кнопці набирався РІВНО тоді, коли
    -- сервер правку відхилив. Кнопка тепер з'являється лише в цьому випадку
    -- і пропонує саме повтор.
    (N'grid.retrySave',                  N'en', N'Retry save ({count})', 1),
    (N'grid.edit',                       N'en', N'Edit {column}', 1),
    (N'grid.paste',                      N'en', N'Paste {count} cell(s)', 1),
    (N'grid.conflictTitle',              N'en', N'Someone changed these cells', 1),
    (N'grid.conflictHint',               N'en', N'{count} cell(s) were changed by someone else after this table was loaded. Keep your values to overwrite theirs, or discard yours to see theirs.', 1),
    -- ⛔ `BE-06`: перелік, а не саме лише число. До цих рядків сітка показувала
    -- лише лічильник, бо сервер і не мав чого сказати: чиє значення, хто і коли
    -- заповнювалися заглушками, і час чужої правки дорівнював поточному часу
    -- сервера. Рішення «беру їхнє / лишаю своє» ухвалюють саме за цими трьома.
    (N'grid.conflictItem',               N'en', N'Row {row}, column {column}: yours {yours}, theirs {value} — {user}, {time}', 1),
    -- ⚠ Стеля переліку — 100 комірок: решту показує лічильник, бо людина, яка
    -- бачить сто рядків із трьохсот, вважає, що бачить усі.
    (N'grid.conflictMore',               N'en', N'And {count} more changed cell(s) not listed here.', 1),
    -- ⚠ «Невідомо» написано словом: порожнє місце в рядку про автора читалося б
    -- як «ніхто», а це інше твердження.
    (N'grid.conflictUnknownUser',        N'en', N'unknown', 1),
    (N'grid.conflictUnknownTime',        N'en', N'time unknown', 1),
    (N'grid.conflictNoValue',            N'en', N'(no value)', 1),
    (N'grid.rejectedTitle',              N'en', N'Some cells were not saved', 1),
    (N'grid.rejectedHint',               N'en', N'The cells below are read-only for you. Nothing from this paste was saved.', 1),
    (N'grid.unknownColumn',              N'en', N'There is no column {column} in this table.', 1),
    (N'grid.roundedTitle',               N'en', N'Rounded {count} value(s)', 1),
    (N'grid.roundedHint',                N'en', N'Extra decimals from the pasted sheet were rounded to the column scale. Nothing was rounded silently.', 1),
    (N'grid.roundedShow',                N'en', N'Show the list', 1),
    (N'grid.saving',                     N'en', N'Saving...', 1),
    (N'grid.saved',                      N'en', N'Saved', 1),
    -- ⛔ `U-17`: одна відмова — одне повідомлення. Обидва місця (позначка в
    -- рядку кнопок і банер із причиною) показували ОДИН ключ, тобто той
    -- самий текст двічі поруч, і верхній напис відсилав «вище» до того, що
    -- насправді нижче. Тепер позначка — два слова власним ключем, а банер
    -- каже, ЧИЯ це відмова; сама причина стоїть у його тілі.
    (N'grid.saveFailedMark',             N'en', N'Not saved', 1),
    (N'grid.saveError',                  N'en', N'The server rejected this change', 1),
    -- ⛔ `BE-05`: статус-рядок перерахунку. Запис і перерахунок — різні моменти:
    -- комірка вже в базі, а обчислені колонки ще ні, і до появи `jobId` у
    -- відповіді сказати про це було нічим.
    -- ⚠ `{time}` — година й хвилина, коли ЦЕЙ екран побачив завершення:
    -- `JobStatus` позначки часу не несе (`useCellPatch.clockLabel`).
    (N'grid.recalculating',              N'en', N'Recalculating...', 1),
    (N'grid.recalculated',               N'en', N'Recalculated {time}', 1),
    (N'grid.recalcFailed',               N'en', N'Recalculation failed', 1),
    (N'grid.requiredInputBlockedTitle',  N'en', N'Cannot save: {count} required column(s) missing', 1),
    (N'grid.requiredInputWarningTitle',  N'en', N'{count} required column(s) missing (does not block saving)', 1),
    (N'grid.columnRequiredHint',         N'en', N'This column is required.', 1),
    (N'grid.confirmTitle',               N'en', N'Confirm this change', 1),
    (N'grid.confirmCancel',              N'en', N'Cancel', 1),
    (N'grid.confirmProceed',             N'en', N'Proceed', 1),
    -- ⛔ Аудит Етапу 3, лана "Documents core" (`lane3-readonly-cell-after-
    -- submit-not-communicated`): до цього рядка комірка після Submit
    -- показувала штрихування й курсор "not-allowed" (`.ecr-cell--read-only`
    -- уже існував), але без ЖОДНОГО тексту — клацання виглядало як
    -- зависання, не як «сюди не можна саме тому, що аркуш подано».
    (N'grid.submittedReadOnlyHint',      N'en', N'This sheet has been submitted; editing is closed until it is reopened.', 1),

    -- ⛔ Вихід із документа з незбереженими правками (`D14-12` крок 3,
    -- `shared/ui/UnsavedGuard.tsx`). Діалог з'являється РІВНО тоді, коли
    -- збереження при виході не вдалося, — тому і заголовок про факт («зміни не
    -- збережено»), а не питання «ви впевнені?»: питання на кожному переході
    -- навчає відповідати «так» не читаючи, а це рядок, який користувач побачить
    -- один раз за багато днів і мусить прочитати.
    --
    -- ⚠ Безпечна дія названа дією («лишитись на сторінці»), а не «Скасувати»:
    -- що саме скасовується в діалозі, який виник сам, читач не знає.
    (N'unsaved.title',                   N'en', N'Your changes are not saved', 1),
    (N'unsaved.body',                    N'en', N'{count} cell(s) could not be saved. If you leave now, they are lost.', 1),
    (N'unsaved.stay',                    N'en', N'Stay on this page', 1),
    (N'unsaved.leave',                   N'en', N'Leave without saving', 1),

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
    (N'password.policy',                 N'en', N'At least 12 characters.', 1),
    (N'templates.title',                 N'en', N'Templates', 1),
    (N'templates.code',                  N'en', N'Code', 1),
    (N'templates.versions',              N'en', N'Versions', 1),
    (N'version.title',                   N'en', N'Template version', 1),
    (N'version.publish',                 N'en', N'Publish', 1),
    (N'version.publishHint',             N'en', N'After this the structure is frozen: fix a wrong rule now, not after publishing.', 1),
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
    -- V-08: розклад відмови «на запис посилаються» за видами (`referenceKinds`).
    (N'registries.referencedBy',                         N'en', N'Referenced by', 1),
    (N'registries.referenceKind.cells',                  N'en', N'Document cells', 1),
    (N'registries.referenceKind.headerValues',           N'en', N'Document header fields', 1),
    (N'registries.referenceKind.registryValues',         N'en', N'Other registry entries (lookup fields)', 1),
    (N'registries.referenceKind.childEntries',           N'en', N'Child entries', 1),
    (N'registries.referenceKind.links',                  N'en', N'Cascade links', 1),
    (N'registries.referenceKind.methodologyConstants',   N'en', N'Methodology constants', 1),
    (N'registries.referenceKind.methodologySubstances',  N'en', N'Methodology substances', 1),
    (N'registries.hierarchical',         N'en', N'hierarchical', 1),
    (N'registries.temporal',             N'en', N'time-bound', 1),
    -- ⛔ UI-аудит-пас 8, lane4, п.6: таблиця записів довідника була голим
    -- списком без пошуку чи фільтра.
    (N'registries.search',               N'en', N'Search', 1),
    (N'registries.searchPlaceholder',    N'en', N'Filter by code or name', 1),
    (N'registries.searchNoMatches',      N'en', N'No entries match this search.', 1),
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
    -- ⛔ IntelliSense редактора виразів (2026-09-24): пояснення замість порожнього
    -- переліку. Порожнеча після `[` чи `CST.` читалася як «підказок тут немає»,
    -- хоча насправді бракувало обраної версії (`features/expressions/describe.ts`).
    (N'expressions.hint.pickTemplateVersion',        N'en', N'Pick a template version to get column suggestions', 1),
    (N'expressions.hint.pickTemplateVersionHeaders', N'en', N'Pick a template version to get header field suggestions', 1),
    (N'expressions.hint.noHeaders',                  N'en', N'This template version has no header fields', 1),
    (N'expressions.hint.pickMethodologyVersion',     N'en', N'Pick a methodology version to get constant, formula and argument suggestions', 1),
    (N'expressions.hint.noConstants',                N'en', N'This methodology version has no constants', 1),
    (N'expressions.hint.noFormulas',                 N'en', N'This methodology version has no formulas yet', 1),
    (N'expressions.hint.noArguments',                N'en', N'No arguments: the methodology has no active binding to a source table', 1),

    -- Довідка під курсором миші і в переліку (`describe.ts`).
    (N'expressions.doc.column',    N'en', N'Column of table {table}', 1),
    (N'expressions.doc.row',       N'en', N'Row of table {table}', 1),
    (N'expressions.doc.table',     N'en', N'Table on sheet {sheet}', 1),
    (N'expressions.doc.sheet',     N'en', N'Sheet', 1),
    (N'expressions.doc.type',      N'en', N'Type: {type}', 1),
    (N'expressions.doc.unit',      N'en', N'Unit: {unit}', 1),
    (N'expressions.doc.constant',  N'en', N'Methodology constant', 1),
    (N'expressions.doc.formula',   N'en', N'Result of another formula of this version', 1),
    (N'expressions.doc.argument',  N'en', N'Field of the source row', 1),
    (N'expressions.doc.header',    N'en', N'Document header field', 1),

    -- ⚠ Короткий опис функції — ключ за ім'ям у нижньому регістрі: `ROUND`
    -- шаблону і `Round` методології описує той самий рядок. Функцію без рядка
    -- підказка показує лише сигнатурою (`hasText`), тож новій функції сервера
    -- рядок тут не обов'язковий. Семантика — `02b` §7, §8.
    (N'expressions.fn.abs',           N'en', N'Absolute value.', 1),
    (N'expressions.fn.average',       N'en', N'Average of non-empty values; empty set gives null.', 1),
    (N'expressions.fn.convert',       N'en', N'Converts a number from one unit to another — the only way to change a unit.', 1),
    (N'expressions.fn.count',         N'en', N'Number of non-empty values.', 1),
    (N'expressions.fn.if',            N'en', N'Returns the second argument when the condition is true, otherwise the third.', 1),
    (N'expressions.fn.iferror',       N'en', N'Returns the fallback when the value is a calculation error (not when it is empty).', 1),
    (N'expressions.fn.max',           N'en', N'Largest of the values.', 1),
    (N'expressions.fn.min',           N'en', N'Smallest of the values.', 1),
    (N'expressions.fn.product',       N'en', N'Product of non-empty values; empty set gives 1.', 1),
    (N'expressions.fn.regfield',      N'en', N'Field of the registry record a lookup cell points to.', 1),
    (N'expressions.fn.round',         N'en', N'Rounds to the given number of decimal places.', 1),
    (N'expressions.fn.sum',           N'en', N'Sum of the values; empty values are ignored, empty set gives 0.', 1),
    (N'expressions.fn.sumif',         N'en', N'Sum over the rows that satisfy the condition.', 1),
    (N'expressions.fn.acos',          N'en', N'Arc cosine, in radians.', 1),
    (N'expressions.fn.asin',          N'en', N'Arc sine, in radians.', 1),
    (N'expressions.fn.atan',          N'en', N'Arc tangent, in radians.', 1),
    (N'expressions.fn.ceiling',       N'en', N'Smallest integer not less than the value.', 1),
    (N'expressions.fn.cos',           N'en', N'Cosine of an angle in radians.', 1),
    (N'expressions.fn.exp',           N'en', N'e raised to the given power.', 1),
    (N'expressions.fn.floor',         N'en', N'Largest integer not greater than the value.', 1),
    (N'expressions.fn.ieeeremainder', N'en', N'IEEE remainder of a divided by b (not the same as %).', 1),
    (N'expressions.fn.ln',            N'en', N'Natural logarithm.', 1),
    (N'expressions.fn.log',           N'en', N'Logarithm of a to base b.', 1),
    (N'expressions.fn.log10',         N'en', N'Base-10 logarithm.', 1),
    (N'expressions.fn.pow',           N'en', N'a raised to the power b.', 1),
    (N'expressions.fn.sign',          N'en', N'Sign of the value: -1, 0 or 1.', 1),
    (N'expressions.fn.sin',           N'en', N'Sine of an angle in radians.', 1),
    (N'expressions.fn.sqrt',          N'en', N'Square root.', 1),
    (N'expressions.fn.tan',           N'en', N'Tangent of an angle in radians.', 1),
    (N'expressions.fn.truncate',      N'en', N'Integer part of the value.', 1),
    (N'expressions.fn.substance',     N'en', N'Property of the substance being calculated, by code.', 1),
    (N'expressions.fn.ifs',           N'en', N'Value of the first pair whose condition is true.', 1),
    (N'expressions.fn.in',            N'en', N'True when the first argument equals any of the others.', 1),
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
    -- Видалення чернетки версії і порівняння двох версій (BE-25).
    (N'methodologies.deleteVersion',     N'en', N'Delete draft', 1),
    (N'methodologies.deleteVersionTitle', N'en', N'Delete draft version {version}?', 1),
    (N'methodologies.deleteVersionText', N'en', N'The draft is removed together with its formulas, constants, rules and test cases. This cannot be undone.', 1),
    (N'methodologies.versionDeleted',    N'en', N'Draft version {version} has been deleted.', 1),
    (N'methodologies.compare',           N'en', N'Compare', 1),
    (N'methodologies.compareTitle',      N'en', N'Compare version {version}', 1),
    (N'methodologies.compareBase',       N'en', N'Compare with', 1),
    (N'methodologies.compareBaseHint',   N'en', N'By default, the nearest earlier published version.', 1),
    (N'methodologies.compareScope',      N'en', N'Only formulas, constants and test cases are compared.', 1),
    (N'methodologies.compareSame',       N'en', N'The versions are identical in formulas, constants and test cases.', 1),
    (N'methodologies.change',            N'en', N'Change', 1),
    (N'methodologies.changedFields',     N'en', N'Changed fields', 1),
    (N'methodologies.changeAdded',       N'en', N'Added', 1),
    (N'methodologies.changeRemoved',     N'en', N'Removed', 1),
    (N'methodologies.changeChanged',     N'en', N'Changed', 1),
    -- Назви полів changedFields у порівнянні версій — рівно MethodologyDiffFields.All
    -- (стереже MethodologyDiffFieldCatalogTests; клієнт — DiffFieldLabel.tsx).
    (N'methodologyDiffField.expression',   N'en', N'Expression', 1),
    (N'methodologyDiffField.resultType',   N'en', N'Result type', 1),
    (N'methodologyDiffField.outputUnitId', N'en', N'Output unit', 1),
    (N'methodologyDiffField.argumentsCsv', N'en', N'Declared arguments', 1),
    (N'methodologyDiffField.kind',         N'en', N'Value kind', 1),
    (N'methodologyDiffField.value',        N'en', N'Numeric value', 1),
    (N'methodologyDiffField.textValue',    N'en', N'Text value', 1),
    (N'methodologyDiffField.unitId',       N'en', N'Unit', 1),
    (N'methodologyDiffField.validTo',      N'en', N'Valid to', 1),
    (N'methodologyDiffField.source',       N'en', N'Source reference', 1),
    (N'methodologyDiffField.inputJson',    N'en', N'Test inputs', 1),
    (N'methodologyDiffField.expectedJson', N'en', N'Expected outputs', 1),
    (N'methodologyDiffField.tolerance',    N'en', N'Tolerance', 1),
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
    (N'methodologies.deleteFormulaConfirmTitle', N'en', N'Remove formula', 1),
    (N'methodologies.deleteFormulaConfirmText', N'en', N'Remove formula {code} from this draft? This cannot be undone.', 1),
    (N'methodologies.saveFormula',       N'en', N'Save formula', 1),
    (N'methodologies.formulaSaved',      N'en', N'The formula has been saved.', 1),
    (N'methodologies.formulaDeleted',    N'en', N'The formula has been removed.', 1),
    (N'methodologies.noFormulas',        N'en', N'This version has no formulas', 1),
    (N'methodologies.noFormulasHint',    N'en', N'Add one, or copy the version from an existing one.', 1),
    (N'methodologies.newMethodology', N'en', N'New methodology', 1),
    (N'methodologies.newMethodologyTitle', N'en', N'New methodology', 1),
    (N'methodologies.create', N'en', N'Create methodology', 1),
    (N'methodologies.created', N'en', N'The methodology has been created. It has no versions yet.', 1),
    (N'methodologies.name', N'en', N'Name', 1),
    (N'methodologies.methodologyCodeHint', N'en', N'What imports and bindings refer to. Latin letters, digits and underscores; it cannot be renamed.', 1),
    (N'methodologies.kind', N'en', N'Kind', 1),
    (N'methodologies.kindHint', N'en', N'Not descriptive: a Bespoke module has no binding rule at all, and a Library computes for nobody - its formulas are referenced.', 1),
    (N'methodologies.group', N'en', N'Group', 1),
    (N'methodologies.groupHint', N'en', N'Grouping in the list only; empty means no group.', 1),
    (N'methodologies.save', N'en', N'Save', 1),
    (N'methodologies.level', N'en', N'Expressiveness level', 1),
    (N'methodologies.levelHint', N'en', N'Matters for an EMPTY draft only: a clone keeps the level of its source, because a version that changed level is a different methodology.', 1),
    (N'methodologies.arguments', N'en', N'Declared arguments', 1),
    (N'methodologies.argumentsHint', N'en', N'A ;-separated list, and the source of truth about arguments. A token missing from it never reaches the expression: the formula computes with an undefined parameter and returns a plausible number. Empty means no list is declared.', 1),
    (N'methodologies.diffTitle', N'en', N'What the publication changed in the numbers', 1),
    (N'methodologies.diffNumeric', N'en', N'Arithmetic', 1),
    (N'methodologies.diffCalendar', N'en', N'Calendar convention', 1),
    (N'methodologies.diffChanges', N'en', N'Differences on the golden set', 1),
    (N'methodologies.diffNone', N'en', N'No number changed.', 1),
    (N'methodologies.warnings', N'en', N'Publication warnings', 1),
    (N'methodologies.testCode', N'en', N'Test', 1),
    (N'methodologies.before', N'en', N'Before', 1),
    (N'methodologies.after', N'en', N'After', 1),
    (N'methodologies.constants', N'en', N'Constants', 1),
    (N'methodologies.addConstant', N'en', N'Add constant', 1),
    (N'methodologies.constantSaved', N'en', N'The constant has been saved.', 1),
    (N'methodologies.constantKind', N'en', N'Kind', 1),
    (N'methodologies.constantKindHint', N'en', N'A constant is not always a number: about ninety non-numeric ones are used in expressions as comparison operands.', 1),
    (N'methodologies.constantText', N'en', N'Text', 1),
    (N'methodologies.constantUnitHint', N'en', N'Mandatory for a number: the dimension check at publication rests on it.', 1),
    (N'methodologies.validFrom', N'en', N'Valid from', 1),
    (N'methodologies.validTo', N'en', N'Valid to', 1),
    (N'methodologies.validToHint', N'en', N'The first day it is NO LONGER valid: a coefficient valid for the whole of 2024 has 2025-01-01 here.', 1),
    (N'methodologies.category', N'en', N'Category', 1),
    (N'methodologies.categoryHint', N'en', N'Narrows where the constant applies: default, offshore, a plant name. Empty means it is shared.', 1),
    (N'methodologies.sourceRef', N'en', N'Source of the value', 1),
    (N'methodologies.sourceRefHint', N'en', N'An order, a plant certificate, a measurement: an emission factor without a source can be neither defended nor updated.', 1),
    (N'methodologies.unresolved', N'en', N'not a number', 1),
    (N'methodologies.categoryLabel', N'en', N'Category label', 1),
    (N'methodologies.noConstants', N'en', N'This version has no constants', 1),
    (N'methodologies.noConstantsHint', N'en', N'A formula without its coefficients computes over emptiness, and publishing does not stop that.', 1),
    (N'methodologies.rules', N'en', N'Row selection rules', 1),
    (N'methodologies.addRule', N'en', N'Add rule', 1),
    (N'methodologies.ruleSaved', N'en', N'The rule has been saved.', 1),
    (N'methodologies.matchJson', N'en', N'Predicate', 1),
    (N'methodologies.matchJsonHint', N'en', N'A structured predicate, not an expression. An empty object matches the whole table - which is why such a rule must have the lowest priority.', 1),
    (N'methodologies.priority', N'en', N'Priority', 1),
    (N'methodologies.priorityHint', N'en', N'The lower the number, the higher the priority. The first match wins.', 1),
    (N'methodologies.catchAllNotLowestTitle', N'en', N'This catch-all rule is not at the lowest priority', 1),
    (N'methodologies.catchAllNotLowestWarning', N'en', N'An empty predicate matches every row. Give it the highest priority number here, or any rule with a lower number will never be reached.', 1),
    (N'methodologies.shadowedByCatchAllTitle', N'en', N'This rule can never match', 1),
    (N'methodologies.shadowedByCatchAllWarning', N'en', N'Rule {code} already matches every row at a higher priority. Rows never reach this rule.', 1),
    (N'methodologies.active', N'en', N'Active', 1),
    (N'methodologies.noRules', N'en', N'This version has no selection rules', 1),
    (N'methodologies.noRulesHint', N'en', N'Without a rule the methodology touches no document row, and recalculation succeeds having computed nothing.', 1),
    -- Панель покриття правил (ФВ-13.9): які рядки реальних даних бере на себе
    -- кожне правило. ⚠ Перелік обрізає сервер, тож банер каже це прямо:
    -- прогалина може лежати поза показаними сполученнями.
    (N'methodologies.ruleCoverage', N'en', N'Rule coverage', 1),
    (N'methodologies.ruleCoverageHint', N'en', N'Which rows of real data each rule claims.', 1),
    (N'methodologies.ruleCoverageTable', N'en', N'Table', 1),
    (N'methodologies.ruleCoverageAllTables', N'en', N'All tables', 1),
    (N'methodologies.ruleCoverageTableOption', N'en', N'Table {id}', 1),
    (N'methodologies.ruleCoveragePeriodFrom', N'en', N'Period from', 1),
    (N'methodologies.ruleCoveragePeriodTo', N'en', N'Period to', 1),
    (N'methodologies.ruleCoveragePeriodHint', N'en', N'Period key as YYYYMM, for example 202601.', 1),
    (N'methodologies.ruleCoverageValues', N'en', N'Values', 1),
    (N'methodologies.ruleCoverageState', N'en', N'State', 1),
    (N'methodologies.ruleCoverageRules', N'en', N'Rules', 1),
    (N'methodologies.ruleCoverageRows', N'en', N'Rows', 1),
    (N'methodologies.ruleCoverageDocuments', N'en', N'Documents', 1),
    (N'methodologies.ruleCoverageCovered', N'en', N'Covered', 1),
    (N'methodologies.ruleCoverageGap', N'en', N'No rule', 1),
    (N'methodologies.ruleCoverageConflict', N'en', N'Two rules, same priority', 1),
    (N'methodologies.ruleCoverageShadowed', N'en', N'shadowed by priority', 1),
    (N'methodologies.ruleCoverageNoCell', N'en', N'no cell', 1),
    (N'methodologies.ruleCoverageTruncatedTitle', N'en', N'Not everything is shown', 1),
    (N'methodologies.ruleCoverageTruncatedHint', N'en', N'The server stopped at {shown} combinations; a gap may be outside them.', 1),
    (N'methodologies.noRuleCoverage', N'en', N'No rows match this window', 1),
    (N'methodologies.noRuleCoverageHint', N'en', N'Widen the period window or pick another table.', 1),
    (N'methodologies.requiredInputs', N'en', N'Required input columns', 1),
    (N'methodologies.addRequiredInput', N'en', N'Add required input', 1),
    (N'methodologies.requiredInputSaved', N'en', N'The required input has been saved.', 1),
    (N'methodologies.requiredInputColumnHint', N'en', N'The column whose emptiness blocks or warns on save. The table is derived from the methodology binding.', 1),
    (N'methodologies.severity', N'en', N'Severity', 1),
    (N'methodologies.severityHint', N'en', N'Whether an unfilled column blocks saving the row, or only warns while the save goes through.', 1),
    (N'methodologies.severityBlock', N'en', N'Block', 1),
    (N'methodologies.severityWarn', N'en', N'Warn', 1),
    (N'methodologies.hint', N'en', N'Hint', 1),
    (N'methodologies.hintHint', N'en', N'Text shown instead of the default template; leave empty to use it.', 1),
    (N'methodologies.noRequiredInputs', N'en', N'This version has no required input columns', 1),
    (N'methodologies.noRequiredInputsHint', N'en', N'Without a required input, a row can be saved even though the methodology has nothing meaningful to compute from it.', 1),
    -- ⛔ UI-аудит, lane 5 (Q-337): деактивована прив'язка лишала вимогу без
    -- ЖОДНОГО натяку, що вона зависла — рядок і далі показував "Column: X,
    -- Severity: Block", наче все гаразд, і далі блокував збереження даних
    -- для колонки, яку методологія вже не пише.
    (N'methodologies.requiredInputUnattached',     N'en', N'unattached', 1),
    (N'methodologies.requiredInputUnattachedHint', N'en', N'This column has no active binding for this methodology right now: the requirement still blocks or warns on save, but nothing writes a result into it. Reactivate the binding, or remove this requirement.', 1),
    (N'methodologies.addOutput', N'en', N'Add output', 1),
    (N'methodologies.outputSaved', N'en', N'The output has been saved.', 1),
    (N'methodologies.outputCodeHint', N'en', N'The address the binding points at; it matches the code of the formula that produces it.', 1),
    (N'methodologies.outputUnitRequiredHint', N'en', N'Mandatory: the dimension check at publication rests on it.', 1),
    (N'methodologies.ordinal', N'en', N'Order', 1),
    (N'methodologies.noOutputs', N'en', N'This version declares no outputs', 1),
    (N'methodologies.noOutputsHint', N'en', N'Without an output the module computes every formula and writes nothing: the write loop goes over outputs.', 1),
    -- Покриття «виходи → колонки» (BE-25): куди пише кожен оголошений вихід і
    -- які прив'язки чекають на вихід, якого ця версія не оголошує.
    (N'methodologies.outputCoverage', N'en', N'Output coverage', 1),
    (N'methodologies.outputCoverageHint', N'en', N'Where each declared output of this version writes, and which columns wait for an output this version does not declare.', 1),
    (N'methodologies.outputCoverageBindings', N'en', N'Bindings', 1),
    (N'methodologies.outputCoverageNowhere', N'en', N'Writes nowhere', 1),
    (N'methodologies.noOutputCoverage', N'en', N'No coverage to show', 1),
    (N'methodologies.noOutputCoverageHint', N'en', N'This version has no declared outputs and no bindings are waiting on it.', 1),
    (N'methodologies.outputCoverageWaiting', N'en', N'Waiting bindings', 1),
    (N'methodologies.outputCoverageWaitingHint', N'en', N'These bindings are active but point at an output this version does not declare; they will stay empty.', 1),
    (N'methodologies.tests', N'en', N'Golden set', 1),
    (N'methodologies.addTest', N'en', N'Add test', 1),
    (N'methodologies.testSaved', N'en', N'The test has been saved.', 1),
    (N'methodologies.inputJson', N'en', N'Input', 1),
    (N'methodologies.inputJsonHint', N'en', N'The run input as CalculationInput: a real document, table instance and period - the module takes the period length from doc.Period.', 1),
    (N'methodologies.expectedJson', N'en', N'Expected outputs', 1),
    (N'methodologies.expectedJsonHint', N'en', N'Output code to number. An empty object means nothing is expected to be produced.', 1),
    (N'methodologies.tolerance', N'en', N'Tolerance', 1),
    (N'methodologies.toleranceHint', N'en', N'Zero means exact equality; a tolerance keeps the test from turning red when the order of terms changes.', 1),
    (N'methodologies.noTests', N'en', N'This version has no tests', 1),
    (N'methodologies.noTestsHint', N'en', N'An empty set is not a green one: a version without tests is never published.', 1),
    (N'methodologies.bindings', N'en', N'Bindings to document columns', 1),
    (N'methodologies.addBinding', N'en', N'Add binding', 1),
    (N'methodologies.bindingSaved', N'en', N'The binding has been saved.', 1),
    (N'methodologies.noBindings', N'en', N'This methodology is bound to no column', 1),
    (N'methodologies.noBindingsHint', N'en', N'Without a binding recalculation succeeds and computes nothing: an empty set of bindings is not an error.', 1),
    (N'methodologies.tableDefId', N'en', N'Table', 1),
    (N'methodologies.columnDefId', N'en', N'Column', 1),
    (N'methodologies.columnDefIdHint', N'en', N'The receiving column. The table is derived from it: two fields about the same thing drift apart silently.', 1),
    (N'methodologies.outputCode', N'en', N'Output', 1),
    (N'methodologies.outputCodeBindingHint', N'en', N'Which output of the methodology lands in the column.', 1),
    (N'methodologies.bindingMatchHint', N'en', N'How to narrow the rows of the table; an empty object means all of them.', 1),
    (N'methodologies.modesSaved', N'en', N'The modes have been saved.', 1),
    (N'methodologies.modesHint', N'en', N'Both the arithmetic and the calendar convention silently change EVERY number of the version without changing a single formula - which is why they are mandatory in the publication diff.', 1),
    (N'methodologies.numericMode', N'en', N'Arithmetic', 1),
    (N'methodologies.calendarMode', N'en', N'Calendar convention', 1),
    (N'methodologies.traceLevel', N'en', N'Trace level', 1),
    (N'methodologies.saveModes', N'en', N'Save modes', 1),
    (N'documents.calculationResults', N'en', N'Calculation results', 1),
    (N'documents.noCalculationResults', N'en', N'No numbers for this period', 1),
    (N'documents.noCalculationResultsHint', N'en', N'Either the document has not been recalculated yet, or no methodology is bound to its columns.', 1),
    (N'documents.rowKey', N'en', N'Row', 1),
    (N'documents.outputCode', N'en', N'Output', 1),
    (N'documents.value', N'en', N'Value', 1),
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
    (N'grants.resourceName',             N'en', N'Resolved name', 1),
    (N'grants.resourceNameUnknown',      N'en', N'Not found — resource deleted or the id is wrong', 1),
    (N'grants.level',                    N'en', N'Level', 1),
    (N'grants.deny',                     N'en', N'Deny', 1),
    (N'grants.remove',                   N'en', N'Remove', 1),
    (N'security.role',                   N'en', N'Role', 1),
    (N'security.name',                   N'en', N'Name', 1),
    (N'security.login',                  N'en', N'Login', 1),
    (N'security.kind',                   N'en', N'Kind', 1),
    (N'security.userState',              N'en', N'State', 1),
    (N'security.inactive',               N'en', N'inactive', 1),
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
    -- ⛔ Q-337, lane 2 UI-аудиту: сторінка показувала «Range»/«Grace until»
    -- без жодного пояснення, а мітка політики `+15/45` (форма створення
    -- проєкту) показує лише два з чотирьох чисел і не на цій сторінці.
    -- Тултипи нижче пояснюють похідну формулу в термінах РЕАЛЬНИХ чисел
    -- політики проєкту (`{open}`/`{grace}`/`{hardClose}`/`{code}` —
    -- підставляються з `PeriodCalendarDto.Policy`, а не вигадані).
    (N'periods.rangeHint',               N'en', N'Start = period start + Open offset ({open} d). End = period end + Hard-close offset ({hardClose} d, policy {code}): the period is fully Closed after this moment, and even late edits are no longer accepted.', 1),
    (N'periods.state',                   N'en', N'State', 1),
    (N'periods.grace',                   N'en', N'Grace until', 1),
    (N'periods.graceHint',               N'en', N'The moment the period leaves Open and enters Grace (still writable, but edits are flagged as late): period end + Grace offset ({grace} d, policy {code}). It stays in Grace until the end of Range (Hard-close, +{hardClose} d).', 1),
    (N'periods.activate',                N'en', N'Activate project', 1),
    (N'periods.activated',               N'en', N'The project is active: periods now follow their dates.', 1),
    (N'periods.draftHint',               N'en', N'The project is a draft: periods stay closed until it is activated.', 1),
    (N'sources.title',                   N'en', N'Sources', 1),
    (N'sources.entity',                  N'en', N'Entity', 1),
    (N'sources.transport',               N'en', N'Transport', 1),
    (N'sources.lastRun',                 N'en', N'Last run', 1),
    (N'sources.gap',                     N'en', N'Gaps', 1),
    (N'sources.never',                   N'en', N'never', 1),
    (N'sources.inactive',                N'en', N'Inactive', 1),
    (N'sources.collect',                 N'en', N'Collect', 1),
    (N'sources.queued',                  N'en', N'Collection queued as job {job}.', 1),
    -- Розклад збору сутності джерела (ФВ-14.3). Формат — Quartz: 6–7 полів,
    -- першим ідуть секунди, рівно одне з двох полів дня — `?`. Причини
    -- `schedule.cron*` ставить `cronFormat.ts` через `t(problem.key, …)`.
    (N'schedule.none',                   N'en', N'No collection schedule yet.', 1),
    (N'schedule.cron',                   N'en', N'Schedule (cron)', 1),
    (N'schedule.cronHint',               N'en', N'Quartz format: seconds minutes hours day-of-month month day-of-week [year]. Exactly one of the two day fields must be ?.', 1),
    (N'schedule.enabled',                N'en', N'Enabled', 1),
    (N'schedule.lastRun',                N'en', N'Last collection:', 1),
    (N'schedule.notApplied',             N'en', N'The scheduler did not apply this schedule', 1),
    (N'schedule.create',                 N'en', N'Create schedule', 1),
    (N'schedule.removeConfirm',          N'en', N'Remove this schedule? Collection will no longer run automatically.', 1),
    (N'schedule.reload',                 N'en', N'Reload the current version', 1),
    (N'schedule.saved',                  N'en', N'Schedule saved.', 1),
    (N'schedule.removed',                N'en', N'Schedule removed.', 1),
    (N'schedule.cronEmpty',              N'en', N'Enter a cron expression.', 1),
    (N'schedule.cronTooLong',            N'en', N'The expression is longer than {max} characters.', 1),
    (N'schedule.cronFieldCount',         N'en', N'Expected 6 or 7 fields separated by spaces, got {count}.', 1),
    (N'schedule.cronField',              N'en', N'Field {position} is not valid: {value}', 1),
    (N'schedule.cronDayQuestion',        N'en', N'Exactly one of day-of-month and day-of-week must be ?.', 1),
    (N'jobs.title',                      N'en', N'Jobs', 1),
    (N'jobs.id',                         N'en', N'Job id', 1),
    (N'jobs.watch',                      N'en', N'Watch', 1),
    (N'jobs.recentEmpty',                N'en', N'No jobs yet', 1),
    -- V-10: результат задачі буває книгою, ZIP-архівом CSV і JSON.
    (N'jobs.resultDownload',             N'en', N'Download the file', 1),
    -- Шухляда «My tasks» у шапці (BE-08): власні фонові задачі, усім ролям.
    (N'jobs.myTasks',                    N'en', N'My tasks', 1),
    (N'jobs.myTasksClose',               N'en', N'Close my tasks', 1),
    (N'jobs.myTasksActive',              N'en', N'{n} running or queued', 1),
    (N'jobs.myTasksHint',                N'en', N'Long operations you start appear here. They keep running on the server, so you can close this tab and come back for the result.', 1),
    -- Факти фонової задачі (BE-08): спроба, причина провалу, кореляція,
    -- документ, автор. Причину провалу дає `err.<errorCode>` із каталогу помилок.
    (N'jobs.attempt',                    N'en', N'Attempt {n}', 1),
    (N'jobs.attemptOf',                  N'en', N'Attempt {n} of {max}', 1),
    (N'jobs.failureUnrecorded',          N'en', N'Reason not recorded', 1),
    (N'jobs.copyCorrelation',            N'en', N'Copy correlation id', 1),
    (N'jobs.openDocument',               N'en', N'Document {id}', 1),
    (N'jobs.system',                     N'en', N'System', 1),
    (N'jobs.createdBy',                  N'en', N'Queued by', 1),
    (N'jobs.createdAt',                  N'en', N'Queued at', 1),
    (N'jobs.recentMessage',              N'en', N'Message', 1),
    (N'jobs.recentCode',                 N'en', N'Job', 1),
    (N'jobs.recentState',                N'en', N'State', 1),
    (N'jobs.recentWatch',                N'en', N'Watch', 1),
    -- ⛔ Аудит-пас 8, lane6, п.8: людські назви типів фонової задачі —
    -- `jobLabel.ts` мапує на них СИРІ .NET-імена (`jobCode`/`jobId`) лише для
    -- показу; сам ідентифікатор у сховищі й API не змінюється.
    (N'jobs.kind.recalculation',            N'en', N'Recalculation', 1),
    (N'jobs.kind.formulaRecalculation',      N'en', N'Formula recalculation', 1),
    (N'jobs.kind.excelExport',               N'en', N'Excel export', 1),
    (N'jobs.kind.excelImport',               N'en', N'Excel import', 1),
    (N'jobs.kind.materializeCollectedData',  N'en', N'Materializing collected data', 1),
    (N'jobs.kind.reportSnapshot',            N'en', N'Report snapshot build', 1),
    (N'jobs.kind.collection',                N'en', N'Data collection', 1),
    (N'health.title',                    N'en', N'Health', 1),
    (N'health.database',                 N'en', N'Database', 1),
    -- ⛔ Аудит-пас 8, lane6, п.7: людські підписи для дев'яти технічних полів
    -- `/health/db` (`DatabaseHealthCheck.cs`) — до фіксу панель показувала
    -- буквально `edition`, `effectiveMode`, `rcsi` тощо.
    (N'health.database.edition',          N'en', N'SQL Server edition', 1),
    (N'health.database.effectiveMode',    N'en', N'Effective mode', 1),
    (N'health.database.majorVersion',     N'en', N'Major version', 1),
    (N'health.database.rcsi',             N'en', N'Read Committed Snapshot Isolation (RCSI)', 1),
    (N'health.database.archiveBatchSize', N'en', N'Archive batch size', 1),
    (N'health.database.filegroups',       N'en', N'Filegroups', 1),
    (N'health.database.missingFilegroups', N'en', N'Missing filegroups', 1),
    (N'health.database.partitionsAhead',  N'en', N'Partitions ahead', 1),
    (N'health.database.limitations',      N'en', N'Limitations in this mode', 1),
    (N'documents.emptyHint',             N'en', N'Documents appear once the period is open and a template version is published.', 1),
    -- Фільтри переліку документів (BE-09b): стан — лише в межах періоду, «мої»
    -- — створені або подані мною. `noMatch*` — порожньо ЧЕРЕЗ фільтри, а не
    -- тому, що документів немає (`documents.empty*`).
    (N'documents.stateAll',              N'en', N'All states', 1),
    (N'documents.stateNeedsPeriod',      N'en', N'Choose a period first: a document''s state is defined only within a period.', 1),
    (N'documents.filterMine',            N'en', N'Mine — created or submitted by me', 1),
    (N'documents.filterLateEdits',       N'en', N'Has late edits', 1),
    (N'documents.lateEdits',             N'en', N'Late edits', 1),
    -- ⚠ Без «for this period»: без вибраного періоду ознака рахується за будь-який.
    (N'documents.lateEditsHint',         N'en', N'The document was edited after the submission deadline.', 1),
    (N'documents.noMatch',               N'en', N'No documents match the filters.', 1),
    (N'documents.noMatchHint',           N'en', N'Change the state or turn off "Mine".', 1),
    (N'documents.resetFilters',          N'en', N'Reset filters', 1),
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
    -- З'єднання з джерелами на `/admin/sources` (UI-09): перелік, шухляда
    -- Connection і проба «Test connection».
    (N'sources.connections',             N'en', N'Connections', 1),
    (N'sources.connection',              N'en', N'Connection', 1),
    (N'sources.state',                   N'en', N'State', 1),
    (N'sources.active',                  N'en', N'Active', 1),
    (N'sources.entities',                N'en', N'Entities', 1),
    (N'sources.schedules',               N'en', N'Schedules', 1),
    -- Журнал прогонів збору (ФВ-5.23), секція на /admin/sources. ⚠ Покриття
    -- показується інтервалами: банер усічення стоїть ПЕРЕД переліком, щоб
    -- людина не рахувала неповні інтервали як повні.
    (N'collectionRuns.title',            N'en', N'Collection runs', 1),
    (N'collectionRuns.entity',           N'en', N'Entity', 1),
    (N'collectionRuns.state',            N'en', N'State', 1),
    (N'collectionRuns.stateRunning',     N'en', N'Running', 1),
    (N'collectionRuns.stateSucceeded',   N'en', N'Succeeded', 1),
    (N'collectionRuns.stateDegraded',    N'en', N'Degraded', 1),
    (N'collectionRuns.stateFailed',      N'en', N'Failed', 1),
    (N'collectionRuns.hasError',         N'en', N'with an error', 1),
    (N'collectionRuns.range',            N'en', N'Period', 1),
    (N'collectionRuns.catchUp',          N'en', N'catch-up', 1),
    (N'collectionRuns.duration',         N'en', N'Duration', 1),
    (N'collectionRuns.durationSeconds',  N'en', N'{value} s', 1),
    (N'collectionRuns.points',           N'en', N'Points', 1),
    (N'collectionRuns.triggeredBy',      N'en', N'Started by', 1),
    (N'collectionRuns.system',           N'en', N'schedule', 1),
    (N'collectionRuns.filterDataSource', N'en', N'Connection', 1),
    (N'collectionRuns.filterEntity',     N'en', N'Entity', 1),
    (N'collectionRuns.filterState',      N'en', N'State', 1),
    (N'collectionRuns.filterFrom',       N'en', N'From', 1),
    (N'collectionRuns.filterTo',         N'en', N'To', 1),
    (N'collectionRuns.more',             N'en', N'Show more', 1),
    (N'collectionRuns.empty',            N'en', N'No collection runs', 1),
    (N'collectionRuns.emptyHint',        N'en', N'Nothing has been collected in this window: no schedule has fired and nobody has started a collection by hand.', 1),
    (N'collectionRuns.closeDetails',     N'en', N'Close run details', 1),
    (N'collectionRuns.error',            N'en', N'What went wrong', 1),
    (N'collectionRuns.coverage',         N'en', N'Covered intervals', 1),
    (N'collectionRuns.coverageEmpty',    N'en', N'The run covered no interval: nothing was collected.', 1),
    (N'collectionRuns.coverageTruncated', N'en', N'Only part of the intervals is shown; the run covered more.', 1),
    (N'sources.connectionsEmpty',        N'en', N'No connections configured', 1),
    (N'sources.connectionsEmptyHint',    N'en', N'A connection says where data is collected from; entities and schedules are attached to it.', 1),
    (N'sources.closeDetails',            N'en', N'Close connection details', 1),
    (N'sources.endpoint',                N'en', N'Endpoint', 1),
    (N'sources.secondaryEndpoint',       N'en', N'Secondary endpoint', 1),
    (N'sources.catalog',                 N'en', N'Catalog', 1),
    (N'sources.maxParallel',             N'en', N'Max parallel requests', 1),
    (N'sources.hasSecret',               N'en', N'Secret', 1),
    (N'sources.hasSecretYes',            N'en', N'Stored', 1),
    -- ⚠ Проба йде в журнал безпеки: сервер звертається до чужої системи від
    -- імені службового запису, тому причина обов'язкова.
    (N'sources.testConnection',          N'en', N'Test connection', 1),
    (N'sources.testTitle',               N'en', N'Test connection: {name}', 1),
    (N'sources.testReason',              N'en', N'Reason', 1),
    (N'sources.testReasonHint',          N'en', N'Required: the test is written to the security log.', 1),
    (N'sources.testOk',                  N'en', N'The source answered', 1),
    (N'sources.testFailed',              N'en', N'The source refused the connection', 1),
    (N'sources.testRunning',             N'en', N'A test of this connection is already running', 1),
    -- ⚠ Перші ключі-множини в сіді: `formatCount` бере `<основа>.<категорія>`
    -- за `Intl.PluralRules` (для en — `one` і `other`) і підставляє `{count}`.
    -- `EndpointCoverageTests` дворівневих ключів не бачить; сторож тут —
    -- тест «технічні ключі на екрані» в `npm run test:a11y`.
    (N'sources.testEntities.one',        N'en', N'The source catalog lists {count} entity.', 1),
    (N'sources.testEntities.other',      N'en', N'The source catalog lists {count} entities.', 1),
    -- Створення, правка й видалення з'єднань (UI-09 крок 2). ⚠ `created` і
    -- `saved` ідуть через `t(умова ? … : …)` — сторож `EndpointCoverageTests`
    -- їх не бачить, тож єдина гарантія — цей рядок.
    (N'sources.newConnection',           N'en', N'New connection', 1),
    (N'sources.editConnection',          N'en', N'Edit', 1),
    (N'sources.editTitle',               N'en', N'Edit connection: {name}', 1),
    (N'sources.code',                    N'en', N'Code', 1),
    (N'sources.codeFixed',               N'en', N'The code cannot be changed: collection entities refer to it', 1),
    (N'sources.name',                    N'en', N'Name', 1),
    (N'sources.isActive',                N'en', N'Collect from this connection', 1),
    (N'sources.create',                  N'en', N'Create connection', 1),
    (N'sources.created',                 N'en', N'Connection created', 1),
    (N'sources.saved',                   N'en', N'Connection saved', 1),
    -- ⚠ Кнопка на 409 від If-Match: з'єднання змінив хтось інший, і форма бере
    -- свіжу версію, а не перезаписує чужу правку.
    (N'sources.reloadCurrent',           N'en', N'Reload the current version', 1),
    (N'sources.deleteConnection',        N'en', N'Delete connection', 1),
    (N'sources.deleteTitle',             N'en', N'Delete connection "{name}"?', 1),
    (N'sources.deleted',                 N'en', N'Connection deleted', 1),
    -- Вкладка Schedule у шухляді з'єднання: розклади за `?dataSource=`.
    (N'sources.tabSchedule',             N'en', N'Schedule', 1),
    (N'sources.schedulesNone',           N'en', N'No collection schedules for this connection', 1),
    (N'sources.scheduleEntity',          N'en', N'Entity', 1),
    (N'sources.scheduleNoEntities',      N'en', N'This connection has no collection entities yet', 1),
    (N'sources.scheduleOff',             N'en', N'Off', 1),
    (N'jobs.pick',                       N'en', N'Enter a job id', 1),
    (N'jobs.pickHint',                   N'en', N'Long operations return a job id; paste it here to follow the progress.', 1),
    (N'jobs.restart',                    N'en', N'Restart', 1),
    (N'jobs.restarting',                 N'en', N'Restarting…', 1),
    -- ⚠ Скасування — ПРОХАННЯ, не вбивство: підтвердження має сказати це
    -- прямо, інакше «Cancel» читається як «нічого не сталося», а задача ще
    -- дописує поточний батч (`CancelJobHandler`, відповідь `202`).
    (N'jobs.cancel',                     N'en', N'Cancel job', 1),
    (N'jobs.cancelConfirm',              N'en', N'The job is asked to stop and finishes in the Cancelled state at the nearest batch boundary. Work already written is kept.', 1),
    (N'jobs.cancelling',                 N'en', N'Cancelling…', 1),
    -- ⛔ `BE-08`: «Мої задачі» — не косметичний фільтр, а єдиний перелік, який
    -- видно БЕЗ права `System.ViewHealth` (Q-156). Підказка каже саме це, бо
    -- інакше знятий прапорець виглядає як «показати більше», а не як «показати
    -- чуже», і відмова 403 читається як збій.
    (N'jobs.mineOnly',                   N'en', N'Only my jobs', 1),
    (N'jobs.mineOnlyHint',               N'en', N'Your own jobs are visible without the System.ViewHealth permission; the full queue is not.', 1),
    (N'jobs.recentStarted',              N'en', N'Started', 1),
    (N'grid.emptyTable',                 N'en', N'This table has no columns for the selected period', 1),
    (N'grid.emptyTableHint',             N'en', N'The template version in force for this period defines no columns for the table.', 1),

    -- ⛔ Окремий стан, а не той самий текст: «немає колонок» і «немає рядків»
    -- лікуються по-різному, і фіксована таблиця без рядків раніше не мала
    -- жодного повідомлення взагалі — вона малювалася як звичайна сітка,
    -- у яку просто нема куди вводити (директива №09 `W8` п.2, `S-13`).
    (N'grid.emptyFixedTable',            N'en', N'This table has no rows for the selected period', 1),
    (N'grid.emptyFixedTableHint',        N'en', N'Rows of a fixed table come from the template: the version in force for this period defines none.', 1),

    -- ⛔ Заголовок колонки підпису рядка. Підпис сервер рахував і локалізував
    -- давно (`TableSliceDto.Label`), але сітка документа його не показувала
    -- взагалі: у формі з фіксованими рядками оператор бачив стовпчики чисел
    -- без жодної ознаки, котрий рядок що означає.
    (N'grid.rowLabelHeader',             N'en', N'Row', 1),
    -- Рядок формули й рядок підсумків сітки (UI-08). ⛔ Вираз обчислюваної
    -- колонки сервер із таблицею НЕ надсилає (`ColumnDto` його не несе), тож
    -- рядок формули каже про це словами, а не вигадує вміст.
    -- ⚠ Підсумок рахує лише ВИДИМІ заповнені комірки, і `totalsCellHint`
    -- називає їхню кількість: інакше сума мовчки видавала б себе за суму по
    -- всьому стовпцю.
    (N'grid.formulaBarLabel',            N'en', N'Formula bar', 1),
    (N'grid.formulaBarEmpty',            N'en', N'Select a cell to see what is in it', 1),
    (N'grid.formulaBarAddress',          N'en', N'{row} · {column}', 1),
    (N'grid.formulaBarCalculated',       N'en', N'Calculated', 1),
    (N'grid.formulaBarNoExpression',     N'en', N'Calculated by the system — by a template formula or by the methodology bound to this column. The expression is not sent with the table.', 1),
    (N'grid.formulaBarValue',            N'en', N'Value: {value}', 1),
    (N'grid.formulaBarNoValue',          N'en', N'(empty)', 1),
    (N'grid.totalsRowLabel',             N'en', N'Total', 1),
    (N'grid.totalsCellHint',             N'en', N'Sum of {count} filled cells in this column', 1),
    (N'health.noChecks',                 N'en', N'No health checks are registered', 1),
    (N'health.noChecksHint',             N'en', N'The server returned an empty report. That is a server configuration problem, not an empty system.', 1),
    (N'health.noDbDetails',              N'en', N'The database check returned no details', 1),

    -- BE-18: факти про процес і команда для DBA (D15-12: застосунок не виконує DDL).
    (N'health.facts',                    N'en', N'System', 1),
    (N'health.facts.productVersion',     N'en', N'Product version', 1),
    (N'health.facts.startedAt',          N'en', N'Started at', 1),
    (N'health.facts.environment',        N'en', N'Environment', 1),
    (N'health.facts.notificationTransport', N'en', N'Notification transport', 1),
    (N'health.facts.transportNotConfigured', N'en', N'Not configured: notifications stay in the queue', 1),
    (N'health.facts.logDirectory',       N'en', N'Log directory', 1),
    -- BE-34: `teamsNotImplemented` прибрано — відправник вебхука є, і проба йде
    -- ним самим. Лишився стан, у якому транспорт каналу не має відправника
    -- взагалі: те саме, що рядок `Failed` у журналі доставок.
    (N'notifications.test.senderNotRegistered', N'en', N'No sender is registered for this channel transport: messages to it never arrive.', 1),
    (N'notifications.test.smtpNotConfigured',   N'en', N'The SMTP transport is not configured on the server.', 1),
    -- BE-21: те саме для джерел даних — транспорт джерела не має адаптера.
    -- Проба віддає `ok: false` із цим ключем, а не 500: конфігурація, у якій
    -- обрано транспорт без адаптера, — стан системи, а не аварія запиту.
    (N'integration.test.adapterNotRegistered', N'en', N'No adapter is registered for this source transport: collection from it never runs.', 1),
    (N'health.copyPartitionScript',      N'en', N'Copy command for DBA', 1),
    (N'health.partitionScriptCopied',    N'en', N'Partition command copied to the clipboard.', 1),
    (N'profile.theme',                   N'en', N'Theme', 1),
    (N'profile.themeAuto',               N'en', N'System', 1),
    (N'profile.themeLight',              N'en', N'Light', 1),
    (N'profile.themeDark',               N'en', N'Dark', 1),
    (N'profile.density',                 N'en', N'Row height', 1),
    (N'profile.densityCompact',          N'en', N'Compact', 1),
    (N'profile.densityComfortable',      N'en', N'Comfortable', 1),

    -- ⚠ Лише підпис перемикача — англійський, як і решта каталогу. Самі
    -- переклади ru/kz — робота термінолога (`C-7`, T9 директиви №11):
    -- реєстр мов (`sys_ecr.Language`, вище) уже мав ru/kz, каталог рядків —
    -- ще ні, і перемикач без цього підпису показував би позначений ключ
    -- (`D-138`) у списку, що вже дає вибір трьох мов.
    (N'profile.language',                N'en', N'Language', 1),
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

    -- Прогалина 2 директиви паритету зі старою системою (Q-327 → Q-331):
    -- до Q-331 кнопка на екрані аркуша насправді перераховувала весь
    -- документ, і підказка лише чесно про це попереджала. Тепер перерахунок
    -- дійсно звужений до цього аркуша — підказка називає лишень те, що й
    -- досі відрізняється від наївного очікування: формула цього аркуша
    -- має право читати дані сусіднього, тож перерахунок однаково враховує
    -- весь документ, хоч і пише лише в цей аркуш.
    (N'workflow.recalculateHint',        N'en', N'Recalculates this sheet. Formulas may still read data from other sheets of the same document.', 1),
    (N'workflow.approved',               N'en', N'The sheet has been approved.', 1),
    (N'workflow.rejected',               N'en', N'The sheet has been returned to the author.', 1),
    (N'workflow.reopened',               N'en', N'The sheet is editable again.', 1),
    (N'workflow.recalcQueued',           N'en', N'Recalculation queued as job {job}.', 1),

    -- ⛔ Відгук на «Перерахувати» (директива №09 `W8` п.7). Доти було рівно
    -- одне «поставлено в чергу як {job}» — GUID, який нікуди не ввести, і
    -- жодного слова про те, чим усе скінчилося.
    (N'workflow.recalcRunning',          N'en', N'Recalculating…', 1),
    (N'workflow.recalcDone',             N'en', N'Recalculation finished: the figures are up to date.', 1),
    (N'workflow.recalcFailed',           N'en', N'Recalculation failed. Open Jobs to see why.', 1),
    (N'workflow.reason',                 N'en', N'Reason', 1),
    (N'workflow.rejectTitle',            N'en', N'Reject the sheet', 1),
    (N'workflow.rejectHint',             N'en', N'Say what has to be corrected: the author sees this text and nothing else.', 1),
    (N'workflow.reopenTitle',            N'en', N'Return the sheet for edits', 1),
    (N'workflow.reopenHint',             N'en', N'Submitted figures are about to change. The reason stays in the audit trail for good.', 1),
    (N'workflow.recall',                 N'en', N'Recall', 1),
    (N'workflow.recalled',               N'en', N'The submission has been recalled: the sheet is a draft again.', 1),
    (N'workflow.recallTitle',            N'en', N'Recall the submission', 1),
    (N'workflow.recallHint',             N'en', N'Possible only until the first approver signs. The reason stays in the approval history.', 1),

    -- Імпорт із обов'язковим переглядом diff (модуль 6.10).
    (N'import.pick',                     N'en', N'Import from Excel', 1),
    (N'import.title',                    N'en', N'Review the import', 1),
    (N'import.changes',                  N'en', N'{count} change(s)', 1),
    (N'import.conflicts',                N'en', N'{count} conflict(s)', 1),
    (N'import.rejected',                 N'en', N'{count} rejected', 1),
    (N'import.blockedTitle',             N'en', N'This file cannot be applied as it is', 1),
    (N'import.blockedHint',              N'en', N'Partial application is not allowed: fix the file or refresh the sheet and import again.', 1),
    (N'import.noChanges',                N'en', N'The file matches the sheet: there is nothing to apply.', 1),
    -- V-10: зміна й відмова називають таблицю (ключі R1/C1 однакові в десятках таблиць).
    (N'import.table',                    N'en', N'Table', 1),
    (N'import.row',                      N'en', N'Row', 1),
    (N'import.column',                   N'en', N'Column', 1),
    (N'import.was',                      N'en', N'Was', 1),
    (N'import.becomes',                  N'en', N'Becomes', 1),
    (N'import.reason',                   N'en', N'Reason', 1),
    (N'import.apply',                    N'en', N'Apply', 1),
    (N'import.applied',                  N'en', N'The import has been applied.', 1),
    -- V-10: причини відмов прев'ю — мовою інтерфейсу, за messageKey відмови.
    -- V-11: документ заводиться на версії шаблону проєкту.
    (N'err.ECR-DOC-0422.versionNotProject', N'en', N'A document is created on the template version of its project; a different version is not accepted.', 1),
    (N'import.rejectedCell',             N'en', N'This value cannot be imported.', 1),
    (N'err.ECR-CELL-4221.importCalculated', N'en', N'The system calculates this cell, and the file changes its value: the value from the file is not applied.', 1),
    (N'err.ECR-ROW-0404.importNoRow',    N'en', N'The document has no row with this key: import does not create rows.', 1),
    (N'err.ECR-ROW-0404.importOutsideRows', N'en', N'This value is outside the rows of the table: import does not create rows. Add the row in the sheet first, then export again.', 1),
    (N'err.ECR-CELL-0422.importIntegerDigits', N'en', N'The number has more than 18 digits before the decimal point: storage cannot hold it.', 1),
    (N'err.ECR-IMP-0422.importInstanceMissing', N'en', N'This table from the file is not in the document for this period: the structure was probably changed after export.', 1),
    (N'err.ECR-IMP-0422.importTableMissing', N'en', N'This table from the file is not in the template version in force.', 1),

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
    -- U-19: перелік версій на картці шаблону (`TemplateVersionsSection`).
    (N'templates.versionStatus',         N'en', N'Status', 1),
    (N'templates.versionPublishedAt',    N'en', N'Published', 1),
    (N'templates.versionsEmpty',         N'en', N'No versions yet', 1),
    (N'templates.versionsEmptyHint',     N'en', N'A version holds the sheets and columns of the template. Create the first one to lay out its structure.', 1),

    -- Картка шаблону (`UI-09`): перейменування, архівування, повернення в обіг.
    -- ⚠ `templates.card` — назва РІВНЯ, а не заглушка замість назви шаблону:
    -- показується лише доки картка їде, і на архівованому шаблоні без жодної
    -- непорожньої назви (сервер вимагає непорожньою лише одну мову).
    (N'templates.card',                  N'en', N'Template', 1),
    (N'templates.rename',                N'en', N'Rename template', 1),
    (N'templates.renamed',               N'en', N'The template has been renamed.', 1),
    (N'templates.archive',               N'en', N'Archive template', 1),
    (N'templates.archived',              N'en', N'The template has been archived.', 1),
    (N'templates.archivedHint',          N'en', N'Archived: new documents are no longer created from it, while existing ones keep working.', 1),
    (N'templates.archiveTitle',          N'en', N'Archive template "{name}"?', 1),
    (N'templates.archiveText',           N'en', N'The template stops being offered for new documents.', 1),
    (N'templates.archiveDependents',     N'en', N'{count} project(s) and document(s) already depend on this template.', 1),
    (N'templates.archiveNote',           N'en', N'Reversible: you can bring the template back into use from this page.', 1),
    (N'templates.restore',               N'en', N'Bring back into use', 1),
    (N'templates.restored',              N'en', N'The template is back in use.', 1),
    (N'templates.undo',                  N'en', N'Undo', 1),
    (N'templates.dependentWork',         N'en', N'Dependent work', 1),
    -- ⚠ Версії в це число НЕ входять: вони належать самому шаблону, а питання
    -- перед архівуванням — скільки чужої роботи на нього спирається.
    (N'templates.dependentBreakdown',    N'en', N'{projects} project(s) bound to a version, {documents} document(s) in them.', 1),

    (N'version.clone',                   N'en', N'Clone version', 1),
    (N'version.diff',                    N'en', N'Compare versions', 1),
    (N'version.diffOther',               N'en', N'Compare with version', 1),
    -- ⛔ Аудит-пас 5: старий текст надсилав до переліку шаблонів по ідентифікатор
    -- версії, а той список показує лише номер версії (`1.0.0.0`), не id —
    -- ідентифікатор видно ЛИШЕ в адресному рядку відкритої версії.
    (N'version.diffOtherHint',           N'en', N'Another version of this template. Changes are always shown from the older version to the newer one.', 1),
    (N'version.diffPick',                N'en', N'Pick the other version', 1),
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
    -- ⛔ Аудит-пас 8, lane7, п.11: те саме пояснення, але НА самій сторінці
    -- версії — раніше воно жило лише всередині діалогу клонування.
    (N'version.structureFrozen',         N'en', N'This published version is frozen: structural changes go through "Clone version".', 1),
    (N'version.cloned',                  N'en', N'The clone is ready and open.', 1),
    (N'version.presentation',            N'en', N'Appearance', 1),
    (N'version.patched',                 N'en', N'Applied; the version is now at revision {revision}.', 1),
    -- Підпис лічильника поруч із заголовком версії (раніше — голе `r0`).
    (N'version.presentationRevision',    N'en', N'Appearance revision {revision}', 1),
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
    (N'periods.archiveConfirm',          N'en', N'The project becomes read-only. It is not deleted: submitted forms still refer to it.', 1),
    (N'periods.archiveOpenPeriods',       N'en', N'This project still has periods that are not Closed. Archiving is only allowed once every period is closed.', 1),
    (N'periods.archived',                N'en', N'The project is archived. It is not deleted: submitted forms still refer to it.', 1),
    (N'periods.current',                 N'en', N'current', 1),
    (N'periods.currentNotOpen',          N'en', N'current is not Open', 1),
    (N'periods.pin',                     N'en', N'Make current', 1),
    (N'periods.pinHint',                 N'en', N'The calendar stops choosing the current period by itself. Say why.', 1),
    (N'periods.pinned',                  N'en', N'The current period is pinned.', 1),
    (N'periods.reopen',                  N'en', N'Reopen period', 1),
    (N'periods.reopenHint',              N'en', N'A closed period holds submitted reporting. The reason stays in the audit trail.', 1),
    (N'periods.reopened',                N'en', N'The period is open again.', 1),
    (N'periods.reopenedUntil',           N'en', N'open until {until}', 1),

    -- ⛔ Вікно перевідкриття. Кнопка слала `until: null` із коментарем
    -- «безстроково», а сервер відкриває період лише до кінця доби майданчика
    -- (`EndOfSiteDay`, D-68) — тобто о півночі він закривався сам, і людина
    -- дізнавалася про це вже по факту. Тепер строк задається явно, а порожнє
    -- поле називає рівно те, що зробить сервер.
    (N'periods.reopenUntil',             N'en', N'Open until', 1),
    (N'periods.reopenUntilHint',         N'en', N'Leave empty to reopen until the end of the site day — the period closes itself at midnight.', 1),

    -- ⚠ Окремий рядок, а не позичений `periods.reopenedUntil`: причина відмови
    -- і підпис стану — різні твердження, і другий у ролі першого читається як
    -- «період відкрито до…», хоча його ще не відкривали.
    -- ⛔ Сервер минулий строк НЕ відхиляє (`Period.Reopen`): період пішов би в
    -- `Grace` із межею в минулому і закрився наступним прогоном `PeriodStateJob`
    -- — мовчки. Тобто клієнтський запобіжник тут не дублює сервер, а закриває
    -- те, чого на сервері немає.
    (N'periods.reopenUntilPast',         N'en', N'The chosen date has already passed: a window that ends in the past closes the period straight away.', 1),

    -- Безпека: ролі, користувачі, перегляд чужими правами.
    (N'security.createRole',             N'en', N'New role', 1),
    (N'security.roleCreated',            N'en', N'The role has been created. Grants say which projects it opens.', 1),
    (N'security.roleCode',               N'en', N'Code', 1),
    (N'security.roleCodeHint',           N'en', N'Used in grants and audit. Built-in role codes cannot be changed.', 1),
    (N'security.roleName',               N'en', N'Name', 1),
    -- `BE-14`: клонувати / перейменувати / видалити роль.
    (N'security.cloneRole',              N'en', N'Clone', 1),
    (N'security.renameRole',             N'en', N'Rename', 1),
    (N'security.newRoleCode',            N'en', N'New code', 1),
    (N'security.roleCloned',             N'en', N'The role has been cloned with the same permissions. Grants are not copied.', 1),
    (N'security.roleRenamed',            N'en', N'The role has been renamed.', 1),
    (N'security.roleDeleted',            N'en', N'The role has been deleted.', 1),
    (N'security.deleteRoleConfirm',      N'en', N'Delete role "{code}"? This cannot be undone.', 1),
    (N'security.roleDeleteRefused',      N'en', N'The role cannot be deleted', 1),
    (N'security.roleAssignments',        N'en', N'Assignments', 1),
    (N'security.roleApprovalSteps',      N'en', N'Approval route steps', 1),
    (N'security.rolePeriodAccessRules',  N'en', N'Period access rules', 1),
    -- Ролі, призначені групам каталогу (ФВ-6.15): перелік, відкликання, призначення.
    (N'groupRoles.title',                N'en', N'Roles assigned to directory groups', 1),
    (N'groupRoles.group',                N'en', N'Group', 1),
    (N'groupRoles.revoke',               N'en', N'Revoke', 1),
    (N'groupRoles.revoked',              N'en', N'The role has been revoked from the group. It stops working for signed-in members immediately.', 1),
    (N'groupRoles.principal',            N'en', N'Group name or SID', 1),
    (N'groupRoles.principalHint',        N'en', N'DOMAIN\Group, MACHINE\Group, BUILTIN\Group or S-1-...', 1),
    (N'groupRoles.assign',               N'en', N'Assign role to group', 1),
    (N'groupRoles.assigned',             N'en', N'The role has been assigned to the group.', 1),
    (N'groupRoles.assignedNextSignIn',   N'en', N'The role has been assigned. Members who are already signed in get it after their next sign-in.', 1),
    (N'groupRoles.dangerousTitle',       N'en', N'This role carries dangerous permissions; every member of the group would get them', 1),
    (N'groupRoles.assignAnyway',         N'en', N'Assign anyway', 1),
    (N'groupRoles.validFrom',            N'en', N'Valid from', 1),
    (N'groupRoles.validTo',              N'en', N'Valid to', 1),
    (N'groupRoles.validityOrder',        N'en', N'The end date cannot be earlier than the start date', 1),
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
    -- Видалення документа-чернетки. ⚠ Примітка каже прямо, що остаточне слово
    -- за сервером: історію погодження клієнт не бачить.
    (N'documents.delete',                N'en', N'Delete document', 1),
    (N'documents.deleteTitle',           N'en', N'Delete document {name}?', 1),
    (N'documents.deleteText',            N'en', N'The draft document and all data entered in it will be deleted. This cannot be undone.', 1),
    (N'documents.deleteNote',            N'en', N'Only a draft can be deleted: a document that has already been through approval will be refused.', 1),
    (N'documents.deleted',               N'en', N'Document {name} has been deleted.', 1),
    -- Зміна бізнес-ключа документа (ФВ-3.9). ⚠ Ключ змінюється лише доки
    -- жоден аркуш не поданий і не затверджений; `changeKeyStaleHint` — про
    -- 409, коли ключ на сервері вже інший, тож повторювати ту саму форму нема
    -- чим, доки людина не побачить новий.
    (N'documents.changeKey',             N'en', N'Change document key', 1),
    (N'documents.changeKeyTitle',        N'en', N'Change document key', 1),
    (N'documents.changeKeyLockedHint',   N'en', N'The key cannot be changed: a sheet has already been submitted or approved.', 1),
    (N'documents.changeKeyStaleHint',    N'en', N'The key was changed by someone else. The document has been reloaded; check the current key before trying again.', 1),
    (N'documents.keyChanged',            N'en', N'The document key has been changed.', 1),
    (N'documents.newBusinessKey',        N'en', N'New key', 1),
    (N'documents.newBusinessKeyHint',    N'en', N'Up to {max} characters; it must differ from the current key. The change and its reason go to the audit trail.', 1),
    (N'documents.version',               N'en', N'Template version', 1),
    (N'documents.versionHint',           N'en', N'Only published versions: a draft has no frozen structure.', 1),
    (N'documents.pickVersion',           N'en', N'Pick a version', 1),
    (N'documents.sheetsHint',            N'en', N'Composition rules are checked by the server: a group may require all of its sheets, or exactly one.', 1),
    (N'documents.name',                  N'en', N'Document name', 1),
    (N'documents.nameHint',              N'en', N'Optional. Shown next to the business key; does not replace it.', 1),
    (N'documents.groupRuleRequiresAll',  N'en', N'Group "{group}": {picked} of {total} sheets selected — the group requires all of them.', 1),
    (N'documents.groupRuleRequiresOne',  N'en', N'Group "{group}": requires at least one sheet.', 1),
    (N'documents.summaryLabel',          N'en', N'Documents by state', 1),
    (N'documents.summaryWithIssues',     N'en', N'With issues', 1),
    (N'documents.modified',              N'en', N'Modified', 1),
    (N'documents.errors',                N'en', N'Errors', 1),

    -- Записи довідників і вікна чинності.
    (N'registries.newEntry',             N'en', N'New entry', 1),
    (N'registries.editEntry',            N'en', N'Edit', 1),
    (N'registries.entryCreated',         N'en', N'The entry has been created.', 1),
    (N'registries.entrySaved',           N'en', N'The entry has been saved.', 1),
    (N'registries.entryCodeHint',        N'en', N'Cells store the entry id, so the code can change. An entry that anything still references cannot be removed — close it with an end date instead.', 1),
    -- Імпорт записів довідника з CSV (BE-24 крок 3). ⚠ Перший перегляд іде
    -- сухим прогоном (dryRun): файл не застосовується, доки людина не
    -- натисне «Apply». Файл із помилковими рядками не застосовується взагалі —
    -- або всі рядки, або жоден. Причини рядків приходять messageKey сервера
    -- (позиція в DynamicKeySites), тому власних ключів під них тут немає.
    (N'registry.import.pick',            N'en', N'Import from CSV', 1),
    (N'registry.import.title',           N'en', N'Review the import', 1),
    (N'registry.import.added',           N'en', N'{count} added', 1),
    (N'registry.import.updated',         N'en', N'{count} updated', 1),
    (N'registry.import.unchanged',       N'en', N'{count} unchanged', 1),
    (N'registry.import.errorsCount',     N'en', N'{count} error(s)', 1),
    (N'registry.import.blockedTitle',    N'en', N'This file cannot be applied as it is', 1),
    (N'registry.import.blockedHint',     N'en', N'Fix the rows listed below and import the file again.', 1),
    (N'registry.import.row',             N'en', N'Row', 1),
    (N'registry.import.entryKey',        N'en', N'Code', 1),
    (N'registry.import.field',           N'en', N'Field', 1),
    (N'registry.import.reason',          N'en', N'Reason', 1),
    (N'registry.import.apply',           N'en', N'Apply', 1),
    (N'registry.import.applied',         N'en', N'{added} added, {updated} updated, {unchanged} unchanged.', 1),
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
    (N'registries.unit',                 N'en', N'Unit', 1),
    -- ⛔ Без цієї кнопки довідник, заведений через `/admin/registries`, не міг
    -- отримати жодного поля: конструктор показував наявні поля, а додати нове
    -- не було чим (сусідня вкладка `Rules` кнопку «Add rule» мала завжди).
    (N'registries.addField',             N'en', N'Add field', 1),
    (N'registries.removeField',          N'en', N'Remove', 1),
    (N'registries.fieldIncomplete',      N'en', N'Fill code and name; a lookup field also needs its target registry.', 1),
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
    (N'registries.definitionVersion',    N'en', N'Definition v{version}', 1),
    -- Чернетка опису довідника і публікація (BE-24 крок 2). ⚠ Форма показує
    -- ЧЕРНЕТКУ, а не опублікований опис, доки чернетка є — банер каже це прямо.
    -- Колишні `saveDefinition`/`definitionSaved` прибрані: збереження тепер
    -- завжди йде в чернетку, а опис змінюється лише публікацією.
    (N'registries.draftPresent',         N'en', N'Unsaved draft', 1),
    (N'registries.draftPresentHint',     N'en', N'Last changed {when} by user {user}. The form below shows the draft, not the published definition.', 1),
    (N'registries.saveDraft',            N'en', N'Save draft', 1),
    (N'registries.draftSaved',           N'en', N'The draft has been saved.', 1),
    (N'registries.publish',              N'en', N'Publish', 1),
    (N'registries.publishTitle',         N'en', N'Publish the definition of registry "{code}"?', 1),
    (N'registries.publishConsequence',   N'en', N'The draft replaces the published definition and the definition version grows.', 1),
    (N'registries.definitionPublished',  N'en', N'Published. Definition version: {version}.', 1),
    (N'registries.discardDraft',         N'en', N'Discard draft', 1),
    (N'registries.discardTitle',         N'en', N'Discard the draft definition of registry "{code}"?', 1),
    (N'registries.discardConsequence',   N'en', N'Unsaved changes will be lost; the form returns to the published definition.', 1),
    (N'registries.draftDiscarded',       N'en', N'The draft has been discarded.', 1),
    (N'registries.reloadDraft',          N'en', N'Take the current version', 1),
    (N'registries.saveAndPublish',       N'en', N'Save and publish', 1),
    (N'registries.saveAndPublishTitle',  N'en', N'Save and publish the definition of registry "{code}"?', 1),
    (N'registries.saveAndPublishConsequence', N'en', N'The form is saved and published in one step, without a draft: the definition version grows immediately.', 1),
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
    -- ⚠ Показано, коли `changedByUserId` не знайшовся в переліку користувачів:
    -- нема права `Security.ManageUsers`, або користувача видалено.
    (N'registries.userUnresolved',       N'en', N'unresolved', 1),
    -- Вкладка «Де використано» (BE-24). «Показано N із M» з'являється лише
    -- тоді, коли сервер віддав не всі посилання (D15-06).
    (N'registries.tabUsage',             N'en', N'Where used', 1),
    (N'registries.usageNone',            N'en', N'Not used anywhere', 1),
    (N'registries.usageTotal',           N'en', N'{total} references', 1),
    (N'registries.usageShown',           N'en', N'Showing {shown} of {total}', 1),
    -- Вид залежного (`UsageItemDto.Kind`) — спільний простір для обох «де
    -- використано»: довідника (`RegistryStore.GetUsageAsync`) і одиниці
    -- (`UnitStore`). Перелік — рівно `UsageKinds.All`; що кожен вид має тут
    -- рядок, а в `UsageKindLabel.tsx` — гілку, стереже `UsageKindCatalogTests`.
    -- ⚠ Колишні `registries.usageKind.*` (п'ять видів довідника) прибрані —
    -- клієнт їх більше не просить. MERGE тут лише додає, тож у вже
    -- розгорнутих базах ці рядки лишаються мертвими.
    (N'usageKind.templateColumn',       N'en', N'Template column', 1),
    (N'usageKind.registryField',        N'en', N'Registry field', 1),
    (N'usageKind.methodologySubstance', N'en', N'Methodology substance', 1),
    (N'usageKind.sourceEntity',         N'en', N'Source entity', 1),
    (N'usageKind.methodologyConstant',  N'en', N'Methodology constant', 1),
    (N'usageKind.methodologyFormula',   N'en', N'Methodology formula', 1),
    (N'usageKind.methodologyOutput',    N'en', N'Methodology output', 1),
    -- `fieldMap` — мапінг поля джерела, де одиниця стоїть на боці джерела або цілі.
    (N'usageKind.fieldMap',             N'en', N'Source field mapping', 1),
    (N'usageKind.unitConversion',       N'en', N'Unit conversion rule', 1),
    -- `derivedUnit` — одиниця, у якої ця стоїть у чисельнику чи знаменнику.
    (N'usageKind.derivedUnit',          N'en', N'Derived unit', 1),
    -- ⚠ `dimensionBase` не видаляється ніколи: через базову одиницю йде кожна
    -- конверсія розмірності. Назва каже саме це.
    (N'usageKind.dimensionBase',        N'en', N'Base unit of dimension', 1),
    -- ⚠ `data` — не звіт і не лише документи: для довідника це `doc.CellValue`,
    -- для одиниці ще `dic.RegistryValue`, `calc.CalculationResult`,
    -- `ext.RawData`. Один рядок на таблицю, без числа (підпис — ім'я таблиці).
    -- Колишнє «Values in documents» для одиниці було б неправдою.
    (N'usageKind.data',                 N'en', N'Stored data', 1),
    -- ФВ-8.14: «де використано» колонки шаблону.
    (N'usageKind.templateFormula',      N'en', N'Template formula', 1),
    (N'usageKind.calculationBinding',   N'en', N'Methodology binding', 1),
    (N'usageKind.methodologyRule',      N'en', N'Methodology rule', 1),
    (N'usageKind.methodologyRequiredInput', N'en', N'Methodology required input', 1),
    (N'registries.newRegistry',          N'en', N'New registry', 1),
    (N'registries.newRegistryTitle',     N'en', N'New registry', 1),
    (N'registries.registryCodeHint',     N'en', N'Latin letters, digits and underscore; cannot be changed later.', 1),
    (N'registries.temporalField',        N'en', N'Time-bound (entries have a validity window)', 1),
    (N'registries.temporalFieldHint',    N'en', N'Decide once: turning this on later would reinterpret entries already entered.', 1),
    (N'registries.created',              N'en', N'The registry has been created.', 1),
    (N'periods.timeZone',                N'en', N'Site time zone (IANA)', 1),
    -- ⚠ Підказка називає IANA і незмінність разом: поле обов'язкове і без
    -- початкового значення (H-13), тож користувач має знати обидві причини,
    -- перш ніж обере — після відкриття першого періоду вибір остаточний.
    (N'periods.timeZoneHint',            N'en', N'IANA identifier of the site, for example Asia/Aqtau. Period boundaries and late-edit marks are calculated in this zone, and it cannot be changed once the first period is open.', 1),
    (N'periods.templateVersion',         N'en', N'Template version', 1),
    (N'periods.templateVersionHint',     N'en', N'Published versions only: a draft has no frozen structure.', 1),
    (N'periods.policy',                  N'en', N'Period policy', 1),
    (N'periods.policyHint',              N'en', N'Grace and hard-close offsets in days; they define when a period stops accepting data.', 1),
    -- T6/#36: кількість періодів для `Custom` — без цього поля вид
    -- `PeriodKind.Custom` був оголошений у домені й недосяжний через
    -- інтерфейс, бо форма не мала звідки взяти кількість.
    (N'periods.customCount',             N'en', N'Number of periods', 1),
    (N'periods.customCountHint',         N'en', N'Must divide the year evenly (1..12): 5 would leave November and December outside any period.', 1),
    -- T6/#37: CRUD політик періодів — до цього завести чи змінити політику
    -- можна було лише сідингом або рукою DBA.
    (N'periods.managePolicies',          N'en', N'Manage policies', 1),
    (N'periods.policyCode',              N'en', N'Code', 1),
    (N'periods.policyOpenOffset',        N'en', N'Open offset (days)', 1),
    (N'periods.policyGraceOffset',       N'en', N'Grace offset (days)', 1),
    (N'periods.policyHardClose',         N'en', N'Hard-close offset (days)', 1),
    (N'periods.policyYearGrace',         N'en', N'Year grace (days)', 1),
    (N'periods.policyCreate',            N'en', N'Add policy', 1),
    (N'periods.policyCreated',           N'en', N'The policy is created.', 1),
    (N'periods.policyUpdated',           N'en', N'The policy is updated. Projects pick up the new offsets on their next calendar rebuild.', 1),
    -- T6/#52: зміна поясу майданчика — дозволена лише поки жоден період не
    -- відкривався (ФВ-1.1a); кнопка доступна лише чернетці з тієї ж причини,
    -- що й активація вище.
    (N'periods.timezoneChange',          N'en', N'Change time zone', 1),
    (N'periods.timezoneChangeHint',      N'en', N'Allowed only while every period of the project is still Scheduled: once one opens, the boundaries become someone''s obligation.', 1),
    (N'periods.timezoneChanged',         N'en', N'The site time zone is changed.', 1),
    (N'workflow.route',                  N'en', N'Approval route', 1),
    (N'workflow.routeHint',              N'en', N'Who approves, and in what order', 1),
    (N'workflow.routeEmptyHint',         N'en', N'No route means single-stage approval: one holder of the Approve level is enough. Removing every step returns the project to that.', 1),
    (N'workflow.routeNone',              N'en', N'No steps: approval is single-stage.', 1),
    (N'workflow.routeAddStep',           N'en', N'Add a step', 1),
    (N'workflow.routeAddStepHint',       N'en', N'The role that approves at this step. The same role may appear twice, but not twice in a row.', 1),
    (N'workflow.routeSaved',             N'en', N'The route now has {count} step(s).', 1),
    (N'workflow.routeCleared',           N'en', N'The route is removed: approval is single-stage again.', 1),
    (N'workflow.history',                N'en', N'History', 1),
    (N'workflow.historyStep',            N'en', N'step {step}', 1),
    (N'security.access',                 N'en', N'Access', 1),
    (N'security.accessSaved',            N'en', N'Saved: {count} role(s) assigned.', 1),
    (N'security.rolesHint',              N'en', N'The whole set at once: these roles are the person''s authority, and it should be seen as a whole.', 1),
    (N'security.noRolesTitle',           N'en', N'No roles assigned', 1),
    (N'security.noRolesWarning',         N'en', N'The account will open, and every screen will be empty. Assign at least one role.', 1),
    -- ⛔ UI-аудит, lane 1: роль(і) призначено, але жодна з них не несе
    -- жодного права — з погляду мультиселекту це "роль є", хоча ефект
    -- той самий, що й узагалі без ролі.
    (N'security.noPermissions',          N'en', N'no permissions', 1),
    (N'security.rolesGrantNothingTitle', N'en', N'Assigned role grants nothing', 1),
    (N'security.rolesGrantNothingWarning', N'en', N'The account will open, and every screen will be empty, even though a role is assigned. Assign a role that actually grants a permission.', 1),
    (N'security.email',                  N'en', N'Email', 1),
    (N'security.emailHint',              N'en', N'Without it no notification reaches this person, and the alerts switch stays off.', 1),
    (N'security.oneTimePassword',        N'en', N'One-time password', 1),
    (N'security.oneTimePasswordHint',    N'en', N'You will have to pass it on yourself. The server neither generates nor returns passwords, and the account must change it at first sign-in.', 1),
    (N'security.lastSignIn',             N'en', N'Last sign-in', 1),
    -- Адміністрування облікових записів (BE-12): блокування з причиною в
    -- журнал безпеки, розблокування, скидання пароля на одноразовий.
    (N'security.lockUser',               N'en', N'Lock', 1),
    (N'security.unlockUser',             N'en', N'Unlock', 1),
    (N'security.resetPassword',          N'en', N'Reset password', 1),
    (N'security.lockUserNamed',          N'en', N'Lock {userName}', 1),
    (N'security.unlockUserNamed',        N'en', N'Unlock {userName}', 1),
    (N'security.resetPasswordNamed',     N'en', N'Reset password for {userName}', 1),
    (N'security.resetPasswordHint',      N'en', N'The user will have to change this password at next sign-in. It is not shown again.', 1),
    (N'security.newPassword',            N'en', N'New password', 1),
    (N'security.lockReasonHint',         N'en', N'Required, up to {max} characters. Recorded in the security log; the user''s sessions end immediately.', 1),
    (N'security.reasonTooLong',          N'en', N'The reason is longer than {max} characters.', 1),
    (N'security.userLocked',             N'en', N'Account locked', 1),
    (N'security.userUnlocked',           N'en', N'Account unlocked', 1),
    (N'security.passwordResetDone',      N'en', N'Password set; the user will change it at next sign-in.', 1),

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
    -- ⛔ UI-аудит, lane 4: жоден обліковий запис, включно з повноправним
    -- адміністратором, не мав шляху додати одиницю виміру.
    (N'units.new',                       N'en', N'New unit', 1),
    (N'units.newCode',                   N'en', N'Code', 1),
    (N'units.newCodeHint',               N'en', N'Latin letters, digits and underscore; cannot be changed later.', 1),
    (N'units.symbol',                    N'en', N'Symbol', 1),
    (N'units.symbolHint',                N'en', N'Shown next to values, e.g. "kg".', 1),
    (N'units.name',                      N'en', N'Name', 1),
    (N'units.factorHint',                N'en', N'Multiplier to the dimension''s base unit.', 1),
    (N'units.offsetHint',                N'en', N'Only nonzero for temperature units (°C to K).', 1),
    (N'units.created',                   N'en', N'Unit created.', 1),
    (N'units.deleted',                   N'en', N'Unit removed.', 1),
    (N'units.deleteUnused',              N'en', N'Nothing refers to this unit.', 1),
    (N'units.deleteUsedIn',              N'en', N'Referenced in {total} place(s) - the unit cannot be removed until they are gone:', 1),
    -- Правка одиниці (BE-15 ч.2): код, розмірність і ознака базової не
    -- змінюються; множник і зсув — лише в одиниці, на яку ніщо не посилається.
    (N'units.edit',                      N'en', N'Edit', 1),
    (N'units.editTitle',                 N'en', N'Edit unit {code}', 1),
    (N'units.codeFixed',                 N'en', N'Code, dimension and the base flag cannot be changed.', 1),
    (N'units.factorLockedBase',          N'en', N'This is the base unit of its dimension: its factor and offset are fixed.', 1),
    (N'units.factorLockedChecking',      N'en', N'Checking whether anything refers to this unit…', 1),
    (N'units.factorLockedUnknown',       N'en', N'Could not check where this unit is used, so its factor and offset stay locked.', 1),
    (N'units.factorLockedUsed',          N'en', N'Referenced in {total} place(s): factor and offset cannot change.', 1),
    (N'units.reloadCurrent',             N'en', N'Take the current version', 1),
    (N'units.saved',                     N'en', N'Unit saved.', 1),
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

    -- ⛔ `BE-03`: фільтри журналу й історія ОДНІЄЇ комірки. До цього журнал
    -- умів лише «документ за вікном»: питання «хто саме», «звідки це число»
    -- і «що було з ЦІЄЮ коміркою» можна було поставити лише читаючи сторінки
    -- очима — тобто ніяк, бо сторінок за тиждень тисячі.
    (N'audit.author',                    N'en', N'By user', 1),
    (N'audit.authorHint',                N'en', N'User id; leave empty for everyone.', 1),
    (N'audit.originAny',                 N'en', N'Any origin', 1),
    (N'audit.lateOnly',                  N'en', N'Late edits only', 1),
    (N'audit.rowKey',                    N'en', N'Row key', 1),
    (N'audit.columnDefId',               N'en', N'Column', 1),
    -- ⚠ Підказка називає ОБМЕЖЕННЯ, а не поле: ключ рядка унікальний у межах
    -- екземпляра таблиці, тож без документа він нічого не адресує — і сервер
    -- відповідає 400, а не порожнім списком.
    (N'audit.cellHint',                  N'en', N'Row key and column need a document: together the three are the history of one cell.', 1),
    (N'audit.reset',                     N'en', N'Clear filters', 1),
    -- `BE-16`: друга вкладка екрана — загальний журнал структурних змін.
    (N'audit.viewCells',                 N'en', N'Cell changes', 1),
    (N'audit.viewStructure',             N'en', N'Structure changes', 1),
    (N'audit.entityType',                N'en', N'Entity type', 1),
    (N'audit.entity',                    N'en', N'Entity', 1),
    (N'audit.operation',                 N'en', N'Operation', 1),
    (N'audit.reason',                    N'en', N'Reason', 1),
    (N'audit.structureEmpty',            N'en', N'No structure changes in this window', 1),
    (N'audit.exportCsv',                 N'en', N'Export CSV', 1),

    -- ⛔ Знахідки перевірки узгодженості (`aud.ConsistencyIssue`). До цього
    -- екрана з продукту було видно лише КІЛЬКІСТЬ за типом (лічильник
    -- `ecr.consistency.issues`): «є 12 знахідок BROKEN_FK» без жодного
    -- способу дізнатися, які саме рядки зачеплені.
    (N'nav.consistency',                 N'en', N'Consistency issues', 1),
    (N'consistency.title',               N'en', N'Consistency issues', 1),
    (N'consistency.rule',                N'en', N'Rule', 1),
    (N'consistency.ruleHint',            N'en', N'Leave empty for every rule.', 1),
    (N'consistency.openOnly',            N'en', N'Unresolved only', 1),
    (N'consistency.when',                N'en', N'Detected at', 1),
    (N'consistency.severity',            N'en', N'Severity', 1),
    (N'consistency.entity',              N'en', N'Affected entity', 1),
    (N'consistency.what',                N'en', N'What was found', 1),
    (N'consistency.state',               N'en', N'State', 1),
    (N'consistency.open',                N'en', N'unresolved', 1),
    (N'consistency.resolved',            N'en', N'resolved', 1),
    (N'consistency.severityInfo',        N'en', N'info', 1),
    (N'consistency.severityWarning',     N'en', N'warning', 1),
    (N'consistency.severityError',       N'en', N'error', 1),
    -- ⚠ Мова текстів знахідок названа ЯВНО на самому екрані, а не замовчана:
    -- інакше англійський інтерфейс із українським рядком у колонці виглядає
    -- як дефект локалізації, а не як свідоме рішення.
    (N'consistency.messageLanguage',     N'en', N'The finding text is written by the nightly job and is not translated.', 1),
    (N'consistency.empty',               N'en', N'No consistency issues recorded', 1),
    (N'consistency.emptyHint',           N'en', N'The nightly check writes what it finds here; an empty journal means the last run found nothing.', 1),
    -- Позачерговий запуск перевірки: підтвердження з причиною (вона йде в
    -- журнал безпеки) і стан фонової задачі. `unknown` — стан задачі не
    -- вдалося прочитати, а не «перевірка впала».
    (N'consistency.runNow',              N'en', N'Run check now', 1),
    (N'consistency.runHint',             N'en', N'The reason is written to the security log.', 1),
    (N'consistency.runRunning',          N'en', N'Check is running', 1),
    (N'consistency.runSucceeded',        N'en', N'Check finished — list refreshed', 1),
    (N'consistency.runFailed',           N'en', N'Check failed', 1),
    (N'consistency.runUnknown',          N'en', N'Check status is unavailable', 1),
    (N'consistency.runJoined',           N'en', N'A check was already running (started earlier or by someone else) — following it', 1),
    -- Огляд звітної кампанії (BE-22): хто затримує кампанію періоду. Лічильники
    -- — по ВСІХ проєктах періоду (Q15-07), і підказка каже це прямо.
    (N'nav.campaign',                    N'en', N'Reporting campaign', 1),
    (N'campaign.title',                  N'en', N'Reporting campaign', 1),
    (N'campaign.scopeHint',              N'en', N'Counts cover every project of the period, not only the projects you have access to.', 1),
    (N'campaign.pickPeriod',             N'en', N'Pick a period', 1),
    (N'campaign.emptyTitle',             N'en', N'No projects in this period', 1),
    (N'campaign.emptyHint',              N'en', N'No project has reporting for this period, so there is no campaign to review.', 1),
    (N'campaign.truncatedTitle',         N'en', N'Showing {shown} of {total} projects', 1),
    -- ⚠ Обрізано лише ПЕРЕЛІК: підсумки й лічильники класів сервер рахує по
    -- всіх проєктах періоду (`totals`). Колишня підказка `truncatedHint`
    -- казала протилежне і прибрана разом із `laggingCount` і
    -- `nobodyLaggingHint` — клієнт їх більше не просить. MERGE тут лише
    -- додає, тож у вже розгорнутих базах ці рядки лишаються мертвими.
    (N'campaign.truncatedListHint',      N'en', N'Only the list is cut short. The totals above count all {total} projects of the period.', 1),
    (N'campaign.laggingTitle',           N'en', N'Holding up the campaign', 1),
    (N'campaign.nobodyLagging',          N'en', N'Nobody is holding up the campaign', 1),
    (N'campaign.nobodyLaggingServerHint', N'en', N'No project is past its submission deadline or close to it. Projects still in progress are counted in the totals.', 1),
    -- Клас проєкту рахує сервер (`CampaignProgressRule`): Done — усе
    -- затверджено і є зріз; Overdue — строк минув; AtRisk — до останнього дня
    -- подання лишилося мало; решта — InProgress.
    (N'campaign.progress.Overdue',       N'en', N'Overdue', 1),
    (N'campaign.progress.AtRisk',        N'en', N'At risk', 1),
    (N'campaign.progress.InProgress',    N'en', N'In progress', 1),
    (N'campaign.progress.Done',          N'en', N'Done', 1),
    (N'campaign.progressColumn',         N'en', N'Status', 1),
    (N'campaign.lastSubmissionDay',      N'en', N'Last day to submit', 1),
    (N'campaign.deadlineUnknown',        N'en', N'No deadline yet', 1),
    (N'campaign.projectCode',            N'en', N'Project code', 1),
    (N'campaign.projectName',            N'en', N'Project', 1),
    (N'campaign.documents',              N'en', N'Documents', 1),
    (N'campaign.snapshots',              N'en', N'Snapshots', 1),
    (N'nav.snapshots',                   N'en', N'Report snapshots', 1),
    (N'snapshots.title',                 N'en', N'Report snapshots', 1),
    (N'snapshots.build',                 N'en', N'Build snapshot', 1),
    (N'snapshots.buildHint',             N'en', N'A snapshot is immutable: building again creates a new one instead of overwriting.', 1),
    (N'snapshots.queued',                N'en', N'Build queued as job {job}.', 1),
    -- ⛔ UI-аудит, lane 6: побудова успішно завершується майже одразу, але
    -- список не оновлювався — ці два рядки супроводжують нове стеження за
    -- задачею (`jobFollow.ts`), не саму постановку в чергу.
    (N'snapshots.built',                 N'en', N'Snapshot built.', 1),
    (N'snapshots.buildFailed',           N'en', N'Snapshot build failed.', 1),
    (N'snapshots.code',                  N'en', N'Report code', 1),
    (N'snapshots.codeHint',              N'en', N'The report definition to build from; definitions are data, not code.', 1),
    (N'snapshots.builtAt',               N'en', N'Built at', 1),
    (N'snapshots.rows',                  N'en', N'Rows', 1),
    (N'snapshots.status',                N'en', N'Status', 1),
    (N'snapshots.hash',                  N'en', N'Content hash', 1),
    (N'snapshots.current',               N'en', N'current', 1),
    -- Формат чисел зрізу: `current` не позначається. ⚠ Ключі беруться з мапи в
    -- `SnapshotFormatBadge.tsx` через `t(look.label)` — `EndpointCoverageTests`
    -- їх не бачить; сторож — ФВ-14.9 у `test:a11y` на `/admin/snapshots`.
    (N'snapshots.formatLegacy',          N'en', N'Earlier format', 1),
    (N'snapshots.formatLegacyHint',      N'en', N'Built before numbers were extended to 16 decimal places. Kept exactly as it was submitted to the regulator.', 1),
    (N'snapshots.formatUnknown',         N'en', N'Format unknown', 1),
    (N'snapshots.formatUnknownHint',     N'en', N'The number format of this snapshot has not been determined yet.', 1),
    (N'snapshots.empty',                 N'en', N'No snapshots built yet', 1),
    (N'snapshots.emptyHint',             N'en', N'SSRS reads snapshots, not live data: until one is built, the regulator sees nothing.', 1),
    (N'snapshots.pickReport',            N'en', N'Pick a report', 1),
    (N'snapshots.noPublished',           N'en', N'No report definition has a published version yet: a snapshot can only be built from one.', 1),
    -- BE-17: перевірка незмінності зрізу — сума перераховується за збереженими рядками.
    (N'snapshots.verify',                N'en', N'Verify', 1),
    (N'snapshots.verifyMatch',           N'en', N'unchanged', 1),
    (N'snapshots.verifyMismatch',        N'en', N'content changed', 1),
    (N'snapshots.verifyStored',          N'en', N'Stored: {hash}', 1),
    (N'snapshots.verifyActual',          N'en', N'Actual: {hash}', 1),
    (N'snapshots.verifyLegacy',          N'en', N'Matched by the earlier checksum format: this snapshot was built before the format changed.', 1),
    -- D-52a: перегляд рядків зрізу в застосунку (другий споживач `rpt.*`).
    (N'snapshots.viewRows',              N'en', N'View rows', 1),
    (N'snapshots.rowsTitle',             N'en', N'Snapshot rows', 1),
    (N'snapshots.rowsMore',              N'en', N'Show more', 1),
    (N'snapshots.rowsEmpty',             N'en', N'This snapshot has no rows.', 1),

    -- R8: макет зрізу — групи й підсумки. ⚠ Підпис функції підсумку береться
    -- з каталогу, а не з коду сервера (`sum`/`count`/…): число без предмета —
    -- це не підсумок. `rowsFnOther` — арм на функцію, якої клієнт ще не знає.
    -- ⛔ Ключі однорівневі (`rowsFnSum`, не `rowsFn.sum`): сторож
    -- `Кожен_рядок_якого_просить_клієнт_є_в_каталозі` регуляркою двокрапкових
    -- ключів не бачить, тож дволанковий ключ проїхав би повз перевірку.
    (N'snapshots.rowsGroup',             N'en', N'Group by {column}', 1),
    (N'snapshots.rowsTotalGroup',        N'en', N'Group total', 1),
    (N'snapshots.rowsTotalAll',          N'en', N'Snapshot total', 1),
    (N'snapshots.rowsFnSum',             N'en', N'Sum', 1),
    (N'snapshots.rowsFnCount',           N'en', N'Count', 1),
    (N'snapshots.rowsFnAvg',             N'en', N'Average', 1),
    (N'snapshots.rowsFnMin',             N'en', N'Minimum', 1),
    (N'snapshots.rowsFnMax',             N'en', N'Maximum', 1),
    (N'snapshots.rowsFnOther',           N'en', N'Total', 1),

    -- R7: вивантаження зрізу в книгу. ⚠ Межа Excel названа ПОРУЧ із дією:
    -- числа в книзі мають 15 значущих цифр, і той, хто звіряє до останнього
    -- знаку, мусить дізнатися про це ДО вивантаження, а не після.
    -- R6: параметри звіту `@Name`. Оголошені в `RulesJson` версії (схема 2),
    -- значення задаються при побудові зрізу.
    (N'snapshots.parameters',            N'en', N'Report parameters', 1),
    (N'snapshots.parameterRequired',     N'en', N'Required', 1),
    (N'snapshots.parametersBlocked',     N'en', N'Fill in every required parameter — the server refuses a build without them.', 1),
    (N'snapshots.parametersUnknown',     N'en', N'The parameters of this report could not be read, so a build would go out blind. Retry, and build once they are known.', 1),

    (N'snapshots.export',                N'en', N'Download .xlsx', 1),
    (N'snapshots.exportHint',            N'en', N'Numbers in the workbook are rounded to 15 significant digits; use "View rows" to reconcile without loss.', 1),

    -- Описи звітів (ФВ-10.4, W7). ⛔ Не конструктор звітів: вигляд лишається
    -- в SSRS (ФВ-10.6), тут лише рядок даних, за яким будується зріз.
    (N'reportDefs.title',                N'en', N'Report definitions', 1),
    (N'reportDefs.manage',               N'en', N'Report definitions', 1),
    (N'reportDefs.empty',                N'en', N'No report is described yet: a snapshot has nothing to be built from.', 1),
    (N'reportDefs.code',                 N'en', N'Report code', 1),
    (N'reportDefs.codeHint',             N'en', N'The address of the report; a snapshot is built by this code and it cannot be renamed later.', 1),
    (N'reportDefs.regulatory',           N'en', N'regulatory', 1),
    (N'reportDefs.regulatoryHint',       N'en', N'Decides which statuses the rpt.v_* view lets through — not how important the report is.', 1),
    (N'reportDefs.inactive',             N'en', N'withdrawn', 1),
    (N'reportDefs.version',              N'en', N'Version', 1),
    (N'reportDefs.versionHint',          N'en', N'Unique within the report; up to 20 characters.', 1),
    (N'reportDefs.versions',             N'en', N'Versions', 1),
    (N'reportDefs.noVersions',           N'en', N'no versions', 1),
    (N'reportDefs.columns',              N'en', N'Snapshot columns', 1),
    (N'reportDefs.columnsHint',          N'en', N'The column code is the key of a snapshot row — it is what the report looks values up by.', 1),
    (N'reportDefs.columnCode',           N'en', N'Column code', 1),
    (N'reportDefs.columnKind',           N'en', N'Value type', 1),
    (N'reportDefs.addColumn',            N'en', N'Add column', 1),
    (N'reportDefs.removeColumn',         N'en', N'Remove column', 1),
    (N'reportDefs.add',                  N'en', N'Add report definition', 1),
    (N'reportDefs.added',                N'en', N'The report definition has been created as a draft version.', 1),
    (N'reportDefs.newVersion',           N'en', N'Add version', 1),
    (N'reportDefs.versionAdded',         N'en', N'The draft version has been created.', 1),
    (N'reportDefs.forReport',            N'en', N'Report', 1),
    (N'reportDefs.forReportHint',        N'en', N'A published version cannot be edited: change it by adding a new version.', 1),
    (N'reportDefs.publish',              N'en', N'Publish', 1),
    (N'reportDefs.published',            N'en', N'The version has been published; snapshots will be built from it.', 1),

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
    (N'uiStrings.coverage',              N'en', N'{language}: {translated} of {total} translated, {missing} missing', 1),
    (N'uiStrings.missingOnly',           N'en', N'Missing only', 1),
    -- Обмін перекладом через CSV (BE-13 ч.2). ⚠ Імпорт або застосовується
    -- цілком, або не пише нічого: підказка каже це прямо, щоб людина не шукала
    -- «частково імпортовані» рядки.
    (N'uiStrings.exportCsv',             N'en', N'Export CSV', 1),
    (N'uiStrings.importCsv',             N'en', N'Import CSV…', 1),
    (N'uiStrings.importCounts',          N'en', N'added {added}, updated {updated}, unchanged {unchanged}', 1),
    (N'uiStrings.importBlockedHint',     N'en', N'Nothing has been written: fix the rows listed below and pick the file again.', 1),
    (N'uiStrings.importReady',           N'en', N'The file is valid: nothing to fix.', 1),

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
    (N'mapping.noData',                  N'en', N'no rows in the source', 1),

    -- Заведення мапінгу поля джерела (Прогалина 1 директиви паритету зі
    -- старою системою): до цих рядків жоден екран не мав способу завести
    -- мапінг інакше, ніж ручним SQL.
    (N'mapping.create',                  N'en', N'Add mapping', 1),
    (N'mapping.createTitle',             N'en', N'Add a source field mapping', 1),
    (N'mapping.createField',             N'en', N'Source field or tag', 1),
    (N'mapping.createKind',              N'en', N'Target', 1),
    (N'mapping.createKindColumn',        N'en', N'Document column', 1),
    (N'mapping.createKindRegistry',      N'en', N'Registry field', 1),
    (N'mapping.createTargetId',          N'en', N'Target identifier', 1),
    (N'mapping.createRowKey',            N'en', N'Target row key', 1),
    (N'mapping.createRowKeyHint',        N'en', N'Leave empty to keep points raw for reconciliation (D-118) instead of landing them in a cell.', 1),
    (N'mapping.createAggregation',       N'en', N'Fold period points as', 1),
    (N'mapping.createSubmit',            N'en', N'Save mapping', 1),
    (N'mapping.created',                 N'en', N'The mapping has been created.', 1),
    -- Призупинений мапінг (isActive = false): видно, але не пише і не
    -- лічиться діючим.
    (N'mapping.paused',                  N'en', N'paused', 1),
    (N'mapping.mapsSummary',             N'en', N'{active} active, {paused} paused. A paused mapping keeps its settings and writes nothing.', 1),
    -- Призупинення й відновлення мапінгу з перегляду (BE-27).
    (N'mapping.pause',                   N'en', N'Pause', 1),
    (N'mapping.resume',                  N'en', N'Resume', 1),
    (N'mapping.pauseDone',               N'en', N'Mapping paused.', 1),
    (N'mapping.resumeDone',              N'en', N'Mapping resumed.', 1),
    -- Видалення мапінгу з рядка перегляду (BE-27). ⚠ `deleteBlocked` — відмова
    -- сервера, коли мапінг уже пояснює зібрані точки: видалити означало б
    -- лишити їх без пояснення, тож пропонується пауза.
    (N'mapping.delete',                  N'en', N'Remove mapping', 1),
    (N'mapping.deleteTitle',             N'en', N'Remove the mapping of {field}?', 1),
    (N'mapping.deleteText',              N'en', N'The mapping is gone for good: its target row, fold and units are not kept anywhere else.', 1),
    (N'mapping.deleteConsequence',       N'en', N'{target} stops being filled from the source; people type it by hand again.', 1),
    (N'mapping.deleteNote',              N'en', N'The value already in the cell stays. The source field returns to the list of fields that land nowhere.', 1),
    (N'mapping.deleteDone',              N'en', N'Mapping removed.', 1),
    (N'mapping.deleteBlocked',           N'en', N'{points} collected rows are explained by this mapping, so it is not removed: deleting it would leave them without an explanation. Pause it instead — a paused mapping keeps its settings and writes nothing.', 1),
    -- Зміна одиниці джерела (ФВ-16.9): збір цього мапінгу на паузі, доки
    -- людина не вирішить, чи нова одиниця правильна.
    (N'mapping.unitChangeTitle',         N'en', N'Source unit changed', 1),
    (N'mapping.unitChangeBanner',        N'en', N'The source now returns {actualUnitCode} instead of {expectedUnitCode}.', 1),
    (N'mapping.unitChangeDetected',      N'en', N'Detected', 1),
    (N'mapping.unitChangeAccept',        N'en', N'Yes, accept {actualUnitCode}', 1),
    (N'mapping.unitChangeDecline',       N'en', N'No, this is a source error', 1),
    (N'mapping.unitChangeAccepted',      N'en', N'Collection resumed', 1),
    (N'mapping.unitChangeGoToUnits',     N'en', N'Add the unit in the catalog first', 1),
    -- Вибір імені з каталогу джерела PI AF (ФВ-13.13). ⚠ Каталог — допомога, а
    -- не умова: коли джерело мовчить, шлях можна ввести руками, і підказка
    -- відмови каже саме це.
    (N'mapping.catalogOpen',             N'en', N'Pick from catalog', 1),
    (N'mapping.catalogTitle',            N'en', N'Source catalog', 1),
    (N'mapping.catalogHint',             N'en', N'Elements expand; an attribute is what a mapping points at.', 1),
    (N'mapping.catalogSearch',           N'en', N'Search by name or description', 1),
    (N'mapping.catalogSearchApply',      N'en', N'Search', 1),
    (N'mapping.catalogSearchClear',      N'en', N'Clear search', 1),
    (N'mapping.catalogExpand',           N'en', N'Expand', 1),
    (N'mapping.catalogCollapse',         N'en', N'Collapse', 1),
    (N'mapping.catalogMore',             N'en', N'Show more', 1),
    (N'mapping.catalogElement',          N'en', N'Element', 1),
    (N'mapping.catalogAttribute',        N'en', N'Attribute', 1),
    (N'mapping.catalogEmpty',            N'en', N'The data source returned no items for this level.', 1),
    (N'mapping.catalogEmptyHint',        N'en', N'Check that the connection points at the right AF database, or search by name.', 1),
    (N'mapping.catalogSearchEmpty',      N'en', N'Nothing in this level matches the search.', 1),
    (N'mapping.catalogUnavailable',      N'en', N'The data source is not responding', 1),
    (N'mapping.catalogUnavailableHint',  N'en', N'The catalog could not be read. The rest of the form still works: type the source path by hand, or try again.', 1),
    -- Пробний запуск шляху мапінгу до першого збору (ФВ-13.17). ⚠ «Шлях є, а
    -- даних немає» — окремий випадок, не помилка: мапінг збережеться, але
    -- поки нічого не принесе.
    (N'mapping.probeAction',             N'en', N'Test path', 1),
    (N'mapping.probeNoData',             N'en', N'The path exists in the catalog, but there is no data in the last 30 days.', 1),
    (N'mapping.probeUnavailableHint',    N'en', N'The check could not run. The rest of the form still works: type the source path by hand, or try again.', 1),
    (N'mapping.probeSuggestionsHint',    N'en', N'Similar names in the catalog:', 1),
    -- Редактор зв'язків між таблицями (ФВ-2.12, ФВ-2.13)
    (N'version.relations',               N'en', N'Table relations', 1),
    (N'tables.relationsTitle',           N'en', N'Table relations', 1),
    (N'tables.optionalHint',             N'en', N'Relations are optional: a template with none simply has independent tables, and that is a valid design.', 1),
    (N'tables.newRelation',              N'en', N'New relation', 1),
    (N'tables.relationForm',             N'en', N'Relation', 1),
    (N'tables.noRelations',              N'en', N'This version has no table relations', 1),
    (N'tables.noRelationsHint',          N'en', N'Add one when a table must take its numbers from another; otherwise leave it empty.', 1),
    (N'tables.readOnly',                 N'en', N'Published version: relations are frozen', 1),
    (N'tables.readOnlyHint',             N'en', N'A relation decides where a table takes its numbers from, so changing it would silently change forms already submitted. Clone the version to change it.', 1),
    (N'tables.relationCode',             N'en', N'Code', 1),
    (N'tables.relationCodeHint',         N'en', N'The address of the relation in the API. It cannot be renamed later.', 1),
    (N'tables.relationKind',             N'en', N'Kind', 1),
    (N'tables.relationKindHint',         N'en', N'What the relation does with the rows it matches.', 1),
    (N'tables.sourceTable',              N'en', N'Source table', 1),
    (N'tables.sourceTableHint',          N'en', N'Where the values come from.', 1),
    (N'tables.targetTable',              N'en', N'Target table', 1),
    (N'tables.targetTableHint',          N'en', N'Where they go. It cannot be the source table.', 1),
    (N'tables.pickTable',                N'en', N'— pick a table —', 1),
    (N'tables.matchJson',                N'en', N'Row matching', 1),
    (N'tables.matchJsonHint',            N'en', N'How a source row is paired with a target row. Without it the relation looks configured and joins nothing.', 1),
    (N'tables.mapJson',                  N'en', N'Column mapping', 1),
    (N'tables.mapJsonHint',              N'en', N'Which column goes into which. Leave empty when the relation carries no values.', 1),
    -- ⛔ Аудит-пас 8, lane7, п.12: `Check` — «a cross-table rule, no values
    -- move» (`tables.kindCheck`) — поле вимкнене й пояснюється цим рядком
    -- замість звичайної підказки `tables.mapJsonHint`.
    (N'tables.mapJsonNotApplicableForCheck', N'en', N'Not applicable: Check is a cross-table rule and carries no values between tables.', 1),
    (N'tables.onSourceChange',           N'en', N'When the source changes', 1),
    (N'tables.onSourceChangeHint',       N'en', N'What happens to the target once a source row is edited.', 1),
    (N'tables.onChangeRecalc',           N'en', N'recalculate the target', 1),
    (N'tables.onChangeWarn',             N'en', N'warn, but accept the edit', 1),
    (N'tables.onChangeBlock',            N'en', N'block the edit', 1),
    (N'tables.kindMirror',               N'en', N'Mirror — the target repeats the source', 1),
    (N'tables.kindRollup',               N'en', N'Rollup — the target aggregates the source', 1),
    (N'tables.kindReference',            N'en', N'Reference — the target points at a source row', 1),
    (N'tables.kindCascade',              N'en', N'Cascade — a change in the source propagates', 1),
    (N'tables.kindCheck',                N'en', N'Check — a cross-table rule, no values move', 1),
    (N'tables.kindCopy',                 N'en', N'Copy — values are materialised in the target', 1),
    (N'tables.isActive',                 N'en', N'The relation is in force', 1),
    (N'tables.isActiveHint',             N'en', N'Switching it off keeps the relation in the structure but stops it from acting.', 1),
    (N'tables.inactive',                 N'en', N'off', 1),
    (N'tables.editRelation',             N'en', N'Edit', 1),
    (N'tables.deleteRelation',           N'en', N'Remove', 1),
    (N'tables.saveRelation',             N'en', N'Save relation', 1),
    (N'tables.relationSaved',            N'en', N'The relation has been saved.', 1),
    (N'tables.relationDeleted',          N'en', N'The relation has been removed.', 1),
    (N'tables.errCode',                  N'en', N'Give the relation a code: it is how the relation is addressed.', 1),
    -- ⛔ Q-338: та сама причина, що `tableDef.errCodeInvalid` (Q-336) — код
    -- зв'язку тепер теж перевіряється на формат до мережевого запиту.
    (N'tables.errCodeInvalid',           N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'tables.errSource',                N'en', N'Pick the source table.', 1),
    (N'tables.errTarget',                N'en', N'Pick the target table.', 1),
    (N'tables.errSelf',                  N'en', N'Source and target must differ: a table cannot be related to itself.', 1),
    (N'tables.errMatch',                 N'en', N'Describe how rows are matched, otherwise the relation joins nothing.', 1),
    -- ⛔ UI-аудит, lane 7 (Q-337): форма приймала геть будь-який текст у "Row
    -- matching" без жодного натяку, що це не той JSON-об'єкт, на який
    -- зважає сервер (TableRelationDef.Apply, ECR-TMPL-0422).
    (N'tables.errMatchSyntax',           N'en', N'Row matching must be a valid JSON object, for example {} for every row.', 1),
    -- Редактор аркушів (ФВ-2.1) — перший вертикальний зріз авторства структури.
    (N'sheets.add',                      N'en', N'Add sheet', 1),
    (N'sheets.edit',                     N'en', N'Edit', 1),
    (N'sheets.delete',                   N'en', N'Remove', 1),
    (N'sheets.save',                     N'en', N'Save sheet', 1),
    (N'sheets.saved',                    N'en', N'The sheet has been saved.', 1),
    (N'sheets.deleted',                  N'en', N'The sheet has been removed.', 1),
    (N'sheets.code',                     N'en', N'Code', 1),
    (N'sheets.codeHint',                 N'en', N'The address of the sheet in the API. It cannot be renamed later.', 1),
    (N'sheets.name',                     N'en', N'Name', 1),
    (N'sheets.nameHint',                 N'en', N'Shown to the person filling in the form.', 1),
    (N'sheets.group',                    N'en', N'Group', 1),
    (N'sheets.groupHint',                N'en', N'Used by document composition rules; leave empty when the sheet belongs to no group.', 1),
    (N'sheets.ordinal',                  N'en', N'Order', 1),
    (N'sheets.ordinalHint',              N'en', N'Display order only — not an identity; nothing refers to it.', 1),
    (N'sheets.mandatory',                N'en', N'Mandatory', 1),
    (N'sheets.mandatoryHint',            N'en', N'Required for the document to be considered complete.', 1),
    (N'sheets.visible',                  N'en', N'Visible', 1),
    (N'sheets.visibleHint',              N'en', N'Hidden sheets stay in the structure but do not show on the form.', 1),
    (N'sheets.errCode',                  N'en', N'Give the sheet a code: it is how the sheet is addressed.', 1),
    -- ⛔ Q-338: та сама причина, що `tableDef.errCodeInvalid` (Q-336).
    (N'sheets.errCodeInvalid',           N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'sheets.errName',                  N'en', N'Give the sheet a name in at least one language.', 1),
    -- Редактор таблиць (W5.1) — другий вертикальний зріз авторства структури,
    -- той самий патерн, що й аркуші вище.
    (N'tableDef.add',                    N'en', N'Add table', 1),
    (N'tableDef.edit',                   N'en', N'Edit', 1),
    (N'tableDef.delete',                 N'en', N'Remove', 1),
    (N'tableDef.save',                   N'en', N'Save table', 1),
    (N'tableDef.saved',                  N'en', N'The table has been saved.', 1),
    (N'tableDef.deleted',                N'en', N'The table has been removed.', 1),
    (N'tableDef.code',                   N'en', N'Code', 1),
    (N'tableDef.codeHint',               N'en', N'The address of the table in the API. It cannot be renamed later.', 1),
    (N'tableDef.name',                   N'en', N'Name', 1),
    (N'tableDef.nameHint',               N'en', N'Shown to the person filling in the form.', 1),
    (N'tableDef.layoutKind',             N'en', N'Layout', 1),
    (N'tableDef.layoutKindHint',         N'en', N'How periods lay out across the table structure.', 1),
    (N'tableDef.rowMode',                N'en', N'Rows', 1),
    (N'tableDef.rowModeHint',            N'en', N'Whether rows come from the template or the person filling in the form adds them.', 1),
    (N'tableDef.maxDynamicRows',         N'en', N'Row limit', 1),
    (N'tableDef.maxDynamicRowsHint',     N'en', N'Caps how many rows a person can add; leave empty for no limit.', 1),
    (N'tableDef.layoutMonthsInColumns',  N'en', N'Months in columns', 1),
    (N'tableDef.layoutMonthsInRows',     N'en', N'Months in rows', 1),
    (N'tableDef.layoutStatic',           N'en', N'Static (independent of period)', 1),
    (N'tableDef.layoutPerPeriodInstance', N'en', N'Separate instance per period', 1),
    (N'tableDef.rowModeFixed',           N'en', N'Fixed by the template', 1),
    (N'tableDef.rowModeDynamic',         N'en', N'Added by the person filling in the form', 1),
    (N'tableDef.rowModeMixed',           N'en', N'Fixed plus rows the person adds', 1),
    (N'tableDef.errCode',                N'en', N'Give the table a code: it is how the table is addressed.', 1),
    -- ⛔ Аудит-пас 8, lane7, п.10: причина ІНША, ніж «поле порожнє» —
    -- повідомлення теж має бути іншим (`TableBlocker.CodeInvalid`).
    (N'tableDef.errCodeInvalid',         N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'tableDef.errName',                N'en', N'Give the table a name in at least one language.', 1),
    -- Редактор колонок (W5.2) — третій і четвертий вертикальні зрізи авторства структури.
    (N'columns.add',                     N'en', N'Add column', 1),
    (N'columns.edit',                    N'en', N'Edit', 1),
    (N'columns.delete',                  N'en', N'Remove', 1),
    (N'columns.save',                    N'en', N'Save column', 1),
    (N'columns.saved',                   N'en', N'The column has been saved.', 1),
    (N'columns.deleted',                 N'en', N'The column has been removed.', 1),
    (N'columns.code',                    N'en', N'Code', 1),
    (N'columns.codeHint',                N'en', N'The address of the column in the API. It cannot be renamed later.', 1),
    (N'columns.header',                  N'en', N'Header', 1),
    (N'columns.headerHint',              N'en', N'Shown to the person filling in the form.', 1),
    (N'columns.dataType',                N'en', N'Data type', 1),
    (N'columns.dataTypeHint',            N'en', N'Fixed once the column is created: changing it would reinterpret values already entered.', 1),
    (N'columns.ordinal',                 N'en', N'Order', 1),
    (N'columns.ordinalHint',             N'en', N'Display order only — not an identity; nothing refers to it.', 1),
    (N'columns.required',                N'en', N'Required', 1),
    (N'columns.readOnly',                N'en', N'Read-only', 1),
    (N'columns.hidden',                  N'en', N'Hidden', 1),
    (N'columns.hiddenHint',              N'en', N'Hidden columns stay in the structure but do not show on the form.', 1),
    (N'columns.displayFormat',           N'en', N'Display format', 1),
    (N'columns.displayFormatHint',       N'en', N'How the value is formatted on screen; leave empty for the default.', 1),
    (N'columns.defaultValue',            N'en', N'Default value', 1),
    (N'columns.defaultValueHint',        N'en', N'Used to fill an empty cell; leave empty when there is none.', 1),
    (N'columns.precision',               N'en', N'Precision', 1),
    (N'columns.scale',                   N'en', N'Scale', 1),
    (N'columns.lookupRegistryDefId',     N'en', N'Registry', 1),
    -- ⛔ Директива registry-lookup, PR A3: раніше автор шаблону мав уводити
    -- сирий числовий ідентифікатор довідника напам'ять; тепер це вибір зі
    -- списку за назвою й кодом.
    (N'columns.lookupRegistryDefIdHint', N'en', N'The registry this column looks values up from.', 1),
    (N'columns.lookupRegistryDefIdEmpty', N'en', N'No registries found', 1),
    -- ⛔ Директива registry-lookup / cell-style, PR B1: `styleId` приєднався
    -- до того самого класу полів, що `precision`/`lookup`/`unit` уже мали —
    -- `TemplateColumnDto` (GET …/structure) його теж не несе (`D-137`), тож
    -- редагування колонки без кешу цього сеансу так само стерло б стиль.
    (N'columns.errCode',                 N'en', N'Give the column a code: it is how the column is addressed.', 1),
    -- ⛔ Q-338: та сама причина, що `tableDef.errCodeInvalid` (Q-336).
    (N'columns.errCodeInvalid',          N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'columns.errHeader',               N'en', N'Give the column a header in at least one language.', 1),
    (N'columns.errScale',                N'en', N'Scale cannot exceed precision.', 1),

    -- Поля шапки версії шаблону (header-fields). Редактор — 1:1 зразок
    -- редактора колонки вище, тому й тексти ті самі за змістом.
    -- ⚠ Видалення поля шапки в контракті НЕМАЄ, тож ключа під нього теж нема:
    -- порядок і обов'язковість змінюються правкою, а не стиранням.
    (N'headerFields.title',              N'en', N'Header fields', 1),
    (N'headerFields.add',                N'en', N'Add header field', 1),
    (N'headerFields.edit',               N'en', N'Edit', 1),
    (N'headerFields.code',               N'en', N'Code', 1),
    (N'headerFields.codeHint',           N'en', N'The address of the field in the API. It cannot be renamed later.', 1),
    (N'headerFields.label',              N'en', N'Label', 1),
    (N'headerFields.labelHint',          N'en', N'Shown to the person filling in the form.', 1),
    (N'headerFields.dataType',           N'en', N'Data type', 1),
    (N'headerFields.dataTypeHint',       N'en', N'Fixed once the field is created: changing it would reinterpret values already entered.', 1),
    (N'headerFields.ordinal',            N'en', N'Order', 1),
    (N'headerFields.ordinalHint',        N'en', N'Display order only — not an identity; nothing refers to it.', 1),
    (N'headerFields.required',           N'en', N'Required', 1),
    (N'headerFields.lookupRegistryDefId', N'en', N'Registry', 1),
    (N'headerFields.lookupRegistryDefIdHint', N'en', N'The registry this field looks values up from.', 1),
    (N'headerFields.lookupRegistryDefIdEmpty', N'en', N'No registries found', 1),
    (N'headerFields.empty',              N'en', N'No header fields yet.', 1),
    (N'headerFields.save',               N'en', N'Save field', 1),
    (N'headerFields.saved',              N'en', N'The header field has been saved.', 1),
    (N'headerFields.errCode',            N'en', N'Give the field a code: it is how the field is addressed.', 1),
    (N'headerFields.errCodeInvalid',     N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'headerFields.errLabel',           N'en', N'Give the field a label in at least one language.', 1),
    (N'headerFields.errLookupRequired',  N'en', N'Pick a registry for a Lookup field.', 1),

    -- ⛔ Обидві причини доти падали в `default` у `blockerLabel` і показувалися
    -- ГОЛИМ кодом (`StyleCode`, `StyleFontSize`) — тобто людина бачила слово з
    -- переліку розробника замість речення про те, що саме виправити.
    (N'columns.errStyleCode',            N'en', N'Give the style a code: Latin letters, digits and underscores, starting with a letter.', 1),
    (N'columns.errStyleFontSize',        N'en', N'Font size must be a number.', 1),

    -- Набір (`KIT.md`): підписи дій, яких компоненти НЕ вигадують самі.
    -- ⛔ `FilterBar` і `DataTable` навмисно не кличуть `t()` на ці ключі, а
    -- беруть їх пропом (`clearLabel`, `showMoreLabel`): виклик ключа, якого
    -- немає в каталозі, показав би `⟦filters.clear⟧` на кожному екрані, що
    -- взяв набір. Тому рядки заводяться ПЕРШИМИ, а екрани переходять на набір
    -- уже потім.
    (N'filters.clear',                   N'en', N'Clear', 1),
    (N'list.showMore',                   N'en', N'Show more', 1),

    -- ⛔ Директива registry-lookup / cell-style, PR B1: раніше жоден екран не
    -- давав автору шаблону задати StyleDef колонки — стиль долітав лише до
    -- Excel-експорту (`StyleMapper.cs`), заведеного в базу лише seed-ом.
    (N'columns.customStyle',             N'en', N'Custom style', 1),
    (N'columns.customStyleHint',         N'en', N'Set once when building the template; the person filling in the form only sees it.', 1),
    (N'styles.legend',                   N'en', N'Style', 1),
    (N'styles.code',                     N'en', N'Style code', 1),
    (N'styles.codeHint',                 N'en', N'Unique within this template version; used to reference this style.', 1),
    (N'styles.errCode',                  N'en', N'Give the style a code.', 1),
    (N'styles.errCodeInvalid',           N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'styles.bold',                     N'en', N'Bold', 1),
    (N'styles.italic',                   N'en', N'Italic', 1),
    (N'styles.wrapText',                 N'en', N'Wrap text', 1),
    (N'styles.fontName',                 N'en', N'Font name', 1),
    (N'styles.fontNameHint',             N'en', N'Leave empty for the workbook theme font.', 1),
    (N'styles.fontSize',                 N'en', N'Font size', 1),
    (N'styles.foreground',               N'en', N'Text color', 1),
    (N'styles.background',               N'en', N'Fill color', 1),
    (N'styles.horizontalAlign',          N'en', N'Horizontal align', 1),
    (N'styles.verticalAlign',            N'en', N'Vertical align', 1),
    (N'styles.alignLeft',                N'en', N'Left', 1),
    (N'styles.alignCenter',              N'en', N'Center', 1),
    (N'styles.alignRight',               N'en', N'Right', 1),
    (N'styles.alignJustify',             N'en', N'Justify', 1),
    (N'styles.alignTop',                 N'en', N'Top', 1),
    (N'styles.alignMiddle',              N'en', N'Middle', 1),
    (N'styles.alignBottom',              N'en', N'Bottom', 1),
    (N'styles.borderLegend',             N'en', N'Border', 1),
    (N'styles.borderTop',                N'en', N'Top', 1),
    (N'styles.borderRight',              N'en', N'Right', 1),
    (N'styles.borderBottom',             N'en', N'Bottom', 1),
    (N'styles.borderLeft',               N'en', N'Left', 1),
    (N'styles.borderNone',               N'en', N'None', 1),
    (N'styles.borderThin',               N'en', N'Thin', 1),
    (N'styles.borderMedium',             N'en', N'Medium', 1),
    (N'styles.borderThick',              N'en', N'Thick', 1),
    (N'styles.numberFormat',             N'en', N'Number format', 1),
    (N'styles.numberFormatHint',         N'en', N'Excel number format, e.g. 0.00; leave empty for the theme default.', 1),
    -- Редактор рядків фіксованої таблиці (W5.2) — третій вертикальний зріз.
    (N'rows.title',                      N'en', N'Rows', 1),
    (N'rows.add',                        N'en', N'Add row', 1),
    (N'rows.edit',                       N'en', N'Edit', 1),
    (N'rows.delete',                     N'en', N'Remove', 1),
    (N'rows.save',                       N'en', N'Save row', 1),
    (N'rows.saved',                      N'en', N'The row has been saved.', 1),
    (N'rows.deleted',                    N'en', N'The row has been removed.', 1),
    (N'rows.empty',                      N'en', N'This table has no rows yet.', 1),
    (N'rows.rowKey',                     N'en', N'Row key', 1),
    (N'rows.rowKeyHint',                 N'en', N'The stable identity of the row in the API. It cannot be renamed later.', 1),
    (N'rows.label',                      N'en', N'Label', 1),
    (N'rows.labelHint',                  N'en', N'Shown to the person filling in the form.', 1),
    (N'rows.rowKind',                    N'en', N'Kind', 1),
    (N'rows.rowKindHint',                N'en', N'Fixed once the row is created: it decides how the row behaves in the report.', 1),
    (N'rows.ordinal',                    N'en', N'Order', 1),
    (N'rows.ordinalHint',                N'en', N'Display order only — not an identity; nothing refers to it.', 1),
    (N'rows.parentRowKey',               N'en', N'Parent row', 1),
    (N'rows.parentRowKeyHint',           N'en', N'Key of the parent row in this table; leave empty for a top-level row.', 1),
    (N'rows.readOnly',                   N'en', N'Read-only', 1),
    (N'rows.readOnlyHint',               N'en', N'For example, a balance row filled in by the system, not by a person.', 1),
    (N'rows.partialLabelWarning',        N'en', N'The label is shown here in one language only. Saving will clear the other languages unless you already edited this row in this session.', 1),
    (N'rows.errRowKey',                  N'en', N'Give the row a key: it is how the row is addressed.', 1),
    (N'rows.errLabel',                   N'en', N'Give the row a label in at least one language.', 1),
    -- Редактор формул колонки чи рядка (W5.3) — наступний зріз авторства структури.
    (N'formulas.edit',                   N'en', N'Formula', 1),
    (N'formulas.save',                   N'en', N'Save formula', 1),
    (N'formulas.saved',                  N'en', N'The formula has been saved.', 1),
    (N'formulas.expression',             N'en', N'Formula expression', 1),
    (N'formulas.targetColumn',           N'en', N'Formula for this column.', 1),
    (N'formulas.targetRow',              N'en', N'Formula for this row.', 1),
    (N'formulas.errExpression',          N'en', N'Write the expression the formula should compute.', 1),
    -- Редактор правил валідації таблиці (W5.4, продовження ФВ-2.1 на ValidationRule).
    (N'validationRules.title',           N'en', N'Validation rules', 1),
    (N'validationRules.saved',           N'en', N'The rule has been saved.', 1),
    (N'validationRules.deleted',         N'en', N'The rule has been removed.', 1),
    (N'validationRules.code',            N'en', N'Code', 1),
    (N'validationRules.codeHint',        N'en', N'The address of the rule within this table. It cannot be renamed later.', 1),
    (N'validationRules.severity',        N'en', N'Severity', 1),
    (N'validationRules.severityHint',    N'en', N'Only a cell-level Error blocks saving.', 1),
    (N'validationRules.scope',           N'en', N'Scope', 1),
    (N'validationRules.scopeHint',       N'en', N'Which level the rule evaluates against.', 1),
    (N'validationRules.columnDefId',     N'en', N'Column id', 1),
    (N'validationRules.columnDefIdHint', N'en', N'Leave empty to apply the rule to every column of the table.', 1),
    (N'validationRules.expression',      N'en', N'Expression', 1),
    (N'validationRules.expressionHint',  N'en', N'The predicate in the template expression language.', 1),
    (N'validationRules.message',         N'en', N'Message', 1),
    (N'validationRules.messageHint',     N'en', N'Shown to the person filling in the form when the rule fires.', 1),
    (N'validationRules.isActive',        N'en', N'Active', 1),
    (N'validationRules.isActiveHint',    N'en', N'An inactive rule stays in the structure but never fires.', 1),
    (N'validationRules.save',            N'en', N'Save rule', 1),
    (N'validationRules.delete',          N'en', N'Remove rule', 1),
    (N'validationRules.errCode',         N'en', N'Give the rule a code: it is how the rule is addressed.', 1),
    -- ⛔ Q-338: та сама причина, що `tableDef.errCodeInvalid` (Q-336).
    (N'validationRules.errCodeInvalid',  N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'validationRules.errExpression',   N'en', N'Give the rule an expression, otherwise it checks nothing.', 1),
    (N'validationRules.errMessage',      N'en', N'Give the rule a message in at least one language.', 1),
    -- Редактор правил доступу до періоду (ФВ-2.15, W5.4).
    (N'periodRules.title',               N'en', N'Period access rules', 1),
    (N'periodRules.kind',                N'en', N'Rule kind', 1),
    (N'periodRules.kindHint',            N'en', N'Fixed once the rule is created; recreate to change it.', 1),
    (N'periodRules.outOfWindow',         N'en', N'Outside the window', 1),
    (N'periodRules.outOfWindowHint',     N'en', N'What happens to an edit made outside the allowed window.', 1),
    (N'periodRules.sheetDefId',          N'en', N'Sheet id', 1),
    (N'periodRules.sheetDefIdHint',      N'en', N'Leave empty when the rule is not scoped to a sheet.', 1),
    (N'periodRules.tableDefId',          N'en', N'Table id', 1),
    (N'periodRules.tableDefIdHint',      N'en', N'Leave empty when the rule is not scoped to a table.', 1),
    (N'periodRules.roleId',              N'en', N'Role id', 1),
    (N'periodRules.roleIdHint',          N'en', N'Leave empty for the rule to apply to every role.', 1),
    (N'periodRules.rowKind',             N'en', N'Row kind', 1),
    (N'periodRules.rowKindHint',         N'en', N'Leave empty for the rule to apply to every row kind.', 1),
    (N'periodRules.rowKindAny',          N'en', N'— any —', 1),
    (N'periodRules.fromSequence',        N'en', N'From period', 1),
    (N'periodRules.toSequence',          N'en', N'To period', 1),
    (N'periodRules.sourceColumnDefId',   N'en', N'Source column id', 1),
    (N'periodRules.sourceColumnDefIdHint', N'en', N'The lookup column whose record supplies the access window.', 1),
    (N'periodRules.relativeOffset',      N'en', N'Offset (periods)', 1),
    (N'periodRules.relativeOffsetHint',  N'en', N'Positive number of periods around the current one.', 1),
    (N'periodRules.condition',           N'en', N'Condition', 1),
    (N'periodRules.conditionHint',       N'en', N'Boolean expression in the template language.', 1),
    (N'periodRules.add',                 N'en', N'Add rule', 1),
    (N'periodRules.added',               N'en', N'The rule has been created.', 1),
    (N'periodRules.errTarget',           N'en', N'Give the rule a sheet or a table — otherwise it applies nowhere.', 1),
    (N'periodRules.errSourceColumn',     N'en', N'A source-window rule needs a source column id.', 1),
    (N'periodRules.errRelativeOffset',   N'en', N'A relative-window rule needs a positive offset.', 1),
    (N'periodRules.errCondition',        N'en', N'An expression rule needs a condition.', 1),
    (N'periodRules.manage',              N'en', N'Edit or remove an existing rule', 1),
    (N'periodRules.manageId',            N'en', N'Rule id', 1),
    (N'periodRules.manageIdHint',        N'en', N'Shown after creating a rule above, or found in the structure change log.', 1),
    (N'periodRules.save',                N'en', N'Save rule', 1),
    (N'periodRules.saved',               N'en', N'The rule has been saved.', 1),
    (N'periodRules.delete',              N'en', N'Remove rule', 1),
    (N'periodRules.deleted',             N'en', N'The rule has been removed.', 1),

    (N'nav.notFound.title',              N'en', N'Page not found', 1),
    (N'nav.notFound.hint',               N'en', N'This address does not match any screen in this system.', 1),

    -- Синтаксис виразів (`Q-303`): раніше конфігуратор показував ГОТОВЕ
    -- українське речення з `ExpressionDiagnostic.Message` незалежно від мови
    -- інтерфейсу — той самий клас дефекту, що й `err.*` до директиви ФВ-14.9a,
    -- лише в іншому конвеєрі (`Ecr.Expressions` — окрема бібліотека без
    -- доступу до каталогу, тому резолвить не сервер, а клієнт за ключем).
    -- Приватна область: редактор виразів вимагає входу.
    -- ⚠ Символи беруться в ПОДВІЙНІ лапки, не одинарні: одинарна лапка в
    -- рядковому літералі T-SQL мусить подвоюватись, і ручне дублювання поруч
    -- із плейсхолдерами `{name}` реально зламало цей самий блок при першій
    -- спробі (SqlException «Incorrect syntax near ']'», знайдено живим
    -- запуском застосунку проти щойно розгорнутої бази, не оглядом коду) —
    -- подвійні лапки цієї проблеми не мають взагалі.
    (N'expr.lex.unmatchedCloseBracket',        N'en', N'Closing bracket "]" without a matching "[".', 1),
    (N'expr.lex.unclosedBracket',              N'en', N'Unclosed square bracket.', 1),
    (N'expr.lex.unclosedString',               N'en', N'Unclosed string literal.', 1),
    (N'expr.lex.unclosedPlaceholder',          N'en', N'Unclosed placeholder "{".', 1),
    (N'expr.lex.identifierStartsWithDigit',    N'en', N'An identifier cannot start with a digit (R-B6).', 1),
    (N'expr.lex.invalidNumber',                N'en', N'Invalid number "{value}".', 1),
    (N'expr.lex.invalidCharacter',             N'en', N'Invalid character "{value}".', 1),
    (N'expr.trailingText',                     N'en', N'Unexpected text after the end of the expression: "{text}".', 1),
    (N'expr.expectedColonInTernary',           N'en', N'Expected ":" in the ternary operator.', 1),
    (N'expr.caretNotPower',                    N'en', N'"^" in the methodology dialect does not mean exponentiation: it is bitwise XOR, and "2^3" equals 1, not 8. Use Pow(a, b).', 1),
    (N'expr.expectedCloseParen',               N'en', N'Expected ")".', 1),
    (N'expr.unexpectedToken',                  N'en', N'Unexpected token "{token}".', 1),
    (N'expr.expectedNameAfterPrefix',          N'en', N'Expected a name after "{prefix}".', 1),
    (N'expr.methodologyConstructInTemplate',   N'en', N'The construct "{construct}" belongs to the methodology dialect and is not allowed in template formulas.', 1),
    (N'expr.bareNameNeedsAt',                  N'en', N'"{name}" is a bare name: in the methodology dialect a parameter is written with "@" ("@{name}"). Without the prefix, a name cannot be told apart from a typo in a function name.', 1),
    (N'expr.unknownIdentifier',                N'en', N'Unknown identifier "{name}". A cell reference is written in square brackets.', 1),
    (N'expr.expectedCloseParenInCall',         N'en', N'Expected ")" in the call to {name}.', 1),
    (N'expr.unknownFunction',                  N'en', N'Function "{name}" is not available in the {dialect} dialect.', 1),
    (N'expr.argCountAtLeast',                  N'en', N'Function "{name}" takes at least {min} argument(s), but received {actual}.', 1),
    (N'expr.argCountExact',                    N'en', N'Function "{name}" takes {count} argument(s), but received {actual}.', 1),
    (N'expr.argCountRange',                    N'en', N'Function "{name}" takes from {min} to {max} argument(s), but received {actual}.', 1),
    (N'expr.calendarContextSyntax',            N'en', N'The calendar context is written as "[Period].Property".', 1),
    (N'expr.cellReferencesForbiddenInMethodology', N'en', N'Cell references to the document are not allowed in the methodology dialect.', 1),
    (N'expr.referenceLinkCount',               N'en', N'A reference has from one to four links.', 1),
    (N'expr.referenceLastLinkColumn',          N'en', N'The last link of a reference is a column; a range is not allowed there.', 1),
    (N'expr.expectedCloseBracketAfterPredicate', N'en', N'Expected "]" after the predicate.', 1),
    (N'expr.referenceLinkEmpty',               N'en', N'Empty or invalid reference link.', 1),
    (N'expr.periodExpectedNumber',             N'en', N'A number was expected after "[Period:".', 1),
    (N'expr.expectedCloseBracket',             N'en', N'Expected "]".', 1),
    (N'expr.rowKeyExpected',                   N'en', N'A row key was expected after ":".', 1),
    (N'expr.expectedCloseBracketAfterRange',   N'en', N'Expected "]" after the range.', 1),
    -- Межа глибини вкладеності. ⚠ Рядок каталогу тут такий самий звичайний, як
    -- у решти синтаксичних відмов, і це суть фіксу: перевищення глибини — це
    -- ПОВІДОМЛЕННЯ в редакторі формул, а не смерть процесу від
    -- StackOverflowException, якого в .NET не перехоплює жоден catch.
    (N'expr.nestingTooDeep',                   N'en', N'The expression is nested deeper than {max} levels.', 1),
    -- V-03: дві формули в одну комірку. Адреси — у нотації мови виразів
    -- (`RTOT·*`, `*·CFRM`, `RTOT·CFRM`), а не словами.
    (N'expr.publish.formulaTargetConflict',    N'en', N'Formulas {first} and {second} both calculate cell {cell} in table "{table}": a cell can be calculated by only one formula. Remove one of the two formulas.', 1),
    -- V-19: зауваження резолвера посилань (`ReferenceResolver`, `RangeExpander`,
    -- `DependencyExtractor`) — раніше лише українським реченням без ключа.
    (N'expr.ref.unknownColumn',                N'en', N'Column "{column}" does not exist in table "{table}".', 1),
    (N'expr.ref.unknownRow',                   N'en', N'Row "{row}" does not exist in table "{table}".', 1),
    (N'expr.ref.unknownTable',                 N'en', N'Table "{sheet}.{table}" does not exist in this template version.', 1),
    (N'expr.ref.unknownHeaderField',           N'en', N'Header field "{name}" does not exist in this template version.', 1),
    (N'expr.ref.ownTableMissing',              N'en', N'Table {tableDefId} that owns the expression is not in the template structure.', 1),
    (N'expr.ref.rowKeyInDynamicTable',         N'en', N'Table "{table}" is dynamic: a specific row ("{row}") cannot be referenced, only a predicate.', 1),
    (N'expr.ref.predicateInFixedTable',        N'en', N'Table "{table}" has fixed rows: a predicate is not needed, the rows are known in advance.', 1),
    (N'expr.ref.unknownRowSelector',           N'en', N'Unknown row selector.', 1),
    (N'expr.ref.monthPlaceholderOutsideColumn', N'en', N'The Month placeholder can only be used in a formula bound to a month column.', 1),
    (N'expr.ref.unknownRangeBound',            N'en', N'Row "{row}" does not exist in table "{table}": the range bound cannot be resolved.', 1),
    (N'expr.ref.reversedRange',                N'en', N'Range "{from}:{to}" is written in reverse order.', 1),
    (N'expr.ref.rangeTableUnavailable',        N'en', N'The range cannot be expanded: its table is not available.', 1),
    -- Діалект Report (`02b` §8a): правило звіту бачить лише свій рядок і параметри.
    (N'expr.referenceForbiddenInReport',       N'en', N'The reference "{construct}" is not allowed in the report dialect: a report rule sees only the columns of its own row ("[Code]") and the report parameters ("@Name").', 1),
    -- V-20: діагностики зв'язувача, поради діалекту методологій і цикли — ключами каталогу.
    (N'expr.unknownFunctionCase', N'en', N'Function "{name}" is not available in the {dialect} dialect. In the legacy engine (NCalc 1.3.8) names are case-sensitive: write "{exact}".', 1),
    (N'expr.unknownFunctionReplacement.POWER', N'en', N'Function "{name}" is not available in the {dialect} dialect. In the legacy engine (NCalc 1.3.8) use Pow(a, b) instead.', 1),
    (N'expr.unknownFunctionReplacement.TRUNC', N'en', N'Function "{name}" is not available in the {dialect} dialect. In the legacy engine (NCalc 1.3.8) use Truncate(a) instead — it truncates to an integer only; to n digits write Truncate(a * 10^n) / 10^n.', 1),
    (N'expr.unknownFunctionReplacement.MOD', N'en', N'Function "{name}" is not available in the {dialect} dialect. In the legacy engine (NCalc 1.3.8) use the % operator instead.', 1),
    (N'expr.unknownFunctionReplacement.SWITCH', N'en', N'Function "{name}" is not available in the {dialect} dialect. In the legacy engine (NCalc 1.3.8) use nested if(condition, then, else) instead.', 1),
    (N'expr.unknownFunctionReplacement.COALESCE', N'en', N'Function "{name}" is not available in the {dialect} dialect, and nothing replaces it: null does not occur at run time in the methodology dialect.', 1),
    (N'expr.unknownFunctionReplacement.IFERROR', N'en', N'Function "{name}" is not available in the {dialect} dialect, and nothing replaces it: an evaluation error is not caught in the methodology dialect.', 1),
    (N'expr.predicate.functionCall', N'en', N'Calling function "{name}" in a predicate is not allowed: the predicate is evaluated for every row of the table.', 1),
    (N'expr.predicate.crossPeriod', N'en', N'A cross-period reference is not allowed in a predicate: filtering this period must not read another one.', 1),
    (N'expr.predicate.nested', N'en', N'A nested predicate is not allowed.', 1),
    (N'expr.predicate.sameRowOnly', N'en', N'A predicate may reference only columns of the same row.', 1),
    (N'expr.predicate.ternary', N'en', N'The ternary operator is not allowed in a predicate.', 1),
    (N'expr.report.resultType', N'en', N'A result of type {expected} was expected, but the expression gives {actual}.', 1),
    (N'expr.report.ownRowColumnsOnly', N'en', N'A report rule sees only the columns of its own row: [Code].', 1),
    (N'expr.report.columnMissing', N'en', N'The report row has no column "{column}".', 1),
    (N'expr.report.parameterMissing', N'en', N'The report has no parameter "@{name}".', 1),
    (N'expr.report.scope', N'en', N'A report rule sees only the columns of its own row and the report parameters.', 1),
    (N'expr.report.unknownColumnType', N'en', N'Unknown type "{type}" of column "{name}".', 1),
    (N'expr.report.unknownParameterType', N'en', N'Unknown type "{type}" of parameter "{name}".', 1),
    (N'expr.report.unknownResultType', N'en', N'Unknown expected result type "{type}".', 1),
    (N'expr.type.notNeedsBoolean', N'en', N'Negation applies only to a Boolean value.', 1),
    (N'expr.type.signNeedsNumber', N'en', N'A unary sign applies only to a number.', 1),
    (N'expr.type.datesNotAdded', N'en', N'Dates cannot be added; the difference of two dates is a number of days.', 1),
    (N'expr.type.logicalNeedsBoolean', N'en', N'A logical operation needs Boolean operands.', 1),
    (N'expr.type.incomparable', N'en', N'Comparison of incompatible types: {left} and {right}.', 1),
    (N'expr.type.conditionNeedsBoolean', N'en', N'The condition must be Boolean.', 1),
    (N'expr.type.ifNeedsCondition', N'en', N'The first argument of IF must be a condition.', 1),
    (N'expr.type.arithmeticNeedsNumber', N'en', N'Arithmetic expects a number, but the operand is of type {actual}.', 1),
    (N'expr.unit.branchesDiffer', N'en', N'The branches of a condition must be in the same unit.', 1),
    (N'expr.unit.addNeedsConvert', N'en', N'Adding values in different units needs an explicit CONVERT.', 1),
    (N'expr.unit.productUndeclared', N'en', N'The product of two dimensioned quantities has no declared unit.', 1),
    (N'expr.unit.inverseUndeclared', N'en', N'Dividing a dimensionless value by a dimensioned one has no declared unit.', 1),
    (N'expr.unit.derivedMissing', N'en', N'The unit catalogue (uom.Unit) has no derived unit for this division.', 1),
    (N'expr.unit.compareNeedsConvert', N'en', N'Comparing values in different units needs an explicit CONVERT.', 1),
    (N'expr.unit.rowScopedAggregate', N'en', N'Aggregating a column with a per-row unit needs an explicit CONVERT to a common unit.', 1),
    (N'expr.unit.aggregateNeedsConvert', N'en', N'Aggregating values in different units needs an explicit CONVERT.', 1),
    (N'expr.unit.convertArity', N'en', N'CONVERT takes three arguments: the value, the source unit and the target unit.', 1),
    (N'expr.unit.convertTargetLiteral', N'en', N'The target unit of CONVERT must be a literal, not an expression: otherwise the unit of the result is unknown until run time.', 1),
    (N'expr.unit.unknownUnit', N'en', N'Unit "{unit}" does not exist in the unit catalogue (uom.Unit).', 1),
    (N'expr.unit.convertSourceForm', N'en', N'The source unit of CONVERT is a literal or a reference to a unit column.', 1),
    (N'expr.unit.convertDimensions', N'en', N'CONVERT from "{from}" to "{to}" is impossible: the dimensions differ. That needs a context coefficient, which belongs to a methodology.', 1),
    (N'expr.unit.dimensionMismatch', N'en', N'The operands have different dimensions: no conversion between them is possible at all.', 1),
    (N'expr.cycle', N'en', N'The formulas form a cycle: {path}.', 1),
    (N'expr.cycleSelf', N'en', N'Formula "{name}" reads its own result. It has no evaluation order: it needs the value before the value exists. In the legacy system such a formula was silently never calculated.', 1),
    (N'expr.cycleUnknownPath', N'en', N'The formulas form a cycle.', 1),

    -- /admin/health (`Q-304`): статуси перевірок будувалися одразу готовим
    -- українським реченням (`HealthCheckResult.Description`) — не через
    -- `Ecr.Expressions`-подібну відсутність DI (`Q-303`), а через звичайний
    -- пропуск: перевірки резолвяться в scope запиту (той самий, з якого
    -- `DatabaseHealthCheck` уже бере `EcrDbContext`), просто каталогом не
    -- скористались. Приватна область: `/admin/health` вимагає входу.
    (N'health.db.rcsiDisabled',                N'en', N'RCSI is disabled.', 1),
    (N'health.db.missingFilegroups',           N'en', N'Missing filegroups: {names}.', 1),
    (N'health.db.partitionsLow',               N'en', N'Partitions ahead: {count} — below the minimum of {minimum}.', 1),
    (N'health.db.available',                   N'en', N'Database is available.', 1),
    (N'health.db.unavailable',                 N'en', N'Database is unavailable.', 1),
    (N'health.db.limitation.onlineIndexRebuild', N'en', N'Online index rebuild requires a maintenance window (ONLINE = ON is not available).', 1),
    (N'health.db.limitation.resourceGovernor', N'en', N'Background jobs are not isolated from the interactive peak (no Resource Governor).', 1),
    (N'health.db.limitation.rcsi',              N'en', N'RCSI is disabled: reads will block writes during the peak of the last day of the period.', 1),
    (N'health.db.limitation.archiveBatchSize', N'en', N'Archive batch size: {size}.', 1),
    (N'health.jobs.notRegistered',              N'en', N'The scheduler is not registered in the container.', 1),
    (N'health.jobs.stopped',                    N'en', N'The scheduler is stopped: no background job will run.', 1),
    (N'health.jobs.noSchedules',                N'en', N'The scheduler is alive, but no schedule is registered.', 1),
    (N'health.jobs.running',                    N'en', N'The scheduler is running.', 1),
    (N'health.jobs.unavailable',                N'en', N'The scheduler is unavailable.', 1),
    (N'health.sources.notRegistered',           N'en', N'The collection store is not registered in the container.', 1),
    (N'health.sources.noneActive',              N'en', N'No active collection sources.', 1),
    (N'health.sources.failedCount',             N'en', N'Sources with a failed last run: {count}.', 1),
    (N'health.sources.gapsCount',                N'en', N'Sources with a coverage gap: {count}.', 1),
    (N'health.sources.allCollectedNoGaps',      N'en', N'All active sources are collected with no gaps.', 1),

    -- /admin/jobs (lane6 медіум-аудиту, `Q-325` → `Q-326`): жоден тип фонової
    -- задачі не проводив повідомлення прогресу через каталог — усі писали
    -- готовий український рядок напряму в `itg.JobProgress.Message`.
    -- Користувач з англійським чи казахським інтерфейсом бачив необ'єднаний
    -- український текст на екрані стеження за задачею. Задача тепер пише
    -- структурований конверт (ключ + параметри, `JobProgressMessageEnvelope`)
    -- у ТОЙ САМИЙ стовпець; ключі нижче резолвяться мовою ЧИТАЧА при
    -- `GET /api/v1/jobs/{jobId}` (`GetJobStatusHandler`), не в момент запису.
    (N'jobs.recalcFormulas',                      N'en', N'Recalculating template formulas.', 1),
    (N'jobs.recalcFormulasDone',                  N'en', N'Template formulas: recalculated cells — {cells}.', 1),
    (N'jobs.recalcMethodologiesStart',            N'en', N'Recalculating methodologies.', 1),
    (N'jobs.documentPrefix',                      N'en', N'Document {id} ({index} of {count}): {message}', 1),
    (N'jobs.phaseMethodologies',                  N'en', N'Methodologies: {message}', 1),
    (N'jobs.batchProgress',                       N'en', N'Batch {ordinal} of {total}', 1),
    (N'jobs.snapshotReading',                     N'en', N'Reading data', 1),
    (N'jobs.snapshotBuilt',                       N'en', N'Snapshot {snapshotId} built', 1),
    (N'jobs.archiveRange',                        N'en', N'Archiving {from}…{to}', 1),
    (N'jobs.archiveRunMissing',                   N'en', N'Run not recorded', 1),
    (N'jobs.archiveResult',                       N'en', N'{status}: moved {rowsMoved}, last period {lastPeriod}', 1),
    (N'jobs.collectionRange',                     N'en', N'Collecting entity {sourceEntityId} for {from}…{to}', 1),
    (N'jobs.consistencyOrphanedCells',            N'en', N'Orphaned cells', 1),
    (N'jobs.consistencyBrokenRefs',                N'en', N'Broken references', 1),
    (N'jobs.consistencyArchiveCheck',              N'en', N'Archive reconciliation', 1),
    (N'jobs.consistencyUnboundCalculated',         N'en', N'Calculated columns without a binding', 1),
    (N'jobs.consistencyRecalcOrphaned',            N'en', N'Recalculating IsOrphaned', 1),
    (N'jobs.consistencyIssuesFound',               N'en', N'Issues found: {count}', 1),
    (N'jobs.exportReadingDocument',                N'en', N'Reading document', 1),
    (N'jobs.exportSavingWorkbook',                 N'en', N'Saving workbook', 1),
    (N'jobs.importApplyingDiff',                   N'en', N'Applying diff', 1),
    (N'jobs.importApplied',                        N'en', N'Applied cells: {cells}; validation messages: {validationCount}', 1),
    (N'jobs.formulaRecalcNone',                    N'en', N'No changed cells.', 1),
    (N'jobs.formulaRecalcDone',                    N'en', N'Recalculated cells: {written}.', 1),
    (N'jobs.materializeReadingMappings',           N'en', N'Reading mappings', 1),
    (N'jobs.materializeNoMappings',                N'en', N'No materialized mappings', 1),
    (N'jobs.materializePeriodClosed',              N'en', N'Period is closed: transfer skipped', 1),
    (N'jobs.materializeFolding',                   N'en', N'Folding points', 1),
    (N'jobs.materializeNoPoints',                  N'en', N'No points in interval', 1),
    (N'jobs.materializeWriting',                   N'en', N'Writing to cells', 1),
    (N'jobs.materializeDone',                      N'en', N'Written {applied}; kept manual {keptManual}', 1),
    (N'jobs.notificationReadingCollectionFailures', N'en', N'Reading collection failures', 1),
    (N'jobs.notificationReadingMaintenanceFailures', N'en', N'Reading maintenance failures', 1),
    (N'jobs.notificationSendingQueue',              N'en', N'Sending notification queue', 1),
    (N'jobs.notificationDone',                      N'en', N'Failures in digest: {count}; sent: {sent}; still queued: {pending}', 1),
    (N'jobs.orphanScanChecking',                    N'en', N'Checking registry references', 1),
    (N'jobs.orphanScanDone',                        N'en', N'Rows changed: {changed}', 1),
    (N'jobs.partitionUnavailable',                  N'en', N'Partitioning unavailable', 1),
    (N'jobs.partitionAhead',                        N'en', N'Partitions ahead: {ahead}', 1),
    (N'jobs.partitionShortage',                     N'en', N'PARTITION SHORTAGE: {ahead}', 1),
    (N'jobs.retentionCleared',                      N'en', N'Cleared snapshots: {snapshots}, rows: {rows}', 1),
    (N'jobs.retryScheduled',                        N'en', N'Attempt {attempt}/{max} in {delaySeconds}s after error: {error}', 1),

    -- ══ Підписи станів для `shared/ui/StatusBadge.tsx` (UI-04, директива №15 §2) ══
    --
    -- ⛔ Ключ — `status.<різновид>.<стан>`, де `<стан>` записаний ТАК, ЯК ЙОГО
    -- НАЗИВАЄ СЕРВЕР. Це вже усталена тут форма для ключів, похідних від
    -- переліку домену (`deny.OutsidePermitWindow`, `deny.ColumnReadOnly` вище):
    -- будь-яке приведення регістру між значенням сервера й ключем дало б другу
    -- відповідність, яку нема кому перевірити — рівно `A7-02`
    -- (`ReadOnlyColumn` проти `ColumnReadOnly`).
    --
    -- ⛔ Стани взяті з КОДУ СЕРВЕРА, а не з макета `docs/design/hybrid/KIT.md`:
    -- макет називає `sheet/Returned`, `period/Archived`, `period/NotOpened`,
    -- `job/Done`, `version/Archived`, `severity/Critical` і цілий різновид
    -- `user` — жодного з них у домені немає. Заведені ключі під неіснуючі
    -- стани були б мертвими рядками каталогу, які ніхто ніколи не покаже.
    --
    -- ⚠ Тексти нічого не вигадують: сьогодні ті самі значення видно
    -- користувачеві СИРИМИ кодами (`pages/DocumentsPage.tsx` малює
    -- `<Badge>{sheet}: {state}</Badge>`). Рядок лише дає тому самому значенню
    -- ім'я в каталозі, яке термінолог (`C-7`) зможе перекласти без правки коду.
    --
    -- ⚠ Область приватна (1): статуси видно лише після входу.

    -- `DocumentStatus` (Enums.cs) — стан пари «аркуш × період» (`D-93`).
    (N'status.sheet.Draft',                N'en', N'Draft', 1),
    (N'status.sheet.Submitted',            N'en', N'Submitted', 1),
    (N'status.sheet.Approved',             N'en', N'Approved', 1),
    (N'status.sheet.Rejected',             N'en', N'Rejected', 1),

    -- `PeriodState` (Enums.cs).
    (N'status.period.Scheduled',           N'en', N'Not open yet', 1),
    (N'status.period.Open',                N'en', N'Open', 1),
    (N'status.period.Grace',               N'en', N'Grace period', 1),
    (N'status.period.Closed',              N'en', N'Closed', 1),

    -- `IntegrationHandlers.KnownStates` плюс `Unknown`/`Unavailable`, які
    -- віддає лише `GET /jobs/{jobId}` (`QuartzJobScheduler`).
    (N'status.job.Queued',                 N'en', N'Queued', 1),
    (N'status.job.Running',                N'en', N'Running', 1),
    (N'status.job.Succeeded',              N'en', N'Succeeded', 1),
    (N'status.job.Failed',                 N'en', N'Failed', 1),
    (N'status.job.Cancelled',              N'en', N'Cancelled', 1),
    (N'status.job.Unknown',                N'en', N'Unknown job', 1),
    (N'status.job.Unavailable',            N'en', N'Scheduler unavailable', 1),

    -- `TemplateVersionStatus` (Enums.cs) — і для версій шаблону, і для версій
    -- методології: перелік один.
    (N'status.version.Draft',              N'en', N'Draft', 1),
    (N'status.version.Published',          N'en', N'Published', 1),
    (N'status.version.Deprecated',         N'en', N'Deprecated', 1),

    -- `ProjectStatus` (Enums.cs).
    (N'status.project.Draft',              N'en', N'Draft', 1),
    (N'status.project.Active',             N'en', N'Active', 1),
    (N'status.project.Archived',           N'en', N'Archived', 1),

    -- Зведений стан перевірок (`HealthReportDto`, `HealthStatus` платформи).
    (N'status.health.Healthy',             N'en', N'Healthy', 1),
    (N'status.health.Degraded',            N'en', N'Degraded', 1),
    (N'status.health.Unhealthy',           N'en', N'Unhealthy', 1),

    -- `ValidationSeverity` (Enums.cs). `Critical` у переліку немає.
    (N'status.severity.Info',              N'en', N'Info', 1),
    (N'status.severity.Warning',           N'en', N'Warning', 1),
    (N'status.severity.Error',             N'en', N'Error', 1),

    -- Результат збору з зовнішнього джерела (`CollectionRunner`). Словник
    -- окремий від `status.health.*`: спільне в них лише слово `Degraded`.
    (N'status.collectionRun.Succeeded',    N'en', N'Succeeded', 1),
    (N'status.collectionRun.Degraded',     N'en', N'Completed with warnings', 1),
    (N'status.collectionRun.Failed',       N'en', N'Failed', 1),

    -- `SnapshotStatus` (Enums.cs, D-65). Словник окремий від `status.sheet.*`:
    -- `Rejected` у зрізі немає, а `Submitted` — кінцевий іммутабельний стан.
    (N'status.snapshot.Draft',             N'en', N'Draft', 1),
    (N'status.snapshot.Approved',          N'en', N'Approved', 1),
    (N'status.snapshot.Submitted',         N'en', N'Submitted', 1),

    -- `NotificationDeliveryStatus` (BE-33). Словник окремий від `status.job.*`:
    -- там падіння ЗАДАЧІ, тут — недоставлене сповіщення про неї. `Suppressed`
    -- бляклий: подію навмисно не надіслали (дедуплікація), це нормальна робота.
    (N'status.notificationDelivery.Sent',       N'en', N'Sent', 1),
    (N'status.notificationDelivery.Failed',     N'en', N'Failed', 1),
    (N'status.notificationDelivery.Suppressed', N'en', N'Suppressed', 1),

    -- Екран сповіщень (BE-33, рішення 2.3 директиви №15): канали, правила
    -- «подія × канал» і журнал доставок.
    (N'nav.notifications',                 N'en', N'Notifications', 1),
    (N'notifications.title',               N'en', N'Notifications', 1),

    (N'notifications.channels',            N'en', N'Channels', 1),
    (N'notifications.addChannel',          N'en', N'Add channel', 1),
    (N'notifications.editChannel',         N'en', N'Edit', 1),
    (N'notifications.channelForm',         N'en', N'Channel', 1),
    (N'notifications.channelName',         N'en', N'Name', 1),
    (N'notifications.channelKind',         N'en', N'Transport', 1),
    (N'notifications.kind.Smtp',           N'en', N'Email (SMTP)', 1),
    (N'notifications.kind.TeamsWebhook',   N'en', N'Teams webhook', 1),
    (N'notifications.enabled',             N'en', N'Enabled', 1),
    (N'notifications.enabledYes',          N'en', N'Yes', 1),
    (N'notifications.enabledNo',           N'en', N'No', 1),
    (N'notifications.modified',            N'en', N'Changed', 1),
    (N'notifications.channelSaved',        N'en', N'Channel saved.', 1),
    (N'notifications.channelDeleted',      N'en', N'Channel deleted.', 1),
    (N'notifications.noChannels',          N'en', N'No channels yet', 1),
    (N'notifications.noChannelsHint',      N'en', N'Until a channel exists, nobody is notified: rules have nowhere to send.', 1),

    -- ⛔ Секрет каналу читає РІВНО ОДИН відправник — `TeamsWebhookSender`, і
    -- для нього це сама адреса вебхука (без неї він відмовляє). Пошта секрету
    -- каналу не торкається: пароль бере транспорт процесу. Тому підписи
    -- говорять про АДРЕСУ, а для пошти в переліку стоїть «не застосовується».
    (N'notifications.webhookUrl',          N'en', N'Webhook URL', 1),
    (N'notifications.webhookHint',         N'en', N'The stored URL is never shown; typing a new one replaces it. The host must be on the allow-list, otherwise the server refuses with ECR-REQ-0422.', 1),
    (N'notifications.webhookSet',          N'en', N'Set', 1),
    (N'notifications.webhookMissing',      N'en', N'No URL', 1),
    (N'notifications.setWebhook',          N'en', N'Webhook URL', 1),
    (N'notifications.clearWebhook',        N'en', N'Remove URL', 1),
    (N'notifications.secretSaved',         N'en', N'Saved.', 1),
    (N'notifications.notApplicable',       N'en', N'Not applicable', 1),
    (N'notifications.secretNotUsedSmtp',   N'en', N'Email channels use the application SMTP credentials; a channel secret would never be read.', 1),
    (N'notifications.testChannel',         N'en', N'Send test', 1),
    (N'notifications.testOk',              N'en', N'The channel accepted the test message.', 1),
    (N'notifications.testFailed',          N'en', N'The channel refused the test message.', 1),

    -- ⚠ Полів `host`/`port`/`from`/`useTls` на екрані немає: контракт їх
    -- носить, але не читає жоден відправник (перевірено в `SmtpChannelSender`).
    (N'notifications.smtpTransportHint',   N'en', N'The server, sender address and password come from the application configuration; the channel only adds recipients.', 1),
    (N'notifications.smtpRecipients',      N'en', N'Recipients', 1),
    (N'notifications.smtpRecipientsHint',  N'en', N'Comma-separated addresses.', 1),
    (N'notifications.subjectPrefix',       N'en', N'Subject prefix', 1),
    (N'notifications.subjectPrefixHint',   N'en', N'Prepended to the subject of every message from this channel.', 1),
    (N'notifications.teamsTitle',          N'en', N'Card title', 1),
    (N'notifications.teamsTitleHint',      N'en', N'Shown above the message in Teams.', 1),

    -- Матриця правил «подія × канал».
    (N'notifications.rules',               N'en', N'Rules', 1),
    (N'notifications.event',               N'en', N'Event', 1),
    (N'notifications.channel',             N'en', N'Channel', 1),
    (N'notifications.minSeverity',         N'en', N'From severity', 1),
    (N'notifications.saveRules',           N'en', N'Save rules', 1),
    (N'notifications.rulesSaved',          N'en', N'Rules saved.', 1),

    -- ⚠ `NotificationEventKind` — п'ять видів, усі приходять у `eventKinds`,
    -- навіть ті, на які правила ще немає.
    (N'notifications.event.JobFailed',              N'en', N'Background job failed', 1),
    (N'notifications.event.ConsistencyIssuesFound', N'en', N'Consistency issues found', 1),
    (N'notifications.event.PartitionsRunningOut',   N'en', N'Partitions running out', 1),
    (N'notifications.event.CollectionFailed',       N'en', N'Collection from a source failed', 1),
    (N'notifications.event.ExportFailed',           N'en', N'Export failed', 1),

    -- Журнал доставок.
    (N'notifications.deliveries',          N'en', N'Deliveries', 1),
    (N'notifications.at',                  N'en', N'When', 1),
    (N'notifications.status',              N'en', N'Outcome', 1),
    (N'notifications.error',               N'en', N'Reason', 1),
    (N'notifications.channelGone',         N'en', N'The channel has been deleted; the log entry remains.', 1),
    (N'notifications.showMore',            N'en', N'Show more', 1),
    (N'notifications.noDeliveries',        N'en', N'No deliveries yet', 1),
    (N'notifications.noDeliveriesHint',    N'en', N'Nothing has been sent since the log was started.', 1),

    -- ══ Назви прав для матриці `/admin/security` (`U-11`) ══
    --
    -- ⛔ Матриця підписувала 41 колонку сирим кодом сервера
    -- (`Calculation.EditConstant`, `System.ManageLocalization`, …). Той самий
    -- клас, який за тиждень закривали чотири рази (#437, #438, #440, #441), —
    -- тут він лишався в найширшому місці продукту.
    --
    -- ⛔ Ключ — `permission.<Code>`, де `<Code>` записаний ТАК, ЯК ЙОГО НАЗИВАЄ
    -- СЕРВЕР (`sec.Permission.Code` вище в цьому ж файлі). Це вже усталена тут
    -- форма для ключів, похідних від значення сервера (`status.<вид>.<стан>`,
    -- `deny.<причина>`): будь-яке приведення регістру дало б другу
    -- відповідність, яку нема кому перевірити — рівно `A7-02`.
    --
    -- ⚠ Код НЕ зникає з екрана: він лишається другим рядком підпису
    -- (`TwoLine`, `KIT.md` §1.7). Адміністратор безпеки оперує саме кодами —
    -- вони в журналі `DangerousPermissionsGranted`, у відмові
    -- `err.ECR-AUTH-0403.permission` («Requires permission {permission}») і в
    -- шаблонах складених ролей (`Report.%`). Причина — в `permissionLabel.ts`.
    --
    -- ⚠ Назва описує ДІЮ, а не повторює код словами: «Edit constant» нічого не
    -- додає до `Calculation.EditConstant`; «Edit methodology constants» каже,
    -- ЧОГО саме стосується право. Область приватна (1): матриця — за входом.
    (N'permission.Template.View',            N'en', N'View templates', 1),
    (N'permission.Template.Edit',            N'en', N'Edit template structure', 1),
    (N'permission.Template.Publish',         N'en', N'Publish template versions', 1),

    (N'permission.Registry.View',            N'en', N'View registries', 1),
    (N'permission.Registry.EditData',        N'en', N'Edit registry rows', 1),
    (N'permission.Registry.EditDefinition',  N'en', N'Edit registry structure', 1),
    (N'permission.Registry.Publish',         N'en', N'Publish registry versions', 1),

    (N'permission.Document.View',            N'en', N'View documents', 1),
    (N'permission.Document.Create',          N'en', N'Create documents', 1),
    (N'permission.Document.Delete',          N'en', N'Delete draft documents', 1),
    (N'permission.Document.Import',          N'en', N'Import documents from a workbook', 1),
    (N'permission.Document.Export',          N'en', N'Export documents', 1),
    (N'permission.Document.Reopen',          N'en', N'Return a submitted document to draft', 1),
    (N'permission.Document.ChangeKey',       N'en', N'Change the business key of a document', 1),

    (N'permission.Project.Manage',           N'en', N'Manage projects', 1),

    (N'permission.Period.Configure',         N'en', N'Configure reporting periods', 1),
    (N'permission.Period.Reopen',            N'en', N'Reopen a closed period', 1),

    (N'permission.Calculation.View',         N'en', N'View methodologies', 1),
    (N'permission.Calculation.EditFormula',  N'en', N'Edit methodology formulas', 1),
    (N'permission.Calculation.EditConstant', N'en', N'Edit methodology constants', 1),
    (N'permission.Calculation.EditRule',     N'en', N'Edit methodology rules', 1),
    (N'permission.Calculation.Publish',      N'en', N'Publish methodology versions', 1),
    (N'permission.Calculation.Recalculate',  N'en', N'Run a recalculation', 1),
    (N'permission.Calculation.ManageRequiredInputs', N'en', N'Manage required input columns', 1),

    (N'permission.Report.ViewRegulatory',    N'en', N'View regulatory reports', 1),
    (N'permission.Report.BuildSnapshot',     N'en', N'Build a report snapshot', 1),
    (N'permission.Report.Export',            N'en', N'Export reports', 1),
    (N'permission.Report.EditDefinition',    N'en', N'Author regulatory report definitions', 1),
    (N'permission.Report.ViewCampaign',      N'en', N'View the campaign overview of all projects', 1),

    (N'permission.Integration.View',         N'en', N'View collection sources', 1),
    (N'permission.Integration.Manage',       N'en', N'Manage collection sources and their secrets', 1),
    (N'permission.Integration.EditSchedule', N'en', N'Edit collection schedules', 1),

    (N'permission.Uom.EditCatalog',          N'en', N'Edit the catalogue of units of measure', 1),

    (N'permission.Security.ManageUsers',     N'en', N'Manage users', 1),
    (N'permission.Security.ManageRoles',     N'en', N'Manage roles and their permissions', 1),
    (N'permission.Security.ViewAudit',       N'en', N'View the audit log', 1),
    (N'permission.Security.Simulate',        N'en', N'Simulate the access of another user', 1),

    (N'permission.System.ViewHealth',        N'en', N'View system health and the job queue', 1),
    (N'permission.System.RunJob',            N'en', N'Start and cancel background jobs', 1),
    (N'permission.System.ManageLocalization', N'en', N'Manage interface strings and languages', 1),
    (N'permission.System.ManageNotifications', N'en', N'Manage notification channels and rules', 1),

    -- ══ Назви карток стану на `/admin/health` (`U-14`) ══
    --
    -- ⛔ Заголовками карток стояли імена реєстрації перевірок — `db`, `jobs`,
    -- `sources` (`Program.cs`, `AddCheck<DatabaseHealthCheck>("db", …)`),
    -- маленькими літерами, над цілком людським реченням, яке `Q-304` уже
    -- перевело на цей самий каталог («Database is available.»).
    --
    -- ⚠ Ключ несе ІМ'Я перевірки так, як його реєструє сервер; невідома
    -- перевірка показує саме ім'я, а не позначений ключ (`checkLabel`).
    (N'health.check.db',                     N'en', N'Database', 1),
    (N'health.check.jobs',                   N'en', N'Background jobs', 1),
    (N'health.check.sources',                N'en', N'Collection sources', 1),

    -- ══ Імпорт і фонові задачі: четвертий раунд UX-PASS (лінія B2) ══
    --
    -- ⛔ F-06: відмова типу — у ПЕРЕГЛЯДІ, тим самим читачем, що й запис.
    -- Колонка стоїть у рядку переліку окремо, тож ключі без `{columnCode}`.
    (N'err.ECR-CELL-0422.importExpectsNumber',     N'en', N'The value is not a number.', 1),
    (N'err.ECR-CELL-0422.importExpectsBoolean',    N'en', N'The value is not true or false.', 1),
    (N'err.ECR-CELL-0422.importExpectsDate',       N'en', N'The value is not a date.', 1),
    (N'err.ECR-CELL-0422.importExpectsIdentifier', N'en', N'No entry with this code in the column''s registry or list of units.', 1),
    (N'err.ECR-CELL-0422.importExpectsUnit',       N'en', N'No unit of measure with this code.', 1),
    -- ⛔ F-24 і сусіди: відмови імпорту, що доти їхали українським реченням.
    (N'err.ECR-IMP-0422.previewExpired',        N'en', N'The import preview has expired or was already applied. Load the file again.', 1),
    (N'err.ECR-IMP-0422.previewUnreadable',     N'en', N'The saved import preview cannot be read. Load the file again.', 1),
    (N'err.ECR-IMP-0422.previewOtherDocument',  N'en', N'This import preview was built for another document.', 1),
    (N'err.ECR-IMP-0422.workbookOtherDocument', N'en', N'The workbook was exported from another document.', 1),
    (N'err.ECR-IMP-0422.noMapSheet',            N'en', N'The workbook has no service sheet: only a file exported by this system can be imported.', 1),
    (N'err.ECR-IMP-0422.mapBroken',             N'en', N'The service sheet of the workbook is empty or damaged. Export the document again.', 1),
    -- Великий імпорт іде у фон (F-01): людина має знати, де шукати результат.
    (N'import.queued',                          N'en', N'The import is large and is being applied in the background. Follow it in My tasks.', 1),
    -- ⛔ F-27: у «My tasks» замість ідентифікатора файлу експорту.
    (N'jobs.exportReady',                       N'en', N'The file is ready to download.', 1),
    -- ══ Четвертий раунд UX, лінія D: шаблони й довідники ══
    (N'common.close', N'en', N'Close', 0),
    (N'columns.unit', N'en', N'Unit', 1),
    (N'columns.unitHint', N'en', N'The unit values of this column are stored in. Once set, it can be changed but not cleared.', 1),
    (N'columns.unitEmpty', N'en', N'No units found', 1),
    (N'columns.usageDraftNote', N'en', N'This version is a draft: template formulas that read this column are recorded when the version is published, so they are not listed yet.', 1),
    (N'columns.deleteTitle', N'en', N'Remove column "{name}"?', 1),
    (N'sheets.deleteTitle', N'en', N'Remove sheet "{name}"?', 1),
    (N'tableDef.deleteTitle', N'en', N'Remove table "{name}"?', 1),
    (N'rows.deleteTitle', N'en', N'Remove row "{name}"?', 1),
    (N'structure.deleteText', N'en', N'It is removed from this draft version. Formulas, rules and relations that refer to it must be fixed before the version can be published.', 1),
    (N'validationRules.existing', N'en', N'Existing rules', 1),
    (N'validationRules.empty', N'en', N'This table has no validation rules yet.', 1),
    (N'validationRules.inactive', N'en', N'Inactive', 1),
    (N'validationRules.deleteNamed', N'en', N'Remove rule {code}', 1),
    (N'validationRules.deleteTitle', N'en', N'Remove rule "{code}"?', 1),
    (N'validationRules.deleteText', N'en', N'The rule stops checking this table. This cannot be undone: to bring it back, create it again.', 1),
    (N'version.titleOf', N'en', N'Template version {version}', 1),
    (N'version.diffDirection', N'en', N'From v{from} to v{to}', 1),
    (N'version.diffNoOther', N'en', N'This template has no other versions', 1),
    (N'periodRules.sheet', N'en', N'Sheet', 1),
    (N'periodRules.table', N'en', N'Table', 1),
    (N'periodRules.role', N'en', N'Role', 1),
    (N'periodRules.sourceColumn', N'en', N'Source column', 1),
    (N'periodRules.sourceColumnEmpty', N'en', N'This version has no lookup columns', 1),
    (N'periodRules.deleteTitle', N'en', N'Remove period access rule {id}?', 1),
    (N'periodRules.deleteText', N'en', N'Cells it locked become editable again according to the remaining rules.', 1),
    (N'tables.deleteRelationTitle', N'en', N'Remove relation "{code}"?', 1),
    (N'tables.deleteRelationText', N'en', N'The target table stops taking its numbers from the source table.', 1),
    (N'notifications.deleteChannelTitle', N'en', N'Remove channel "{name}"?', 1),
    (N'notifications.deleteChannelText', N'en', N'Notifications routed to this channel stop being delivered.', 1),
    (N'groupRoles.empty', N'en', N'No directory group has a role yet.', 1),
    (N'groupRoles.revokeTitle', N'en', N'Revoke role {role} from {group}?', 1),
    (N'groupRoles.revokeText', N'en', N'Every member of the group loses the permissions of this role.', 1),
    (N'units.deleteTitle', N'en', N'Remove unit "{code}"?', 1),
    (N'units.deleteChecking', N'en', N'Checking where the unit is used…', 1),
    (N'registries.deleteEntryTitle', N'en', N'Remove entry "{code}"?', 1),
    (N'registries.usageDataInDocuments', N'en', N'Values of this registry are already stored in documents', 1),
    (N'err.ECR-TMPL-0422.diffOtherTemplate', N'en', N'Only versions of the same template can be compared.', 1),
    (N'enum.dataType.String', N'en', N'Text', 1),
    (N'enum.dataType.Int', N'en', N'Whole number', 1),
    (N'enum.dataType.Decimal', N'en', N'Number', 1),
    (N'enum.dataType.Bool', N'en', N'Yes / no', 1),
    (N'enum.dataType.Date', N'en', N'Date', 1),
    (N'enum.dataType.Lookup', N'en', N'Registry value', 1),
    (N'enum.dataType.Unit', N'en', N'Unit per row', 1),
    (N'enum.dataType.Formula', N'en', N'Formula', 1),
    (N'enum.dataType.Calculated', N'en', N'Methodology result', 1),
    (N'enum.rowKind.Group', N'en', N'Group', 1),
    (N'enum.rowKind.Item', N'en', N'Item', 1),
    (N'enum.rowKind.Balance', N'en', N'Balance', 1),
    (N'enum.rowKind.Note', N'en', N'Note', 1),
    (N'enum.rowKind.Header', N'en', N'Header', 1),
    (N'enum.diffKind.Added', N'en', N'Added', 1),
    (N'enum.diffKind.Removed', N'en', N'Removed', 1),
    (N'enum.diffKind.Modified', N'en', N'Changed', 1),
    (N'enum.diffKind.Presentation', N'en', N'Appearance', 1),
    (N'enum.changeClass.Safe', N'en', N'Safe', 1),
    (N'enum.changeClass.Presentation', N'en', N'Appearance only', 1),
    (N'enum.changeClass.Guarded', N'en', N'Needs a migration', 1),
    (N'enum.changeClass.Breaking', N'en', N'Breaking', 1),
    (N'enum.periodRuleKind.AlwaysReadOnly', N'en', N'Always read-only', 1),
    (N'enum.periodRuleKind.HeaderRows', N'en', N'Header rows', 1),
    (N'enum.periodRuleKind.EditablePeriodOnly', N'en', N'Editable in a period range', 1),
    (N'enum.periodRuleKind.RelativeWindow', N'en', N'Window around the current period', 1),
    (N'enum.periodRuleKind.SourceWindow', N'en', N'Window from a source column', 1),
    (N'enum.periodRuleKind.Expression', N'en', N'Expression', 1),
    (N'enum.outOfWindow.ReadOnly', N'en', N'Read-only', 1),
    (N'enum.outOfWindow.Warn', N'en', N'Warn', 1),
    (N'enum.outOfWindow.AllowWithConfirmation', N'en', N'Allow with confirmation', 1),
    (N'enum.outOfWindow.Hide', N'en', N'Hide', 1),
    -- B-09: конфлікт версії має вихід — «Keep mine» / «Discard mine».
    (N'grid.conflictKeepMine', N'en', N'Keep mine', 1),
    (N'grid.conflictDiscardMine', N'en', N'Discard mine', 1),
    (N'grid.conflictRowGone', N'en', N'Row {row} no longer exists: your changes to it cannot be saved.', 1),

    -- X-13, R-01, R-02: редактори комірок — пошук і вибір зі списку (Lookup, Bool, Unit), поле дати.
    (N'grid.listSearchPlaceholder', N'en', N'Type to search', 1),
    (N'grid.listLoading', N'en', N'Loading options...', 1),
    (N'grid.listNothingFound', N'en', N'Nothing matches', 1),
    (N'grid.listMore', N'en', N'{count} more: type to narrow the list', 1),
    (N'grid.listClear', N'en', N'(clear the cell)', 1),
    (N'grid.lookupEditorLabel', N'en', N'Choose a registry entry', 1),
    (N'grid.boolEditorLabel', N'en', N'Choose yes or no', 1),
    (N'grid.unitEditorLabel', N'en', N'Choose a unit', 1),
    (N'grid.dateEditorLabel', N'en', N'Choose a date', 1),
    (N'grid.boolYes', N'en', N'Yes', 1),
    (N'grid.boolNo', N'en', N'No', 1),

    -- X-39: заглушка таблиці, яку ще не прогорнули.
    (N'grid.tableLoadsOnScroll', N'en', N'This table loads when you scroll to it.', 1),

    -- F-17, F-18, X-25: пояснення рівня для подання, причина закритого документа, підтвердження затвердження.
    (N'workflow.submitNeedsGrant', N'en', N'Submitting needs the Submit access level on this project or sheet; yours is {level}. Ask an administrator to raise it.', 1),
    (N'workflow.approveTitle', N'en', N'Approve this sheet?', 1),
    (N'workflow.approveHint', N'en', N'Approved figures become final for this period and go into regulatory reports. To change them later, the sheet has to be returned for edits.', 1),
    (N'document.lock.projectArchived', N'en', N'This project is archived: its documents are read-only.', 1),
    (N'document.lock.periodClosed', N'en', N'Period {period} is closed: its data can no longer be edited, imported, submitted or recalculated. Ask a period manager to reopen it.', 1),
    (N'document.lock.periodNotOpen', N'en', N'Period {period} is not open yet: data entry starts when it opens.', 1),
    (N'document.lock.sheetApproved', N'en', N'This sheet has been approved; editing is closed until it is returned for edits.', 1),

    -- ── Четвертий раунд UX, лінія B1 (методологія → документ) ──────────
    (N'err.ECR-CALC-0422.unknownUnit', N'en', N'{code}: unit {unitId} does not exist in the unit catalog.', 1),
    (N'err.ECR-CALC-0422.bindingMatchInvalid', N'en', N'The match condition for output {outputCode} is not a flat JSON object of "column → value" pairs, so it would match no row.', 1),
    (N'err.ECR-CALC-0422.bindingUnknownOutput', N'en', N'No version of this methodology declares output {outputCode}: nothing would ever be calculated for this binding.', 1),
    (N'err.ECR-CALC-0422.noPublishedVersion', N'en', N'Methodology {methodologyId} is bound to a table but has no published version to calculate with.', 1),
    (N'err.ECR-CALC-0422.goldenTestNoPeriod', N'en', N'Golden test {test} has no period: set a document and an existing period in its input (periodKey is year × 100 + number, e.g. 202601).', 1),
    (N'publish.problemsTitle', N'en', N'What to fix before publishing', 1),
    (N'publish.problem.constantNotNumber', N'en', N'Constant {code} is declared numeric, but its value "{value}" is not a number: decide on a value, zero is not a default.', 1),
    (N'publish.problem.constantNoText', N'en', N'Constant {code} ({kind}) has no text.', 1),
    (N'publish.problem.categoryLabelInExpression', N'en', N'Formula {formula} refers to CST.{code}, which is a category label, not a value.', 1),
    (N'publish.problem.textConstantInArithmetic', N'en', N'Formula {formula} uses text constant CST.{code} ("{value}") in arithmetic.', 1),
    (N'publish.problem.numberReturnsText', N'en', N'Formula {formula} is declared numeric but returns only text.', 1),
    (N'publish.problem.textReturnsNumber', N'en', N'Formula {formula} is declared as text but returns a number.', 1),
    (N'publish.problem.textOutput', N'en', N'Formula {formula} returns text but is declared a methodology output: results are stored as numbers.', 1),
    (N'publish.problem.importNoVersion', N'en', N'Imported methodology {code} has no version in effect on {date}: its formulas are not visible.', 1),
    (N'publish.problem.libraryHasRules', N'en', N'Methodology {code} is a library but has {count} active rules: a library does not calculate for any document.', 1),
    (N'publish.problem.ambiguousReference', N'en', N'Formula {formula}: reference !{name} is found in {count} imports ({candidates}).', 1),
    (N'methodologies.columnNotFound', N'en', N'No column matches. Search by column code or by part of its header.', 1),
    (N'methodologies.constantDialogTitle', N'en', N'Constant', 1),
    (N'methodologies.constantUnit', N'en', N'Unit', 1),
    (N'documents.calculationResultsStale', N'en', N'These results are out of date', 1),
    (N'documents.calculationResultsStaleHint', N'en', N'The inputs changed after the last recalculation, so these numbers no longer match the data. Recalculate the sheet before submitting it.', 1),
    (N'methodologies.publications', N'en', N'Publication log', 1),
    (N'methodologies.publicationsEmpty', N'en', N'No version of this methodology has been published yet.', 1),
    (N'methodologies.publishedAt', N'en', N'Published', 1),
    (N'methodologies.publishedBy', N'en', N'Published by', 1),
    (N'methodologies.publicationReason', N'en', N'Reason for the change', 1),
    (N'methodologies.publicationChanges', N'en', N'Changed values on the golden set', 1),

    -- UX-прохід, четвертий раунд, лінія E2 (оболонка й адмін-екрани).
    (N'common.technicalDetails', N'en', N'Technical details', 1),
    -- R-19: відповідь без тіла problem+json (шлюз, проксі) — ключі публічні,
    -- бо 502 буває й на сторінці входу.
    (N'err.http.notFound', N'en', N'The page or record you asked for does not exist.', 0),
    (N'err.http.forbidden', N'en', N'You do not have permission for this action.', 0),
    (N'err.http.timeout', N'en', N'The server took too long to answer. Try again.', 0),
    (N'err.http.unavailable', N'en', N'The server is not reachable right now. Try again in a minute.', 0),
    (N'err.http.serverError', N'en', N'The server could not complete the request.', 0),
    (N'err.http.requestFailed', N'en', N'The request could not be completed.', 0),
    -- X-26: доступні імена службових кнопок (були англійськими літералами).
    (N'common.closeNotification', N'en', N'Close notification', 0),
    (N'common.togglePasswordVisibility', N'en', N'Show or hide the password', 0),
    (N'common.undo', N'en', N'Undo', 1),
    (N'nav.skipToContent', N'en', N'Skip to main content', 1),
    (N'nav.showAllCrumbs', N'en', N'Show the whole path', 1)
) AS s ([Key], Lang, Val, Scope)
   ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
WHEN NOT MATCHED THEN INSERT ([Key], LanguageCode, Value, Scope, ModifiedAt)
     VALUES (s.[Key], s.Lang, s.Val, s.Scope, SYSUTCDATETIME());

-- ⛔ Без цього нові ключі, додані СЮДИ (а не через
-- `PUT /api/v1/ui-strings/{lang}/{key}`), лишають Revision незмінним:
-- `SetUiStringHandler` інкрементує його сам (R-B7), а цей MERGE — ні. Клієнт
-- із чинним ETag (`uiStrings:{lang}:{scope}:{revision}` у localStorage)
-- отримує 304 на СТАРУ версію каталогу — свіжі рядки лежать у таблиці, але
-- ніколи не доїжджають до екрана, доки хтось не відкриє `/admin/ui-strings`
-- і не збереже той самий ключ вручну. Живий доказ: `methodologies.*` з
-- директиви «обов'язкові вхідні колонки» (Q-306) — у базі є, на екрані досі
-- `⟦methodologies.requiredInputs⟧`.
--
-- ⚠ Умовно, а не завжди: `SeedRunner` виконується на КОЖНОМУ старті
-- застосунку (`02-contracts.md` §14), і безумовний інкремент означав би
-- зайве validation-round-trip для кожного клієнта на кожному рестарті, навіть
-- коли жодного нового рядка не додалося. `@@ROWCOUNT` після `MERGE` — це
-- кількість щойно вставлених рядків (клаузи `WHEN MATCHED` тут немає).
-- `@textUpdates` і `@removed` — те саме для секцій над MERGE: без інкременту
-- кеш `ui:{lang}:{scope}:{revision}` і ETag клієнта тримали б старий каталог.
SET @inserted = @@ROWCOUNT;
IF @inserted > 0 OR @textUpdates > 0 OR @removed > 0
BEGIN
    UPDATE sys_ecr.UiStringRevision
    SET Revision = Revision + 1, ModifiedAt = SYSUTCDATETIME()
    WHERE Id = 1;
END
GO

-- ── Опис звіту: одна державна форма з каталогу ФВ-10.7 ───────────────────
-- ⛔ Рівно ОДИН опис, і це не заготовка «на потім». `rpt.ReportDef` і
-- `rpt.ReportVersion` не створювало НІЩО — ні код, ні seed, ні тести, — тому
-- `POST /reports/{code}/build` відмовляв `ECR-RPT-0404` на будь-який код:
-- звітність існувала і не могла спрацювати жодного разу. Одна реальна форма
-- в seed робить чисту базу здатною побудувати зріз одразу, а не після того,
-- як хтось здогадається завести опис руками (директива №09, W7).
--
-- ⚠ `IEC` — з каталогу державних форм ТЗ (`ФВ-10.7`): Industrial
-- Environmental Control, квартальна. Решта шести (230 A1/B1/B4, PermitInfo,
-- 20986 Primary Water Use, 2-ТП водгосп, IEC Water, 2-ТП відходи) сюди НЕ
-- йдуть: seed, що заводить сім форм, кожна з яких описана тими самими
-- п'ятьма колонками, виглядав би готовим каталогом і не був би ним —
-- `ФВ-10.9` вимагає для кожної форми ВЛАСНОГО критерію звірки. Решта
-- заводиться через `POST /api/v1/reports` (`Report.EditDefinition`).
MERGE rpt.ReportDef AS t
USING (VALUES (N'IEC',
               N'{"en":"Industrial Environmental Control (quarterly)","ru":"Производственный экологический контроль (квартал)","kz":"Өндірістік экологиялық бақылау (тоқсан)"}',
               1)) AS s (Code, NameL10n, IsRegulatory)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, NameL10n, IsRegulatory, IsActive)
     VALUES (s.Code, s.NameL10n, s.IsRegulatory, 1);
GO

-- ⚠ Версія одразу `Published` (Status = 1), а не чернетка: сховище описів
-- бере ЛИШЕ опубліковане (`IReportDefinitionStore.FindCurrentVersionAsync`),
-- і чернетка в seed дала б рівно те, від чого seed і рятує, — опис, за яким
-- побудова однаково відмовляє.
--
-- ⚠ Колонки — ті самі п'ять, які будівник зрізу справді пише в
-- `rpt.ReportRow` (`ReportSnapshotBuilder.AggregateAsync`), і в тому форматі,
-- який читає `ReportColumnSpec.Parse`. Опис, що обіцяє колонки, яких у зрізі
-- не буде, гірший за відсутній.
MERGE rpt.ReportVersion AS t
USING (
    SELECT d.Id AS ReportDefId, v.[Version], v.ColumnsJson, v.RulesJson
    FROM (VALUES (N'IEC', N'1.0',
                  N'[{"code":"DocumentId","kind":"number"},{"code":"RowKey","kind":"text"},{"code":"OutputCode","kind":"text"},{"code":"Value","kind":"number"},{"code":"SubstanceEntryId","kind":"number"}]',
                  N'{"rowSource":"CalculationResults"}'))
         AS v (Code, [Version], ColumnsJson, RulesJson)
    JOIN rpt.ReportDef AS d ON d.Code = v.Code
) AS s
ON t.ReportDefId = s.ReportDefId AND t.[Version] = s.[Version]
WHEN NOT MATCHED THEN INSERT (ReportDefId, [Version], Status, ColumnsJson, RulesJson, CreatedAt)
     VALUES (s.ReportDefId, s.[Version], 1, s.ColumnsJson, s.RulesJson, SYSUTCDATETIME());
GO
