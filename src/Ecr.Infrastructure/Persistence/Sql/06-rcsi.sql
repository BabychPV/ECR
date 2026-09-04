-- src/Ecr.Infrastructure/Persistence/Sql/06-rcsi.sql
-- Обов'язково: без RCSI пік «останнього дня періоду» упирається в блокування
-- (D-29). Рівень SNAPSHOT НЕ вмикаємо — він важчий і дає конфлікти оновлення.
ALTER DATABASE [Ecr] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
ALTER DATABASE [Ecr] SET ALLOW_SNAPSHOT_ISOLATION OFF;
GO
