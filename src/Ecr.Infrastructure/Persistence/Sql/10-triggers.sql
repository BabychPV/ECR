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

-- src/Ecr.Infrastructure/Persistence/Sql/10-triggers.sql
--
-- Тригери незмінності опублікованої структури (ФВ-7.1).
--
-- ⚠ ЧОМУ ЦЕ ОКРЕМИЙ СКРИПТ
-- `HasTrigger()` у конфігурації EF **не створює тригера**. Він лише каже EF
-- Core не користуватися `OUTPUT`-клаузою на цій таблиці — інакше `SaveChanges`
-- падає в рантаймі (ТЗ §13.5 п.1). Сам тригер має створити хтось інший, і
-- досі це не робив ніхто: `migrationBuilder` тригерів не вміє, а серед
-- скриптів такого не було (`Q-045`).
--
-- Наслідок відсутності тихий і дорогий: структурна зміна колонки в
-- ОПУБЛІКОВАНІЙ версії проходить без жодної помилки, і посилання у виразах
-- та історичні дані мовчки починають означати інше.
--
-- Витягнуто з `02a-db-schema.md` §15 і розширено D5 (2026-09-30), див. нижче.
--
-- `CREATE OR ALTER` робить скрипт ідемпотентним за побудовою.
-- ПОРЯДОК ЗАПУСКУ: після міграцій EF (тригери потребують таблиць).
--
-- ⛔ D5 (аудит безпеки): ЗАМОРОЖЕНА версія — це `Status IN (1, 2)`, тобто
-- Published І Deprecated (`TemplateVersion.IsStructurallyFrozen`); виведена з
-- обігу версія так само незмінна, проєкти на ній працюють далі. Раніше тригери
-- бачили лише `Status = 1` і мовчали про решту:
--   * Deprecated — структурний UPDATE проходив;
--   * INSERT — у чужу опубліковану версію можна було докласти колонку, рядок
--     чи формулу повз публікаційні перевірки (C5 закрив це лише в застосунку);
--   * DELETE колонки й рядка — тригер стояв лише на UPDATE (формула мала й
--     DELETE);
--   * перенесення `TableDefId` — версія бралася за НОВОЮ таблицею, тож рядок,
--     винесений ІЗ замороженої таблиці в чернетку, виглядав правкою чернетки;
--     формула ж дивилася лише на СТАРУ таблицю, тож внесення чернеткової
--     формули В заморожену проходило. Тепер перевіряються обидві.
--
-- Дозволено, як і раніше: усе в Draft (вставка, правка, видалення, перенесення
-- між чернетками); презентаційні поля колонки/рядка (підпис, порядок, формат,
-- прихованість, стиль) у будь-якому стані — `PatchPresentationHandler`.
-- Легальні переходи стану (`Draft → Published → Deprecated`) міняють лише
-- `cfg.TemplateVersion` і дочірніх таблиць не зачіпають.
--
-- Помилки — `THROW` зі стабільним номером; текст несе ключ каталогу
-- `[ECR-TMPL-0409 structurallyFrozen]` (той самий, що дає застосунок):
--   50001 колонка: структурний UPDATE     50004 INSERT у заморожену версію
--   50002 рядок:   структурний UPDATE     50005 DELETE колонки/рядка
--   50003 формула: UPDATE/DELETE
-- Застосунок ці номери не мапить: до бази вони доходять лише повз обробники,
-- які самі відмовляють раніше (`EnsureDraftUnderLockAsync`).
--
-- ⚠ Тригер працює над УСІМ набором `inserted`/`deleted`, а не над першим
-- рядком: один рядок замороженої версії в багаторядковій команді валить її
-- цілком. Рядок вважається новим, коли його Id немає в `deleted`, і видаленим,
-- коли немає в `inserted` (Id — IDENTITY, тож UPDATE його не міняє).

CREATE OR ALTER TRIGGER cfg.TR_ColumnDef_Immutable
ON cfg.ColumnDef
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    -- INSERT: нова колонка в замороженій версії.
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)   -- Published, Deprecated
          AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id)
    )
    BEGIN
        THROW 50004, N'Додати колонку в опубліковану або виведену з обігу версію заборонено [ECR-TMPL-0409 structurallyFrozen]. Використайте CloneFrom (ФВ-7.1).', 1;
    END

    -- DELETE: колонку замороженої версії видаляти не можна (лише м'яке
    -- видалення в Draft; застосунок жорстко не видаляє нічого).
    IF EXISTS (
        SELECT 1
        FROM deleted d
        JOIN cfg.TableDef t ON t.Id = d.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)
          AND NOT EXISTS (SELECT 1 FROM inserted i WHERE i.Id = d.Id)
    )
    BEGIN
        THROW 50005, N'Видалити колонку опублікованої або виведеної з обігу версії заборонено [ECR-TMPL-0409 structurallyFrozen]. Використайте CloneFrom (ФВ-7.1).', 1;
    END

    -- UPDATE: структурні поля; таблиця береться і СТАРА, і НОВА (перенесення).
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted  d ON d.Id = i.Id
        JOIN cfg.TableDef t ON t.Id IN (d.TableDefId, i.TableDefId)
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)   -- Published, Deprecated
          AND (ISNULL(i.Code,N'')       <> ISNULL(d.Code,N'')
            OR i.DataType               <> d.DataType
            -- ⚠ CAST у int ОБОВ'ЯЗКОВИЙ: Precision і Scale — tinyint, а
            -- ISNULL(tinyint, -1) намагається втиснути -1 у tinyint і падає з
            -- Msg 220 «Arithmetic overflow». Без цього тригер валив БУДЬ-ЯКИЙ
            -- UPDATE опублікованої колонки, зокрема презентаційний, і сама
            -- перевірка незмінності не виконувалася жодного разу (Q-046).
            OR ISNULL(CAST(i.Precision AS int),-1) <> ISNULL(CAST(d.Precision AS int),-1)
            OR ISNULL(CAST(i.Scale AS int),-1)     <> ISNULL(CAST(d.Scale AS int),-1)
            OR i.IsRequired             <> d.IsRequired
            OR ISNULL(i.LookupRegistryDefId,-1) <> ISNULL(d.LookupRegistryDefId,-1)
            OR ISNULL(i.UnitId,-1)      <> ISNULL(d.UnitId,-1)
            OR i.IsBusinessKey          <> d.IsBusinessKey
            OR i.TableDefId             <> d.TableDefId)
    )
    BEGIN
        THROW 50001, N'Структурна зміна колонки в опублікованій або виведеній з обігу версії заборонена [ECR-TMPL-0409 structurallyFrozen]. Використайте CloneFrom (ФВ-7.1).', 1;
    END
