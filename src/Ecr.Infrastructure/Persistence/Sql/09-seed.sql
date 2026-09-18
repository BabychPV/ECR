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
  (N'Calculation.Recalculate',  N'Calculation', 0), (N'Calculation.ManageRequiredInputs', N'Calculation', 0),
  (N'Report.ViewRegulatory',    N'Report',      0), (N'Report.BuildSnapshot', N'Report',      0),
  (N'Report.MarkSubmitted',     N'Report',      0), (N'Report.Export',        N'Report',      0),
  -- ⚠ НЕБЕЗПЕЧНЕ (1) навмисно, і не через ризик втратити дані. Причина в
  -- фільтрі нижче: `Approver` має шаблон `Report.%`, виданий тоді, коли всі
  -- права цієї родини були «дивитися, будувати, подавати, вивантажувати».
  -- `Report.EditDefinition` — інша річ: це авторство ДЕРЖАВНОЇ ФОРМИ
  -- (`ФВ-10.4`), і мовчки роздати його кожному погоджувачу лише тому, що воно
  -- починається на `Report.`, означало б змінити повноваження людей правкою
  -- одного рядка каталогу. Адміністратор видає його свідомо, і в журналі
  -- безпеки видно, хто це зробив (`DangerousPermissionsGranted`).
  (N'Report.EditDefinition',    N'Report',      1),
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
    (N'state.errorUnknown',              N'en', N'An unexpected error occurred. Retry; if it repeats, contact support and describe what you were doing.', 0),
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
    (N'err.ECR-AUTH-0403.requiresPermission', N'en', N'Requires permission', 1),
    (N'err.ECR-AUTH-0423', N'en', N'The account is locked.', 0),
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
    (N'err.ECR-SEC-0409',  N'en', N'Role code already in use', 1),
    (N'err.ECR-SEC-0409.roleCodeTaken', N'en', N'A role with code "{code}" already exists.', 1),
    (N'err.ECR-PRJ-0409',  N'en', N'Project code already in use', 1),
    (N'err.ECR-PRJ-0409.projectCodeTaken', N'en', N'A project with code "{code}" already exists.', 1),
    (N'err.ECR-REG-0409',  N'en', N'Registry entry code already in use', 1),
    (N'err.ECR-REG-0409.entryCodeTaken', N'en', N'An entry with code "{code}" already exists in this registry (Id {id}).', 1),
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
    (N'err.validityWindowEmpty', N'en', N'Empty validity window: the exclusive end {to} is not later than the start {from}.', 1),
    (N'err.ECR-REQ-0422.auditWindowOrder',   N'en', N'The end of the audit window must be later than the start.', 1),
    (N'err.ECR-REQ-0422.auditWindowTooWide', N'en', N'The audit window is wider than {maxDays} days: the request would scan every partition.', 1),
    (N'err.ECR-IMP-0422.notAWorkbook',       N'en', N'The file cannot be read as an .xlsx workbook.', 1),
    (N'err.ECR-CALC-0422.constantNoValue',   N'en', N'A numeric constant needs a value: an empty number is not "zero by default" — it is a decision nobody made.', 1),
    (N'err.ECR-CALC-0422.constantNoUnit',    N'en', N'A numeric constant needs a unit: the dimension check cannot run without it.', 1),
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
    (N'err.ECR-CALC-0437.requiredInputs',    N'en', N'Required methodology input columns are empty: {rowCount} row(s) with an error.', 1),
    (N'err.ECR-CELL-4223.missingEntry',      N'en', N'Reference to a registry entry that does not exist: {cellCount} cell(s).', 1),
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

    -- ⛔ Узагальнений репозиторій (`Repository<T,TId>.GetAsync`) будував
    -- повідомлення з ІМЕНІ КЛАСУ .NET: «TemplateVersion з ідентифікатором 5
    -- не знайдено». Для оператора це не назва нічого — у продукті немає
    -- сутності «TemplateVersion», є «версія шаблону». Ключ окремий на КОЖЕН
    -- тип, хоч код у двох із них спільний: один ключ на код сказав би «не
    -- знайдено шаблон» там, де немає ВЕРСІЇ, і людина шукала б не те.
    (N'err.ECR-TMPL-0404.template',          N'en', N'Template {templateId} was not found.', 1),
    (N'err.ECR-TMPL-0404.templateVersion',   N'en', N'Template version {versionId} was not found.', 1),
    (N'err.ECR-ROW-0404.tableRow',           N'en', N'Table row {rowId} was not found.', 1),
    (N'err.ECR-REG-0404.registryEntry',      N'en', N'Registry entry {entryId} was not found.', 1),

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
    (N'err.ECR-DOC-0422',   N'en', N'Invalid document composition', 1),
    (N'err.ECR-ROW-0404',   N'en', N'Row not found', 1),
    (N'err.ECR-ROW-0409',   N'en', N'Row key conflict', 1),
    (N'err.ECR-CELL-0409',  N'en', N'Edit conflict', 1),
    (N'err.ECR-CELL-0422',  N'en', N'Invalid cell value', 1),
    (N'err.ECR-CELL-4221',  N'en', N'The cell is computed', 1),
    (N'err.ECR-CELL-4222',  N'en', N'Value out of range', 1),
    (N'err.ECR-CELL-4223',  N'en', N'Reference to a missing registry entry', 1),
    (N'err.ECR-SUB-4221',   N'en', N'Orphaned rows block submission', 1),

    -- Періоди і проєкти.
    (N'err.ECR-PRD-0409',   N'en', N'The period is closed', 1),
    (N'err.ECR-PRD-0404',   N'en', N'Period not found', 1),
    (N'err.ECR-PRD-0422',   N'en', N'The period is outside the project', 1),
    (N'err.ECR-PRD-4223',   N'en', N'Reopen is blocked by a closed period', 1),
    (N'err.ECR-PRD-4224',   N'en', N'Invalid period sequence', 1),
    (N'err.ECR-PRD-4225',   N'en', N'Invalid period policy', 1),
    (N'err.ECR-PRD-4091',   N'en', N'The period policy code is taken', 1),
    (N'err.ECR-PRJ-0404',   N'en', N'Project not found', 1),
    (N'err.ECR-PRJ-0422',   N'en', N'The project cannot be activated', 1),
    (N'err.ECR-CFG-4221',   N'en', N'Invalid project time zone', 1),

    -- Реєстри і одиниці.
    (N'err.ECR-REG-0404',   N'en', N'Registry entry not found', 1),
    (N'err.ECR-REG-0422',   N'en', N'The registry source cannot be switched in an open period', 1),
    (N'err.ECR-UOM-0404',   N'en', N'Unit not found', 1),
    (N'err.ECR-UOM-0422',   N'en', N'Incompatible unit dimensions', 1),
    (N'err.ECR-UOM-4221',   N'en', N'Contextual conversion coefficient', 1),

    -- Розрахунки і методології.
    (N'err.ECR-CALC-0404',  N'en', N'Methodology version not found', 1),
    (N'err.ECR-CALC-0409',  N'en', N'A second pair of eyes is required', 1),
    (N'err.ECR-CALC-0422',  N'en', N'The methodology version cannot be published', 1),
    (N'err.ECR-CALC-0431',  N'en', N'Unsupported operator in a formula', 1),
    (N'err.ECR-CALC-0432',  N'en', N'Undeclared formula argument', 1),
    (N'err.ECR-CALC-0433',  N'en', N'Extension function in Legacy mode', 1),
    (N'err.ECR-CALC-0437',  N'en', N'Required methodology inputs are empty', 1),
    (N'err.ECR-CALC-0438',  N'en', N'Formula argument has no matching column', 1),
    (N'err.ECR-CALC-4221',  N'en', N'Recalculation of a closed period', 1),

    -- Імпорт та інтеграція.
    (N'err.ECR-IMP-0422',   N'en', N'The workbook does not match the template', 1),
    (N'err.ECR-INT-0404',   N'en', N'Source entity not found', 1),
    (N'err.ECR-INT-0405',   N'en', N'Mapping target not found', 1),
    (N'err.ECR-INT-0422',   N'en', N'The source unit of measure changed', 1),
    (N'err.ECR-INT-0502',   N'en', N'The data source refused authentication', 1),
    (N'err.ECR-INT-0503',   N'en', N'The data source is unavailable', 1),

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
    (N'document.submit',                 N'en', N'Submit', 1),
    (N'document.submitted',              N'en', N'The sheet has been submitted.', 1),
    (N'document.export',                 N'en', N'Export to Excel', 1),
    (N'document.exportBuilding',         N'en', N'Building...', 1),
    (N'document.exportReady',            N'en', N'Download the workbook', 1),
    (N'document.exportFailed',           N'en', N'Export failed.', 1),
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
    (N'grid.saving',                     N'en', N'Saving...', 1),
    (N'grid.saved',                      N'en', N'Saved', 1),
    (N'grid.saveError',                  N'en', N'Not saved — see the error above', 1),
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
    (N'jobs.title',                      N'en', N'Jobs', 1),
    (N'jobs.id',                         N'en', N'Job id', 1),
    (N'jobs.watch',                      N'en', N'Watch', 1),
    (N'jobs.recentEmpty',                N'en', N'No jobs yet.', 1),
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
    (N'jobs.restart',                    N'en', N'Restart', 1),
    (N'jobs.restarting',                 N'en', N'Restarting…', 1),
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
    -- ⛔ Аудит-пас 5: старий текст надсилав до переліку шаблонів по ідентифікатор
    -- версії, а той список показує лише номер версії (`1.0.0.0`), не id —
    -- ідентифікатор видно ЛИШЕ в адресному рядку відкритої версії.
    (N'version.diffOtherHint',           N'en', N'The other version to compare against — open it and copy the id from its URL (…/versions/{id}).', 1),
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
    -- ⛔ Аудит-пас 8, lane7, п.11: те саме пояснення, але НА самій сторінці
    -- версії — раніше воно жило лише всередині діалогу клонування.
    (N'version.structureFrozen',         N'en', N'This published version is frozen: structural changes go through "Clone version".', 1),
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
    (N'documents.name',                  N'en', N'Document name', 1),
    (N'documents.nameHint',              N'en', N'Optional. Shown next to the business key; does not replace it.', 1),
    (N'documents.groupRuleRequiresAll',  N'en', N'Group "{group}": {picked} of {total} sheets selected — the group requires all of them.', 1),
    (N'documents.groupRuleRequiresOne',  N'en', N'Group "{group}": requires at least one sheet.', 1),

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
    -- ⚠ Показано, коли `changedByUserId` не знайшовся в переліку користувачів:
    -- нема права `Security.ManageUsers`, або користувача видалено.
    (N'registries.userUnresolved',       N'en', N'unresolved', 1),
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
    -- Q-298: підказка біля недоступної кнопки «Зберегти» у формі створення
    -- проєкту — перелік бракуючих полів замість мовчазної недоступності
    -- кнопки без жодного пояснення (`CreateDocumentModal.tsx` має той самий
    -- дефект і поки що без цього фіксу).
    (N'periods.stillNeeded',             N'en', N'Still needed: {fields}', 1),
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
    (N'snapshots.empty',                 N'en', N'No snapshots built yet', 1),
    (N'snapshots.emptyHint',             N'en', N'SSRS reads snapshots, not live data: until one is built, the regulator sees nothing.', 1),
    (N'snapshots.pickReport',            N'en', N'Pick a report', 1),
    (N'snapshots.noPublished',           N'en', N'No report definition has a published version yet: a snapshot can only be built from one.', 1),

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
    -- Редактор зв'язків між таблицями (ФВ-2.12, ФВ-2.13)
    (N'version.relations',               N'en', N'Table relations', 1),
    (N'tables.relationsTitle',           N'en', N'Table relations', 1),
    (N'tables.optionalHint',             N'en', N'Relations are optional: a template with none simply has independent tables, and that is a valid design.', 1),
    (N'tables.newRelation',              N'en', N'New relation', 1),
    (N'tables.relationForm',             N'en', N'Relation', 1),
    (N'tables.noRelations',              N'en', N'This version has no table relations', 1),
    (N'tables.noRelationsHint',          N'en', N'Add one when a table must take its numbers from another; otherwise leave it empty.', 1),
    (N'tables.readOnly',                 N'en', N'Published version: relations are frozen', 1),
    (N'tables.readOnlyHint',             N'en', N'A relation decides where a table takes its numbers from, so changing it would silently change forms already submitted. Clone the version to change it (ФВ-7.1).', 1),
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
    (N'columns.partialDataWarning',      N'en', N'This column carries fields not shown here (precision, lookup, unit, default value, style). Saving will clear them unless you already edited this column in this session.', 1),
    (N'columns.errCode',                 N'en', N'Give the column a code: it is how the column is addressed.', 1),
    -- ⛔ Q-338: та сама причина, що `tableDef.errCodeInvalid` (Q-336).
    (N'columns.errCodeInvalid',          N'en', N'The code can contain only Latin letters, digits, and underscores, and must start with a letter.', 1),
    (N'columns.errHeader',               N'en', N'Give the column a header in at least one language.', 1),
    (N'columns.errScale',                N'en', N'Scale cannot exceed precision.', 1),
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
    (N'jobs.retryScheduled',                        N'en', N'Attempt {attempt}/{max} in {delaySeconds}s after error: {error}', 1)
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
IF @@ROWCOUNT > 0
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
-- бере ЛИШЕ опубліковане (`IReportDefinitionStore.FindCurrentVersionIdAsync`),
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
