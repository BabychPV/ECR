-- src/Ecr.Infrastructure/Persistence/Sql/08-system-tables.sql
--
-- Таблиці схеми `sys_ecr` — системні реєстри і каталог рядків інтерфейсу.
--
-- ⚠ ЧОМУ НЕ МІГРАЦІЯ EF
-- У жодної з цих таблиць **немає доменної сутності** — і це навмисно
-- (`05b-skeleton-domain.md` їх не оголошує). Доступ до каталогу йде через порт
-- `IUiStringCatalog` (`02-contracts.md` §5), а не через `DbSet`, бо це не
-- предметна модель, а довідник рядків, який читається зрізом на мову і
-- кешується за `ETag`. А що не в моделі EF — того міграція не створить.
-- Без цього скрипта seed падає на першому ж `MERGE sys_ecr.Language`.
--
-- Той самий поділ, що й у `07-partition-tables.sql`: EF володіє тим, що є в
-- моделі; скрипти — тим, чого в ній немає.
--
-- Скрипт ідемпотентний: усе під `IF NOT EXISTS`.
-- ПОРЯДОК ЗАПУСКУ: після міграцій EF, перед seed.

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF SCHEMA_ID(N'sys_ecr') IS NULL
    EXEC(N'CREATE SCHEMA sys_ecr AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'sys_ecr.Language', N'U') IS NULL
BEGIN
    CREATE TABLE sys_ecr.Language
    (
        Code        nvarchar(8)   NOT NULL,   -- 'en', 'ru', 'kz'
        NameNative  nvarchar(100) NOT NULL,
        Ordinal     int           NOT NULL,
        IsDefault   bit           NOT NULL CONSTRAINT DF_Language_Default DEFAULT(0),
        IsActive    bit           NOT NULL CONSTRAINT DF_Language_Active  DEFAULT(1),
        CONSTRAINT PK_Language PRIMARY KEY (Code)
    );

    -- Рівно одна мова за замовчуванням
    CREATE UNIQUE INDEX UX_Language_Default ON sys_ecr.Language (IsDefault) WHERE IsDefault = 1;
END
GO

IF OBJECT_ID(N'sys_ecr.SystemSetting', N'U') IS NULL
BEGIN
    CREATE TABLE sys_ecr.SystemSetting
    (
        [Key]       nvarchar(100)  NOT NULL,
        Value       nvarchar(max)  NULL,
        Description nvarchar(400)  NULL,
        ModifiedAt  datetime2(3)   NOT NULL,
        ModifiedByUserId int       NULL,
        CONSTRAINT PK_SystemSetting PRIMARY KEY ([Key])
    );
END
GO

-- Каталог рядків інтерфейсу (D-95, ФВ-14.9). Тут лежить chrome клієнта —
-- меню, кнопки, підписи полів, тексти помилок форм. Причина: ФВ-14.9 обіцяє
-- «додати мову = запис у реєстр, не збірка клієнта», а це виконувано лише
-- тоді, коли рядки самого UI теж приходять із сервера.
IF OBJECT_ID(N'sys_ecr.UiString', N'U') IS NULL
BEGIN
    CREATE TABLE sys_ecr.UiString
    (
        [Key]        nvarchar(200) NOT NULL,   -- 'grid.paste.confirm', 'nav.templates'
        LanguageCode nvarchar(8)   NOT NULL,
        Value        nvarchar(1000) NOT NULL,
        -- 0 = Public: віддається АНОНІМНО (сторінка входу, помилки автентифікації,
        --     загальний chrome). 1 = Private: лише після входу.
        -- Причина розділення (D-114): анонімний каталог із підписами
        -- адміністративних областей і назвами прав розкрив би поверхню
        -- функціоналу тому, хто ще не увійшов (суперечило б ФВ-14.2).
        Scope        tinyint       NOT NULL CONSTRAINT DF_UiString_Scope DEFAULT(1),
        ModifiedAt   datetime2(3)  NOT NULL,
        ModifiedByUserId int       NULL,
        CONSTRAINT PK_UiString PRIMARY KEY ([Key], LanguageCode),
        CONSTRAINT FK_UiString_Lang FOREIGN KEY (LanguageCode)
            REFERENCES sys_ecr.Language (Code),
        CONSTRAINT CK_UiString_Scope CHECK (Scope IN (0, 1))
    );

    CREATE INDEX IX_UiString_Lang_Scope ON sys_ecr.UiString (LanguageCode, Scope)
        INCLUDE ([Key], Value);
END
GO

-- Версія каталогу: змінюється будь-яким записом у UiString і слугує ETag.
-- Без неї клієнт або тягне каталог щоразу, або показує застарілі підписи.
IF OBJECT_ID(N'sys_ecr.UiStringRevision', N'U') IS NULL
BEGIN
    CREATE TABLE sys_ecr.UiStringRevision
    (
        Id         tinyint       NOT NULL CONSTRAINT CK_UiRev_Single CHECK (Id = 1),
        Revision   int           NOT NULL,
        ModifiedAt datetime2(3)  NOT NULL,
        CONSTRAINT PK_UiStringRevision PRIMARY KEY (Id)
    );
END
GO