END;
GO

CREATE OR ALTER TRIGGER cfg.TR_RowDef_Immutable
ON cfg.RowDef
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    -- INSERT: новий рядок у замороженій версії.
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)
          AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id)
    )
    BEGIN
        THROW 50004, N'Додати рядок в опубліковану або виведену з обігу версію заборонено [ECR-TMPL-0409 structurallyFrozen] (ФВ-7.1).', 1;
    END

    -- DELETE: рядок замороженої версії видаляти не можна.
    IF EXISTS (
        SELECT 1
        FROM deleted d
        JOIN cfg.TableDef t ON t.Id = d.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)
          AND NOT EXISTS (SELECT 1 FROM inserted i WHERE i.Id = d.Id)
    )
    BEGIN
        THROW 50005, N'Видалити рядок опублікованої або виведеної з обігу версії заборонено [ECR-TMPL-0409 structurallyFrozen] (ФВ-7.1).', 1;
    END

    -- UPDATE: структурні поля; таблиця береться і СТАРА, і НОВА (перенесення).
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted  d ON d.Id = i.Id
        JOIN cfg.TableDef t ON t.Id IN (d.TableDefId, i.TableDefId)
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)
          AND (ISNULL(i.RowKey,N'') <> ISNULL(d.RowKey,N'')
            OR i.RowKind            <> d.RowKind
            OR i.TableDefId         <> d.TableDefId
            OR ISNULL(i.ParentRowDefId,-1) <> ISNULL(d.ParentRowDefId,-1))
    )
    BEGIN
        THROW 50002, N'Структурна зміна рядка в опублікованій або виведеній з обігу версії заборонена [ECR-TMPL-0409 structurallyFrozen] (ФВ-7.1).', 1;
    END
END;
GO

CREATE OR ALTER TRIGGER cfg.TR_FormulaDef_Immutable
ON cfg.FormulaDef
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    -- INSERT: нова формула в замороженій версії (повз публікаційні перевірки
    -- й граф залежностей — `cfg.FormulaDependency` про неї не знав би).
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)
          AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id)
    )
    BEGIN
        THROW 50004, N'Додати формулу в опубліковану або виведену з обігу версію заборонено [ECR-TMPL-0409 structurallyFrozen] (ФВ-7.1).', 1;
    END

    -- UPDATE/DELETE: будь-яка зміна чи видалення формули замороженої версії.
    -- Стара таблиця (`deleted`) — правка, видалення і перенесення ІЗ замороженої.
    IF EXISTS (
        SELECT 1
        FROM deleted d
        JOIN cfg.TableDef t ON t.Id = d.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)
    )
    BEGIN
        THROW 50003, N'Зміна або видалення формули в опублікованій або виведеній з обігу версії заборонені [ECR-TMPL-0409 structurallyFrozen] (ФВ-7.1).', 1;
    END

    -- Нова таблиця (`inserted`, лише для UPDATE — рядок є і в `deleted`):
    -- перенесення чернеткової формули У заморожену версію.
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted d ON d.Id = i.Id
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status IN (1, 2)
    )
    BEGIN
        THROW 50003, N'Зміна або видалення формули в опублікованій або виведеній з обігу версії заборонені [ECR-TMPL-0409 structurallyFrozen] (ФВ-7.1).', 1;
    END
END;
GO

-- ФВ-2.6/2.7: правила умовного форматування належать структурі версії —
-- у Published/Deprecated (Status 1, 2) незмінні, як формули (50003/50004).
CREATE OR ALTER TRIGGER cfg.TR_ConditionalFormatRule_Immutable
ON cfg.ConditionalFormatRule
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN cfg.TemplateVersion v ON v.Id = i.TemplateVersionId
        WHERE v.Status IN (1, 2)
          AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.Id = i.Id)
    )
    BEGIN
        THROW 50004, N'Додати правило умовного форматування в опубліковану або виведену з обігу версію заборонено [ECR-TMPL-0409 structurallyFrozen] (ФВ-2.7).', 1;
    END

    IF EXISTS (
        SELECT 1
        FROM deleted d
        JOIN cfg.TemplateVersion v ON v.Id = d.TemplateVersionId
        WHERE v.Status IN (1, 2)
    )
    BEGIN
        THROW 50003, N'Зміна або видалення правила умовного форматування в опублікованій або виведеній з обігу версії заборонені [ECR-TMPL-0409 structurallyFrozen] (ФВ-2.7).', 1;
    END
END;
GO
