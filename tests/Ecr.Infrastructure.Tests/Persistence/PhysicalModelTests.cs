using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перевіряє фізичну модель на **реальному** SQL Server.
/// </summary>
/// <remarks>
/// Ці інваріанти неможливо перевірити на SQLite, а помилка в них виявляється
/// не при написанні коду, а на 108 млн рядків — коли міняти вже дорого.
/// </remarks>
[Collection("SqlServer")]
public sealed class PhysicalModelTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Первинний_ключ_CellValue_починається_з_партиційного_стовпця()
    {
        var key = await ScalarAsync<string>("""
            SELECT STUFF((SELECT N',' + c.name
                          FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = OBJECT_ID(N'doc.CellValue')
                            AND ic.index_id = 1 AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, N'')
            """);

        // Партиційний стовпець ПЕРШИЙ — інакше кластерний індекс не вирівняний
        // зі схемою, і TRUNCATE … WITH (PARTITIONS) неможливий.
        Assert.Equal("PeriodKey,TableRowId,ColumnDefId", key);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task CellValue_не_має_сурогатного_Id()
    {
        var count = await ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'doc.CellValue') AND name = N'Id'")
            ;

        // Сурогат коштував би ~0.9 ГБ/рік і не давав би нічого (R-A1).
        Assert.Equal(0, count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Усі_унікальні_індекси_партиційованих_таблиць_містять_PeriodKey()
    {
        // Унікальність без партиційного стовпця SQL Server або відхилить, або
        // (гірше) зробить індекс невирівняним — і партиційні операції відпадуть.
        var offenders = await QueryAsync("""
            SELECT SCHEMA_NAME(t.schema_id) + N'.' + t.name + N'.' + i.name
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.is_unique = 1
              AND ds.type_desc = N'PARTITION_SCHEME'
              AND NOT EXISTS (SELECT 1
                              FROM sys.index_columns ic
                              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                AND ic.is_included_column = 0 AND c.name = N'PeriodKey')
            """);

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Партиційовані_таблиці_лежать_на_схемі_партиціонування()
    {
        // ⚠ Регресія на Q-035: міграція EF кладе таблиці на PRIMARY, бо
        // ON ps_ByPeriodKey(PeriodKey) вона виставити не вміє. Якщо
        // 07-partition-tables.sql випаде з розгортання, архівація мовчки
        // перестане звільняти місце — без жодної помилки.
        var misplaced = await QueryAsync("""
            SELECT SCHEMA_NAME(t.schema_id) + N'.' + t.name + N'.' + i.name + N' -> ' + ds.name
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.type IN (1, 2)
              AND t.name IN (N'CellValue', N'TableRow', N'TableInstance')
              AND SCHEMA_NAME(t.schema_id) = N'doc'
              AND ds.type_desc <> N'PARTITION_SCHEME'
            """);

        Assert.Empty(misplaced);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кластерний_індекс_CellValue_стиснений_сторінково()
    {
        var compression = await ScalarAsync<string>("""
            SELECT TOP (1) p.data_compression_desc
            FROM sys.partitions p
            WHERE p.object_id = OBJECT_ID(N'doc.CellValue') AND p.index_id = 1
            """);

        // На ~108 млн рядків на рік це не оптимізація, а умова, за якої обсяг
        // узагалі керований.
        Assert.Equal("PAGE", compression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Складений_FK_не_дає_записати_комірку_в_колонку_чужої_таблиці()
    {
        // Ключ саме складений — (TableDefId, ColumnDefId) на (TableDefId, Id).
        // FK лише на ColumnDefId пропустив би комірку в колонку чужої таблиці.
        var columns = await QueryAsync("""
            SELECT pc.name + N'->' + rc.name
            FROM sys.foreign_key_columns fkc
            JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.name = N'FK_CellValue_Column'
            ORDER BY fkc.constraint_column_id
            """);

        Assert.Equal(["TableDefId->TableDefId", "ColumnDefId->Id"], columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task FK_на_рядок_складений_із_PeriodKey_і_TableRowId()
    {
        var columns = await QueryAsync("""
            SELECT pc.name + N'->' + rc.name
            FROM sys.foreign_key_columns fkc
            JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.name = N'FK_CellValue_Row'
            ORDER BY fkc.constraint_column_id
            """);

        // PeriodKey у ключі означає, що комірка не може перетнути межу партиції.
        Assert.Equal(["PeriodKey->PeriodKey", "TableRowId->Id"], columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Немає_жодної_колонки_типу_float_у_результатних_таблицях()
    {
        // D-30: порядок додавання float змінює результат, і звірка з еталоном
        // стає неможливою. Забороняється в усіх схемах даних, не лише в doc.
        var offenders = await QueryAsync("""
            SELECT SCHEMA_NAME(t.schema_id) + N'.' + t.name + N'.' + c.name + N' : ' + ty.name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE ty.name IN (N'float', N'real')
              AND SCHEMA_NAME(t.schema_id) IN (N'doc', N'calc', N'cfg', N'dic', N'uom', N'rpt')
            """);

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Запит_за_один_період_читає_рівно_одну_партицію()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Явна_порожнеча_без_значень_проходить_CHECK_а_з_значенням_ні()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Конверсію_між_різними_розмірностями_неможливо_вставити_в_таблицю()
        => Assert.Fail("not implemented");

    private async Task<T?> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }

    private async Task<List<string>> QueryAsync(string query)
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
