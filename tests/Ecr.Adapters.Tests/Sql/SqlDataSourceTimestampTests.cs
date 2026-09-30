using Ecr.Adapters.Sql;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Sql;

/// <summary>
/// Мітка часу точки SQL-джерела: тип колонки, пояс і порядок рядків (аудит A7).
/// </summary>
/// <remarks>
/// ⛔ Спільне в усіх трьох дефектах — мовчання. Непрочитана мітка відкидала
/// рядок через <c>continue</c>, і порожній необрізаний батч записував покриття
/// за весь інтервал; невпорядкований запит давав хибний «хвіст» обрізаного
/// батча, і точки раніше за нього не дозбирались ніколи. Наздоганяння шукає
/// ДІРКИ в журналі покриття, тож обидві діри лишалися невидимими.
/// <para>
/// ⚠ Таблиця своя (<c>flert.ReadingA7</c>), не <c>flert.StreamValue</c>
/// сусіднього класу: база фікстури спільна, і чужа DDL посеред прогону
/// зносила б дані.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SqlDataSourceTimestampTests(SqlServerFixture sql)
{
    private static readonly DateTime Midnight = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Мітка_datetimeoffset_зі_зсувом_дає_точку_в_UTC_а_не_відкидається()
    {
        await CreateAsync("datetimeoffset(3)");
        await InsertAsync(new DateTimeOffset(2026, 3, 1, 5, 0, 0, TimeSpan.FromHours(3)), 10m);   // 02:00Z
        await InsertAsync(new DateTimeOffset(2026, 3, 1, 1, 30, 0, TimeSpan.FromHours(-1)), 20m); // 02:30Z

        var result = await Adapter(Ordered).ReadAsync(
            Request(Midnight, Midnight.AddHours(6), maxPoints: 1000), CancellationToken.None);

        // ⛔ Дві точки, а не нуль. Доти `is not DateTime` відкидав обидві, і
        // прогін записував покриття за шість годин без жодної точки.
        Assert.Equal([10m, 20m], result.Points.Select(p => p.ValueNumeric));

        // Час — момент на осі UTC, а не «годинник» джерела: 05:00+03:00 — це
        // 02:00Z, і саме так він мусить лягти поруч із точками інших джерел.
        Assert.Equal(
            [Midnight.AddHours(2), Midnight.AddHours(2.5)],
            result.Points.Select(p => p.Timestamp));
        Assert.All(result.Points, p => Assert.Equal(DateTimeKind.Utc, p.Timestamp.Kind));
        Assert.Empty(result.FailedIntervals);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Мітка_без_поясу_вважається_UTC_тим_самим_годинником_що_й_межі_запиту()
    {
        // ⚠ Фіксує ПРИПУЩЕННЯ, а не знання: поля поясу в ext.DataSource немає,
        // і в якому поясі FLERT пише `datetime2`, документи не кажуть. Обрано
        // UTC, бо межі @from/@to ідуть у запит теж як UTC — фільтр і мітки в
        // одній шкалі. Конвертація «з місцевого часу машини» зсунула б ряд на
        // пояс сервера ECR, про який джерело не знає нічого.
        await CreateAsync("datetime2(3)");
        await InsertAsync(Midnight.AddHours(10), 10m);

        var result = await Adapter(Ordered).ReadAsync(
            Request(Midnight, Midnight.AddDays(1), maxPoints: 1000), CancellationToken.None);

        var point = Assert.Single(result.Points);
        Assert.Equal(Midnight.AddHours(10), point.Timestamp);
        Assert.Equal(DateTimeKind.Utc, point.Timestamp.Kind);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    [InlineData("CONVERT(nvarchar(40), ReadingAt, 126)", "String")]
    [InlineData("CAST(NULL AS datetime2)", "NULL")]
    public async Task Мітка_непідтримуваного_типу_відмовляє_а_не_дає_порожній_зібраний_інтервал(
        string tsExpression, string valueType)
    {
        await CreateAsync("datetime2(3)");
        await InsertAsync(Midnight.AddHours(1), 10m);

        var query = $"""
            SELECT {tsExpression} AS Ts, Volume AS Val
            FROM flert.ReadingA7
            WHERE StreamCode = @path AND ReadingAt >= @from AND ReadingAt < @to
            ORDER BY ReadingAt
            """;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Adapter(query).ReadAsync(
                Request(Midnight, Midnight.AddDays(1), maxPoints: 1000), CancellationToken.None));

        // ⛔ Відмова, а не `Points = []` без обрізання: той результат прогін
        // читав як «інтервал зібрано повністю» і писав покриття.
        Assert.Equal(IExternalDataSource.QueryRefusedCode, error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.timestampUnreadable", error.Details!["messageKey"]);
        Assert.Equal(valueType, error.Details["valueType"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.3")]
    [InlineData(2)]
    [InlineData(1000)]
    public async Task Запит_у_зворотному_порядку_відмовляє_а_не_оголошує_хибний_хвіст(int maxPoints)
    {
        await CreateAsync("datetime2(3)");
        await InsertAsync(Midnight, 10m);
        await InsertAsync(Midnight.AddHours(1), 20m);
        await InsertAsync(Midnight.AddHours(2), 30m);

        // Доти при стелі 2: прочитано 02:00 і 01:00, хвіст [01:00, 05:00) — і
        // точка 00:00 не потрапляла ні в батч, ні в наздоганяння. При стелі
        // 1000 хвоста немає, але відмова та сама: контракт — ORDER BY за
        // міткою, і дефект конфігурації не мусить чекати великого батча, щоб
        // проявитися.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Adapter(Ordered.Replace("ORDER BY ReadingAt", "ORDER BY ReadingAt DESC", StringComparison.Ordinal))
                .ReadAsync(Request(Midnight, Midnight.AddHours(5), maxPoints), CancellationToken.None));

        Assert.Equal(IExternalDataSource.QueryRefusedCode, error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.timestampsOutOfOrder", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.3")]
    public async Task Непрочитаний_рядок_раніший_за_впорядкований_префікс_теж_відмова()
    {
        await CreateAsync("datetime2(3)");
        await InsertAsync(Midnight, 10m);
        await InsertAsync(Midnight.AddHours(1), 20m);
        await InsertAsync(Midnight.AddHours(2), 30m);

        // ⚠ Прочитаний префікс (01:00, 02:00) впорядкований — порушення лише в
        // першому НЕпрочитаному рядку (00:00). Хвіст [02:00, 05:00) без цієї
        // перевірки загубив би 00:00 так само, як і зворотний порядок.
        var query = Ordered.Replace(
            "ORDER BY ReadingAt",
            "ORDER BY CASE WHEN ReadingAt < DATEADD(hour, 1, @from) THEN 1 ELSE 0 END, ReadingAt",
            StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Adapter(query).ReadAsync(
                Request(Midnight, Midnight.AddHours(5), maxPoints: 2), CancellationToken.None));

        Assert.Equal("err.ECR-INT-0422.timestampsOutOfOrder", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.3")]
    public async Task Рівно_MaxPoints_рядків_це_прочитаний_інтервал_а_не_хвіст()
    {
        await CreateAsync("datetime2(3)");
        await InsertAsync(Midnight, 10m);
        await InsertAsync(Midnight.AddHours(1), 20m);

        var result = await Adapter(Ordered).ReadAsync(
            Request(Midnight, Midnight.AddHours(5), maxPoints: 2), CancellationToken.None);

        // Обрізання визначає НАСТУПНИЙ рядок, а не лічильник: доти два рядки при
        // стелі два давали хвіст [01:00, 05:00) і зайвий прохід наздоганяння.
        Assert.Equal(2, result.Points.Count);
        Assert.Empty(result.FailedIntervals);
    }

    /// <summary>Запит значень із <c>ORDER BY</c> — так, як його мусить писати інтегратор.</summary>
    private const string Ordered = """
        SELECT ReadingAt AS Ts, Volume AS Val
        FROM flert.ReadingA7
        WHERE StreamCode = @path AND ReadingAt >= @from AND ReadingAt < @to
        ORDER BY ReadingAt
        """;

    private static CollectionRequest Request(DateTime from, DateTime to, int maxPoints)
        => new(DataSourceId: 1, SourceEntityId: 7, SourcePath: "STREAM-1", from, to, maxPoints);

    /// <summary>Адаптер над реальною базою фікстури з одним запитом значень.</summary>
    /// <param name="valueQuery">Запит значень.</param>
    private SqlDataSource Adapter(string valueQuery)
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new DataSource(
            EcrCode.Create("FLERT"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "FLERT" }),
            ExternalTransport.Sql,
            sql.ConnectionString,
            "Flert.Primary"));

        var settings = Substitute.For<ISecretProvider>();
        settings.Find(SqlDataSource.ValueQueryKey).Returns(valueQuery);

        return new SqlDataSource(store, Substitute.For<ISecretProvider>(), settings);
    }

    /// <summary>Створює таблицю наново з колонкою мітки заданого типу.</summary>
    /// <param name="tsType">Тип SQL колонки мітки.</param>
    private async Task CreateAsync(string tsType)
        => await ExecuteAsync($"""
            IF SCHEMA_ID(N'flert') IS NULL EXEC(N'CREATE SCHEMA flert');
            IF OBJECT_ID(N'flert.ReadingA7') IS NOT NULL DROP TABLE flert.ReadingA7;
            CREATE TABLE flert.ReadingA7
            (
                StreamCode nvarchar(100)  NOT NULL,
                ReadingAt  {tsType}       NOT NULL,
                Volume     decimal(18, 6) NULL
            );
            """);

    /// <summary>Кладе точку потоку <c>STREAM-1</c>.</summary>
    /// <param name="at">Мітка — <see cref="DateTime"/> або <see cref="DateTimeOffset"/>.</param>
    /// <param name="value">Значення.</param>
    private async Task InsertAsync(object at, decimal value)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var insert = new SqlCommand(
            "INSERT INTO flert.ReadingA7 (StreamCode, ReadingAt, Volume) VALUES (N'STREAM-1', @at, @value);",
            connection);

        insert.Parameters.AddWithValue("@at", at);
        insert.Parameters.AddWithValue("@value", value);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(text, connection);
        await command.ExecuteNonQueryAsync();
    }
}
