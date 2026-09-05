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

-- 13-cache-table.sql
-- Таблиця розподіленого кешу (Microsoft.Extensions.Caching.SqlServer).
--
-- ⚠ Виконує DBA або SQL Agent, а не застосунок: у PROD обліковий запис
-- застосунку DDL-прав не має і мати не повинен (D-66).
--
-- ⚠ Кеш саме розподілений, бо в ньому живе diff імпорту між переглядом і
-- застосуванням (ФВ-4.3). Інстансів застосунку кілька, і другий запит цілком
-- може потрапити не на той, що будував diff: у памʼяті процесу він знайшовся б
-- лише коли пощастило — а це найгірший вид поломки, бо відтворюється в одному
-- запуску з десяти.
--
-- Схема таблиці задана самим пакетом і змінам не підлягає: колонки й типи
-- читає його власний провайдер.

IF OBJECT_ID(N'dbo.Cache', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Cache
    (
        Id                          nvarchar(449) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL,
        Value                       varbinary(MAX)   NOT NULL,
        ExpiresAtTime               datetimeoffset   NOT NULL,
        SlidingExpirationInSeconds  bigint           NULL,
        AbsoluteExpiration          datetimeoffset   NULL,
        CONSTRAINT PK_Cache PRIMARY KEY CLUSTERED (Id)
    );
END
GO

-- Індекс за строком життя: прибирання протермінованих записів інакше
-- сканувало б таблицю цілком при кожному зверненні до кешу.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'Index_ExpiresAtTime'
               AND object_id = OBJECT_ID(N'dbo.Cache'))
BEGIN
    CREATE NONCLUSTERED INDEX Index_ExpiresAtTime ON dbo.Cache (ExpiresAtTime);
END
GO
