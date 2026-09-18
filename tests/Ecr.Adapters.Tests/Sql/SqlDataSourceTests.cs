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
/// Джерело, яке НЕ є PI, збирається тим самим портом (ФВ-11.8).
/// </summary>
/// <remarks>
/// ⛔ Доти, доки <c>ExternalTransport</c> знав лише <c>PiWebApi</c> і
/// <c>PiSqlClient</c>, ці тести написати було НЕМОЖЛИВО: обидва значення —
/// транспорти до одного й того самого PI AF, і оголосити FLERT (окрему
/// SQL-базу, `B14` §7) не було чим. Саме тому вимога так довго стояла
/// «звільненою від трасування» з причиною «конфігурація джерела в проді»: у
/// проді її неможливо було й налаштувати.
/// <para>
/// ⚠ База тут — справжня, а не підставна, і це принципово. Предмет перевірки —
/// «чужа SQL-база читається як джерело», і замінити її мок-об'єктом означало б
/// перевірити, що мок повертає те, що в нього поклали.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SqlDataSourceTests(SqlServerFixture sql)
{
    /// <summary>Схема й таблиця, що вдають FLERT: потоки з об'ємами по годинах.</summary>
    /// <remarks>
    /// ⚠ Навмисно НЕ схожа на <c>ext.*</c>: сенс джерела в тому, що його
    /// структуру задає чужа система, а ми лише знаємо контракт результату
    /// запиту (<c>Ts</c>, <c>Val</c>, <c>Uom</c>).
    /// </remarks>
    private const string Ddl = """
        IF SCHEMA_ID(N'flert') IS NULL EXEC(N'CREATE SCHEMA flert');
        IF OBJECT_ID(N'flert.StreamValue') IS NOT NULL DROP TABLE flert.StreamValue;
        CREATE TABLE flert.StreamValue
        (
            StreamCode  nvarchar(100)  NOT NULL,
            ReadingAt   datetime2(3)   NOT NULL,
            Volume      decimal(18, 6)  NULL,
            UnitSymbol  nvarchar(16)    NULL,
            CONSTRAINT PK_StreamValue PRIMARY KEY (StreamCode, ReadingAt)
        );
        """;

    /// <summary>Запит значень так, як його напише інтегратор у конфігурації.</summary>
    /// <remarks>
    /// ⚠ Колонки джерела називаються по-своєму (<c>ReadingAt</c>,
    /// <c>Volume</c>), а до контрактних імен їх приводить сам запит. Саме тому
    /// адаптер не мусить нічого знати про FLERT.
    /// </remarks>
    private const string ValueQuery = """
        SELECT ReadingAt AS Ts, Volume AS Val, UnitSymbol AS Uom
        FROM flert.StreamValue
        WHERE StreamCode = @path AND ReadingAt >= @from AND ReadingAt < @to
        ORDER BY ReadingAt
        """;

    private const string CatalogQuery = """
        SELECT DISTINCT StreamCode AS Code, StreamCode AS DisplayName,
               N'flert.StreamValue' AS EntityPath, UnitSymbol AS Uom, N'decimal' AS DataType
        FROM flert.StreamValue
        """;

    private static readonly DateTime Midnight = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Окрема_SQL_база_читається_як_джерело_і_межа_діапазону_напіввідкрита()
    {
        await SeedAsync(
            (Midnight.AddHours(-1), 5m),   // до діапазону
            (Midnight, 10m),               // межа `from` — включно
            (Midnight.AddHours(1), 20m),
            (Midnight.AddHours(2), 30m));  // межа `to` — ВИКЛЮЧНО

        var adapter = Adapter(ValueQuery);

        var result = await adapter.ReadAsync(
            new CollectionRequest(
                DataSourceId: 1,
                SourceEntityId: 7,
                SourcePath: "STREAM-1",
                FromUtc: Midnight,
                ToUtc: Midnight.AddHours(2),
                MaxPoints: 1000),
            CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Empty(result.FailedIntervals);

        // ⛔ Рівно дві точки, а не три і не чотири. Година до діапазону не
        // потрапляє, година рівно на `to` — теж: інакше сусідні прогони
        // наздоганяння порахували б ту саму точку двічі, і журнал покриття
        // (ER-I-03) став би неправдивим на стику інтервалів.
        Assert.Equal([10m, 20m], result.Points.Select(p => p.ValueNumeric));

        Assert.All(result.Points, p =>
        {
            Assert.Equal("STREAM-1", p.SourcePath);
            Assert.Equal(DateTimeKind.Utc, p.Timestamp.Kind);

            // Одиниця джерела доїжджає як є: конверсія — на межі, із записом
            // у журнал (ФВ-16.9), а не мовчки тут.
            Assert.Equal("m3", p.SourceUnitSymbol);
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Шлях_сутності_іде_параметром_і_не_може_виконатися_як_SQL()
    {
        await SeedAsync((Midnight, 10m));

        var adapter = Adapter(ValueQuery);

        // ⚠ `SourcePath` приходить із НАШОЇ бази (мапінг `ext.EntityFieldMap`),
        // і саме тому спокуса вставити його в текст запиту найбільша: джерело
        // виглядає довіреним. Ін'єкція з власного сховища — найгірший її
        // різновид, бо перевіряти його ніхто не подумає.
        var attack = "STREAM-1'; DROP TABLE flert.StreamValue; --";

        var result = await adapter.ReadAsync(
            new CollectionRequest(1, 7, attack, Midnight.AddDays(-1), Midnight.AddDays(1), 1000),
            CancellationToken.None);

        // Потоку з таким кодом немає — отже порожньо, без винятку.
        Assert.Empty(result.Points);

        // ⛔ І головне: таблиця ціла. Якщо колись хтось замінить параметр на
        // конкатенацію, цей рядок упаде першим.
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM flert.StreamValue"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Код_потоку_з_апострофом_читається_а_не_ламає_запит()
    {
        // ⚠ Той самий доказ, що й вище, але з боку звичайної роботи, а не
        // нападу: код потоку з апострофом — не екзотика, а нормальне ім'я в
        // чужій системі. Підставлений у текст запиту, він дає не «нуль
        // точок», а синтаксичну помилку T-SQL — тобто джерело виглядало б
        // недоступним, і шукали б мережу, а не лапку.
        await SeedAsync(streamCode: "O'BRIEN-1", points: [(Midnight, 42m)]);

        var adapter = Adapter(ValueQuery);

        var result = await adapter.ReadAsync(
            new CollectionRequest(1, 7, "O'BRIEN-1", Midnight, Midnight.AddHours(1), 1000),
            CancellationToken.None);

        Assert.Equal([42m], result.Points.Select(p => p.ValueNumeric));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Каталог_джерела_читається_разом_з_одиницею()
    {
        await SeedAsync((Midnight, 10m));

        var adapter = Adapter(ValueQuery, CatalogQuery);

        var catalog = await adapter.DiscoverAsync(dataSourceId: 1, CancellationToken.None);

        var stream = Assert.Single(catalog);
        Assert.Equal("STREAM-1", stream.Code);
        Assert.Equal("flert.StreamValue", stream.EntityPath);

        // Одиницю видно вже в каталозі — при налаштуванні, а не через місяць
        // на звірці чисел (ФВ-16.9).
        Assert.Equal("m3", stream.SourceUnitSymbol);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Джерело_без_налаштованого_запиту_відмовляє_іменем_ключа_а_не_порожнечею()
    {
        // ⛔ Імена таблиць FLERT — факт про світ замовника, якого в цьому
        // репозиторії немає. Типовий запит «за аналогією» виглядав би як
        // робоче налаштування і мовчки збирав би нуль точок: журнал покриття
        // писався б, наздоганяння не спрацьовувало б, і дірка в даних
        // виявилася б на звірці за квартал.
        var adapter = new SqlDataSource(
            StoreWith("Server=nowhere"), Secrets(), Settings(new Dictionary<string, string>()));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadAsync(
                new CollectionRequest(1, 7, "STREAM-1", Midnight, Midnight.AddHours(1), 10),
                CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);

        // Повідомлення називає ключ, якого бракує: інакше налаштування шукають
        // у коді, якого не буде.
        Assert.Contains("Sql:FLERT:ValueQuery", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Задовгий_шлях_сутності_відхиляється_а_не_обрізається()
    {
        await SeedAsync((Midnight, 10m));

        var adapter = Adapter(ValueQuery);
        var tooLong = new string('x', SqlDataSource.MaxSourcePath + 1);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadAsync(
                new CollectionRequest(1, 7, tooLong, Midnight, Midnight.AddHours(1), 10),
                CancellationToken.None));

        // ⛔ Мовчазне обрізання до 400 символів дало б не помилку, а ПОРОЖНІЙ
        // результат: джерело чесно відповіло б «такої сутності немає», збір
        // записав би покриття, і діру знайшли б на звірці за квартал.
        Assert.Equal("ECR-INT-0503", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.8")]
    public async Task Власний_запит_джерела_має_перевагу_над_спільним()
    {
        await SeedAsync((Midnight, 10m), (Midnight.AddHours(1), 20m));

        // Спільний запит віддає все, власний — лише перший запис. Різні
        // джерела одного транспорту не мусять ділити один SQL: у FLERT своя
        // схема, у наступної SQL-бази буде своя.
        var settings = Settings(new Dictionary<string, string>
        {
            [SqlDataSource.ValueQueryKey] = ValueQuery,
            ["Sql:FLERT:ValueQuery"] = ValueQuery.Replace(
                "SELECT ReadingAt", "SELECT TOP 1 ReadingAt", StringComparison.Ordinal),
        });

        var adapter = new SqlDataSource(StoreWith(sql.ConnectionString), Secrets(), settings);

        var result = await adapter.ReadAsync(
            new CollectionRequest(1, 7, "STREAM-1", Midnight, Midnight.AddHours(5), 1000),
            CancellationToken.None);

        Assert.Equal([10m], result.Points.Select(p => p.ValueNumeric));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-11.3")]
    public async Task Обрізаний_батч_повертає_хвіст_діапазону_в_наздоганяння()
    {
        await SeedAsync(
            (Midnight, 10m), (Midnight.AddHours(1), 20m), (Midnight.AddHours(2), 30m));

        var adapter = Adapter(ValueQuery);

        var result = await adapter.ReadAsync(
            new CollectionRequest(1, 7, "STREAM-1", Midnight, Midnight.AddHours(5), MaxPoints: 2),
            CancellationToken.None);

        Assert.Equal(2, result.Points.Count);

        // ⛔ Хвіст оголошується непрочитаним. Мовчазне «успішно, дві точки»
        // записало б покриття за весь діапазон — і третя точка не з'явилася б
        // ніколи: наздоганяння шукає ДІРКИ в журналі, а діри вже не було б.
        var tail = Assert.Single(result.FailedIntervals);
        Assert.Equal(Midnight.AddHours(1), tail.FromUtc);
        Assert.Equal(Midnight.AddHours(5), tail.ToUtc);
    }

    /// <summary>Адаптер над реальною базою фікстури.</summary>
    /// <param name="valueQuery">Запит значень.</param>
    /// <param name="catalogQuery">Запит каталогу; <c>null</c> — не налаштований.</param>
    private SqlDataSource Adapter(string valueQuery, string? catalogQuery = null)
    {
        var configured = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SqlDataSource.ValueQueryKey] = valueQuery,
        };

        if (catalogQuery is not null)
        {
            configured[SqlDataSource.CatalogQueryKey] = catalogQuery;
        }

        return new SqlDataSource(StoreWith(sql.ConnectionString), Secrets(), Settings(configured));
    }

    /// <summary>Сховище, що віддає одне SQL-джерело.</summary>
    /// <param name="endpoint">Рядок з'єднання джерела.</param>
    private static ICollectionStore StoreWith(string endpoint)
    {
        var store = Substitute.For<ICollectionStore>();

        var source = new DataSource(
            EcrCode.Create("FLERT"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "FLERT" }),

            // ⛔ Ось значення, якого не існувало: джерело, що не є PI.
            ExternalTransport.Sql,
            endpoint,
            "Flert.Primary");

        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);

        return store;
    }

    /// <summary>Секретів немає: фікстура ходить тим, що вже в рядку з'єднання.</summary>
    private static ISecretProvider Secrets() => Substitute.For<ISecretProvider>();

    /// <summary>Канал налаштувань із заданими ключами.</summary>
    /// <param name="values">Ключ → значення.</param>
    private static ISecretProvider Settings(IReadOnlyDictionary<string, string> values)
    {
        var provider = Substitute.For<ISecretProvider>();
        provider.Find(Arg.Any<string>()).Returns(call => values.GetValueOrDefault(call.ArgAt<string>(0)));

        return provider;
    }

    /// <summary>Створює таблицю джерела наново і кладе в неї точки потоку <c>STREAM-1</c>.</summary>
    /// <param name="points">Мітка часу і значення.</param>
    private Task SeedAsync(params (DateTime At, decimal Value)[] points)
        => SeedAsync("STREAM-1", points);

    /// <summary>Те саме, але з явним кодом потоку.</summary>
    /// <param name="streamCode">Код потоку в чужій системі.</param>
    /// <param name="points">Мітка часу і значення.</param>
    private async Task SeedAsync(string streamCode, (DateTime At, decimal Value)[] points)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using (var ddl = new SqlCommand(Ddl, connection))
        {
            await ddl.ExecuteNonQueryAsync();
        }

        foreach (var (at, value) in points)
        {
            await using var insert = new SqlCommand(
                """
                INSERT INTO flert.StreamValue (StreamCode, ReadingAt, Volume, UnitSymbol)
                VALUES (@code, @at, @value, N'm3');
                """,
                connection);

            insert.Parameters.AddWithValue("@code", streamCode);
            insert.Parameters.AddWithValue("@at", at);
            insert.Parameters.AddWithValue("@value", value);

            await insert.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Одне число з бази джерела.</summary>
    /// <param name="query">Запит.</param>
    private async Task<int> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);

        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }
}
