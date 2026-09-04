using System.Text.RegularExpressions;
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
        // ⚠ Перевіряється СВІЙ партиційний стовпець кожного індексу, а не
        // буквально PeriodKey: aud.* партиційовані за ChangedAt, і вимагати від
        // них PeriodKey означало б перевіряти не той інваріант. Правильний
        // інваріант один: унікальний індекс на партиційованій таблиці мусить
        // містити стовпець, за яким її розрізано, — інакше SQL Server або
        // відхилить його, або зробить невирівняним, і партиційні операції
        // відпадуть.
        var offenders = await QueryAsync("""
            SELECT SCHEMA_NAME(t.schema_id) + N'.' + t.name + N'.' + i.name
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.is_unique = 1
              AND ds.type_desc = N'PARTITION_SCHEME'
              AND NOT EXISTS (SELECT 1
                              FROM sys.index_columns ic
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                AND ic.is_included_column = 0
                                AND ic.partition_ordinal > 0)
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

    // ⚠ Тест доданий після Q-060: сім сутностей без конфігурації EF лягали
    // конвенцією в `dbo` з множинним іменем, і міграція створювала таблиці,
    // яких у `02a-db-schema.md` немає. `SchemaValidator` цього не бачить —
    // він звіряє список міграцій, а не форму схеми. Перевірка потрібна саме
    // на розгорнутій базі: вона ловить і модель EF, і `.sql`-скрипти разом.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Міграція_не_створює_таблиць_поза_контрактними_схемами()
    {
        // `__EFMigrationsHistory` — єдина дозволена таблиця в `dbo`: її кладе
        // туди сам EF, і в контрактній схемі їй місця немає за побудовою.
        var strays = await QueryAsync("""
            SELECT s.name + N'.' + t.name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name NOT IN (N'cfg', N'doc', N'calc', N'rpt', N'ext', N'sec',
                                 N'wf', N'dic', N'uom', N'arc', N'aud', N'itg', N'sys_ecr')
              AND t.name <> N'__EFMigrationsHistory'
            ORDER BY 1
            """);

        Assert.Empty(strays);
    }

    // ⚠ Тест доданий після Q-071 — і саме він мав би зловити сам Q-071.
    // Попередній сторож перевіряв ЛИШЕ зворотний бік: що не створено зайвого.
    // Питання «а чи створено все» не ставив ніхто, і `aud.SimulationSession`
    // пролежала непоміченою від Q-049 — скрипт, написаний рівно проти цього
    // класу дефектів, перелічив шість таблиць на око і зробив п'ять.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кожна_таблиця_контрактної_схеми_існує_або_явно_відкладена()
    {
        var declared = ContractTables();
        var existing = (await QueryAsync("""
            SELECT s.name + N'.' + t.name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            ORDER BY 1
            """)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = declared.Except(existing, StringComparer.OrdinalIgnoreCase)
                              .Except(Deferred, StringComparer.OrdinalIgnoreCase)
                              .OrderBy(t => t, StringComparer.Ordinal)
                              .ToList();

        Assert.Empty(missing);

        // Список відкладених має ЗМЕНШУВАТИСЯ. Таблиця, яку вже створили, але
        // забули прибрати звідси, знову робить пропуск невидимим.
        var stale = Deferred.Intersect(existing, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Empty(stale);
    }

    /// <summary>
    /// Таблиці, які свідомо ще не створюються, і етап, що їх принесе.
    /// </summary>
    /// <remarks>
    /// Це не «дозволені винятки», а розклад: кожен етап прибирає свій блок, і
    /// порожній список означає, що схема розгорнута повністю.
    /// </remarks>
    private static readonly string[] Deferred =
    [
        // Етап 4 — реєстри, одиниці, розрахунки
        "dic.RegistryEntry", "dic.RegistryEntryLink", "dic.RegistryExternalKey", "dic.RegistryValue",
        "calc.CalculationInput", "calc.CalculationResult", "calc.CalculationRun", "calc.CalculationStep",
        "calc.Methodology", "calc.MethodologyConstant", "calc.MethodologyFormula",
        "calc.MethodologyOutput", "calc.MethodologyRule", "calc.MethodologySubstance",
        "calc.MethodologyVersion", "calc.ScriptVersion", "calc.SubmissionSnapshot",

        // Етап 5 — інтеграція, звітність, архів
        "arc.CalculationResult", "arc.CalculationStep", "arc.CellChange", "arc.CellValue",
        "arc.TableInstance", "arc.TableRow",
        "ext.CollectionSchedule", "ext.ConsistencyRule", "ext.DataSource", "ext.EntityFieldMap",
        "ext.LegacyColumnMapping", "ext.LegacyRowMapping", "ext.LegacySheetMapping",
        "ext.LegacyTableMapping", "ext.RawDataPoint", "ext.SourceEntity",
        "itg.ArchiveRun", "itg.CollectionCoverage", "itg.CollectionRun",
        "itg.JobProgress", "itg.MaintenanceRun",
        "rpt.ReportDef", "rpt.ReportRow", "rpt.ReportSnapshot", "rpt.ReportVersion",

        // Індекс IsIndexed-полів для фільтрів по документах; наповнює шлях запису.
        "doc.DocumentIndexValue",
    ];

    /// <summary>Таблиці, оголошені в <c>02a-db-schema.md</c>.</summary>
    private static List<string> ContractTables()
    {
        var path = Path.Combine(SolutionRoot(), "docs", "build", "02a-db-schema.md");
        return Regex.Matches(File.ReadAllText(path), @"CREATE TABLE \[?([a-z_]+)\]?\.\[?(\w+)\]?")
                    .Select(m => $"{m.Groups[1].Value}.{m.Groups[2].Value}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Ecr.sln не знайдено вище за каталог збірки.");
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
    public async Task Запит_за_один_період_читає_рівно_одну_партицію()
    {
        // Дані у двох різних періодах: якщо відсікання не працює, запит за
        // один період усе одно дістане обидві партиції.
        var january = await BuildAsync(202601);
        var february = await BuildAsync(202602);

        await InsertCellAsync(january);
        await InsertCellAsync(february);

        var partitions = await QueryAsync($"""
            SELECT CAST($PARTITION.pf_ByPeriodKey(PeriodKey) AS nvarchar(10))
            FROM doc.CellValue
            WHERE PeriodKey = {january.PeriodKey.Value}
            GROUP BY $PARTITION.pf_ByPeriodKey(PeriodKey)
            """);

        // Рядки одного періоду лежать рівно в одній партиції — це і є умова,
        // за якої TRUNCATE … WITH (PARTITIONS) звільняє рік одним рухом.
        Assert.Single(partitions);

        var both = await QueryAsync("""
            SELECT CAST($PARTITION.pf_ByPeriodKey(PeriodKey) AS nvarchar(10))
            FROM doc.CellValue
            GROUP BY $PARTITION.pf_ByPeriodKey(PeriodKey)
            """);

        // І різні періоди справді розкладені по різних партиціях, а не
        // склеєні в одну — інакше перша перевірка була б порожньою обіцянкою.
        Assert.True(both.Count >= 2, $"очікували ≥2 партиції, отримали {both.Count}");
        _ = february;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Явна_порожнеча_без_значень_проходить_CHECK_а_з_значенням_ні()
    {
        var doc = await BuildAsync(202603);

        // Явна порожнеча: рядок є, значень немає. Так і має бути (R-B4).
        await ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 0, 1)
            """);

        // А порожнеча ЗІ значенням — суперечність, і її не має пропускати
        // база, а не код: інакше третій стан тримався б на домовленості.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[1]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 1, 0, 1)
            """));

        Assert.Contains("CK_CellValue_Empty", error.Message, StringComparison.Ordinal);
    }

    private async Task<TestDocument> BuildAsync(int periodKey)
        => await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: periodKey, ct: CancellationToken.None);

    private Task InsertCellAsync(TestDocument doc)
        => ExecuteAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 1, 0, 0)
            """);

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

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
