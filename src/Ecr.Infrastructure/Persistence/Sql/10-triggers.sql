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
-- Витягнуто ДОСЛІВНО з `02a-db-schema.md` §15.
--
-- `CREATE OR ALTER` робить скрипт ідемпотентним за побудовою.
-- ПОРЯДОК ЗАПУСКУ: після міграцій EF (тригери потребують таблиць).

CREATE OR ALTER TRIGGER cfg.TR_ColumnDef_Immutable
ON cfg.ColumnDef
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted  d ON d.Id = i.Id
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status = 1   -- Published
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
        THROW 50001, N'Структурна зміна колонки в опублікованій версії заборонена. Використайте CloneFrom (ФВ-7.1).', 1;
    END
END;
GO

CREATE OR ALTER TRIGGER cfg.TR_RowDef_Immutable
ON cfg.RowDef
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted  d ON d.Id = i.Id
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status = 1
          AND (ISNULL(i.RowKey,N'') <> ISNULL(d.RowKey,N'')
            OR i.RowKind            <> d.RowKind
            OR i.TableDefId         <> d.TableDefId
            OR ISNULL(i.ParentRowDefId,-1) <> ISNULL(d.ParentRowDefId,-1))
    )
    BEGIN
        THROW 50002, N'Структурна зміна рядка в опублікованій версії заборонена (ФВ-7.1).', 1;
    END
END;
GO

CREATE OR ALTER TRIGGER cfg.TR_FormulaDef_Immutable
ON cfg.FormulaDef
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted d
        JOIN cfg.TableDef t ON t.Id = d.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status = 1
    )
    BEGIN
        THROW 50003, N'Зміна або видалення формули в опублікованій версії заборонені (ФВ-7.1).', 1;
    END
END;
GO