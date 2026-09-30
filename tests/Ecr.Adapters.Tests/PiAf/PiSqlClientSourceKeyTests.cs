using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Ключі запитів PI SQL Client на рівні джерела (рішення людини 2026-09-29: «різні бази на
/// одному AF-сервері»): <c>PiSqlClient:{код}:&lt;Query&gt;</c> перекриває спільний
/// <c>PiSqlClient:&lt;Query&gt;</c> — для КОЖНОГО запиту, не лише подій.
/// </summary>
/// <remarks>
/// Реалізація одна — <see cref="PiSqlClientDataSource.ScopedKey"/> і
/// <see cref="PiSqlClientDataSource.ConfiguredQuery"/> (прийшли зі шляхом подій HSE301/F4e).
/// Вибраний текст іде лише в команду ODBC, тому вибір перевіряється двома шляхами:
/// <see cref="PiSqlClientDataSource.ConfiguredQuery"/> напряму і через адаптер — чи пройдено
/// ворота «запит налаштовано» (далі рядок з'єднання навмисно не читається → <c>ECR-INT-0503</c>
/// <c>.connectionStringBroken</c> без жодного з'єднання) чи ні (<c>ECR-INT-0422</c>).
/// <para>
/// Мутаційні докази (2026-09-29, власний worktree, кожна окремо, після — відкат):
/// М1 — у <c>ConfiguredQuery</c> спершу спільний, потім ключ джерела → червоніє
/// <see cref="Ключ_джерела_перекриває_спільний"/> і <see cref="Два_джерела_отримують_різні_тексти"/>;
/// М2 — у <c>ConfiguredQuery</c> не падати на спільний → червоніє
/// <see cref="Без_ключа_джерела_береться_спільний"/>;
/// М3 — <c>ReadCurrentAsync</c> бере лише спільний ключ (<c>settings.Find(key)</c>) → червоніє
/// <see cref="Поточне_значення_й_інтерпольований_беруть_ключ_свого_джерела"/>;
/// М4 — прибрати <c>sharedConfigKey</c> з відмови → червоніє
/// <see cref="Без_обох_ключів_відмова_0422_називає_обидва"/>, <see cref="Відмова_подій_має_ту_саму_форму_параметрів"/>
/// і три тести «без ключа» (CurrentValue, ElementList, Interpolated).
/// </para>
/// </remarks>
public sealed class PiSqlClientSourceKeyTests
{
    private const string Requirement = "ФВ-8.11";

    /// <summary>Рядок з'єднання, який не розбирається: адаптер відмовить ДО з'єднання.</summary>
    private const string UnparsableEndpoint = "Driver={unterminated";

    [Fact]
    [Trait("Requirement", Requirement)]
    public void Ключ_джерела_має_форму_як_у_SqlDataSource()
    {
        Assert.Equal("PiSqlClient:PIAF_EMIS:ElementListQuery",
            PiSqlClientDataSource.ScopedKey("PIAF_EMIS", PiSqlClientDataSource.ElementListQueryKey));
        Assert.Equal("PiSqlClient:PIAF_EMIS:ValueQuery",
            PiSqlClientDataSource.ScopedKey("PIAF_EMIS", PiSqlClientDataSource.ValueQueryKey));
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public void Ключ_джерела_перекриває_спільний()
    {
        var settings = Settings(
            (PiSqlClientDataSource.ElementListQueryKey, "SHARED"),
            ("PiSqlClient:PIAF:ElementListQuery", "OWN"));

        Assert.Equal("OWN", PiSqlClientDataSource.ConfiguredQuery(settings, "PIAF", PiSqlClientDataSource.ElementListQueryKey));
    }

    [Theory]
    [Trait("Requirement", Requirement)]
    [InlineData(null)]
    [InlineData("   ")]
    public void Без_ключа_джерела_береться_спільний(string? own)
    {
        var settings = Settings(
            (PiSqlClientDataSource.CurrentValueQueryKey, "SHARED"),
            ("PiSqlClient:PIAF:CurrentValueQuery", own));

        Assert.Equal("SHARED", PiSqlClientDataSource.ConfiguredQuery(settings, "PIAF", PiSqlClientDataSource.CurrentValueQueryKey));
        Assert.Null(PiSqlClientDataSource.ConfiguredQuery(Settings(), "PIAF", PiSqlClientDataSource.CurrentValueQueryKey));
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public void Два_джерела_отримують_різні_тексти()
    {
        var settings = Settings(
            (PiSqlClientDataSource.TemplateQueryKey, "SHARED"),
            ("PiSqlClient:AIR:TemplateQuery", "SELECT … FROM [ECR_01_Air].[Element].[Element]"),
            ("PiSqlClient:FLARE:TemplateQuery", "SELECT … FROM [ECR_02_Flare].[Element].[Element]"));

        Assert.Equal("SELECT … FROM [ECR_01_Air].[Element].[Element]",
            PiSqlClientDataSource.ConfiguredQuery(settings, "AIR", PiSqlClientDataSource.TemplateQueryKey));
        Assert.Equal("SELECT … FROM [ECR_02_Flare].[Element].[Element]",
            PiSqlClientDataSource.ConfiguredQuery(settings, "FLARE", PiSqlClientDataSource.TemplateQueryKey));
        Assert.Equal("SHARED",
            PiSqlClientDataSource.ConfiguredQuery(settings, "OTHER", PiSqlClientDataSource.TemplateQueryKey));
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task Адаптер_бере_ключ_свого_джерела_а_сусіднє_відмовляє_його_ключем()
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(1, Arg.Any<CancellationToken>()).Returns(Rtqp("AIR"));
        store.FindDataSourceAsync(2, Arg.Any<CancellationToken>()).Returns(Rtqp("FLARE"));
        var adapter = new PiSqlClientDataSource(
            store, Substitute.For<ISecretProvider>(), Settings(("PiSqlClient:AIR:ElementListQuery", "SELECT ?")));

        // AIR: текст є — далі рядок з'єднання (не розбирається), тобто запит пройшов ворота.
        var passed = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverElementsAsync(1, "Plant", CancellationToken.None));
        Assert.Equal(("ECR-INT-0503", "err.ECR-INT-0503.connectionStringBroken"),
            (passed.ErrorCode, passed.Details!["messageKey"]));

        // FLARE: ключа ні свого, ні спільного — відмова конфігурації з ЙОГО ключем.
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverElementsAsync(2, "Plant", CancellationToken.None));
        Assert.Equal("ECR-INT-0422", refused.ErrorCode);
        Assert.Equal("PiSqlClient:FLARE:ElementListQuery", refused.Details!["sourceConfigKey"]);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task Поточне_значення_й_інтерпольований_беруть_ключ_свого_джерела()
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Rtqp("AIR"));
        var adapter = new PiSqlClientDataSource(
            store,
            Substitute.For<ISecretProvider>(),
            Settings(
                ("PiSqlClient:AIR:CurrentValueQuery", "SELECT ?"),
                ("PiSqlClient:AIR:InterpolatedQuery", "SELECT ?, ?, ?, ?")));

        // Лише ключ джерела, спільного немає: ворота пройдено — далі рядок з'єднання (0503).
        var current = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadCurrentAsync(1, ["EL|Capacity"], CancellationToken.None));
        Assert.Equal(("ECR-INT-0503", "err.ECR-INT-0503.connectionStringBroken"),
            (current.ErrorCode, current.Details!["messageKey"]));

        var interpolated = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadAsync(
                new CollectionRequest(
                    1, 7, "EL|Capacity", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 100, SourceQueryKind.Interpolated,
                    TimeSpan.FromMinutes(1)),
                CancellationToken.None));
        Assert.Equal(("ECR-INT-0503", "err.ECR-INT-0503.connectionStringBroken"),
            (interpolated.ErrorCode, interpolated.Details!["messageKey"]));
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task Без_обох_ключів_відмова_0422_називає_обидва()
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Rtqp("PIAF"));
        var adapter = new PiSqlClientDataSource(store, Substitute.For<ISecretProvider>(), Settings());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadCurrentAsync(1, ["EL|Capacity"], CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotConfigured", error.Details!["messageKey"]);
        Assert.Equal("PiSqlClient:PIAF:CurrentValueQuery", error.Details["sourceConfigKey"]);
        Assert.Equal(PiSqlClientDataSource.CurrentValueQueryKey, error.Details["sharedConfigKey"]);
        Assert.Equal("PiSqlClient:PIAF:CurrentValueQuery / PiSqlClient:CurrentValueQuery", error.Details["configKey"]);
        Assert.Equal("PIAF", error.Details["dataSource"]);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task Відмова_подій_має_ту_саму_форму_параметрів()
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Rtqp("PIAF"));
        var adapter = new PiSqlClientDataSource(store, Substitute.For<ISecretProvider>(), Settings());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverEventTemplatesAsync(1, CancellationToken.None));

        // configKey — заповнювач СВОГО тексту сіду: «set {sourceConfigKey} (or the shared {configKey})».
        Assert.Equal("err.ECR-INT-0422.eventQueryNotConfigured", error.Details!["messageKey"]);
        Assert.Equal("PiSqlClient:PIAF:EventTemplateQuery", error.Details["sourceConfigKey"]);
        Assert.Equal(PiSqlClientDataSource.EventTemplateQueryKey, error.Details["sharedConfigKey"]);
        Assert.Equal(PiSqlClientDataSource.EventTemplateQueryKey, error.Details["configKey"]);
        Assert.Equal("PIAF", error.Details["dataSource"]);
    }

    /// <summary>RTQP-джерело з рядком з'єднання, що не розбирається: з'єднання не буде ніколи.</summary>
    /// <param name="code">Код джерела.</param>
    internal static DataSource Rtqp(string code)
        => new(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = code }),
            ExternalTransport.PiSqlClient,
            UnparsableEndpoint,
            "PiAf." + code);

    /// <summary>Налаштування: лише задані ключі, решта — <c>null</c>.</summary>
    /// <param name="values">Ключ → текст.</param>
    internal static ISecretProvider Settings(params (string Key, string? Value)[] values)
    {
        var settings = Substitute.For<ISecretProvider>();
        settings.Find(Arg.Any<string>()).Returns((string?)null);
        foreach (var (key, value) in values)
        {
            settings.Find(key).Returns(value);
        }

        return settings;
    }
}
