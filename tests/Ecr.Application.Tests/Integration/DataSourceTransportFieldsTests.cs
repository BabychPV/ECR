using System.Net;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Збереження джерела відмовляє формам і полям, яких адаптер транспорту не вміє прочитати.
/// </summary>
/// <remarks>
/// ⛔ Дефекти (знайдено на підказках діалогу «Нове з'єднання»):
/// <list type="bullet">
/// <item>голе ім'я сервера для Sql / PiSqlClient зберігалося (політика адреси — про хости), а адаптери
/// розбирають Endpoint як рядок з'єднання й падали лише під час збору (<c>connectionStringBroken</c>);</item>
/// <item>запасна адреса Sql і каталог PiSqlClient зберігалися, хоча їх ніхто не читає.</item>
/// </list>
/// ⛔ МУТАЦІЇ (доведено вручну): прибрати перевірку «немає <c>=</c>» у <c>RequireSqlServerAddressAsync</c> —
/// червоні обидва рядки «голе ім'я»; прибрати виклик <c>RequireReadByTransport</c> — червоні обидва тести
/// поля, яке транспорт не читає.
/// </remarks>
public sealed class DataSourceTransportFieldsTests
{
    private sealed class FakeNetwork : IEndpointNetwork
    {
        public IReadOnlyList<string> AllowedHosts => [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.0.0.5")]);
    }

    private static SaveDataSourceHandler Handler(IDataSourceStore? store = null)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(11);
        access.BuildProfileAsync(11, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 11 }.Permission("Integration.Manage").Build());

        return new SaveDataSourceHandler(
            store ?? Substitute.For<IDataSourceStore>(), Substitute.For<ISecretProvider>(), access,
            Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), user, Substitute.For<IClock>(),
            new FakeNetwork());
    }

    private static async Task<BusinessRuleException?> TryCreate(
        ExternalTransport transport, string endpoint, string? secondary = null, string? catalog = null)
    {
        try
        {
            await Handler().CreateAsync(
                "SRC", new Dictionary<string, string> { ["uk"] = "Джерело" }, transport,
                endpoint, secondary, catalog, null, null, default);
            return null;
        }
        catch (BusinessRuleException e)
        {
            return e;
        }
    }

    [Theory]
    [InlineData(ExternalTransport.Sql, "sql01.corp.local")]
    [InlineData(ExternalTransport.Sql, "sql01.corp.local,1433")]
    [InlineData(ExternalTransport.PiSqlClient, "af.corp.local")]
    [InlineData(ExternalTransport.PiSqlClient, "af.corp.local:5461")]
    public async Task Голе_ім_я_сервера_відхиляється_ключем_рядка_з_єднання(ExternalTransport transport, string endpoint)
    {
        var refused = await TryCreate(transport, endpoint);

        Assert.NotNull(refused);
        Assert.Equal(SaveDataSourceHandler.NotConnectionStringKey, refused.Details!["messageKey"]);
        Assert.Equal("endpoint", refused.Details!["field"]);
    }

    [Theory]
    [InlineData(ExternalTransport.Sql, "Server=sql01.corp.local;Database=Reports;Integrated Security=true")]
    [InlineData(ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af.corp.local;AF Database=ECR;Integrated Security=SSPI")]
    [InlineData(ExternalTransport.PiSqlClient, "DSN=PiSqlEcr")]
    public async Task Рядок_з_єднання_проходить(ExternalTransport transport, string endpoint)
        => Assert.Null(await TryCreate(transport, endpoint));

    [Fact]
    public async Task Голе_ім_я_в_запасній_адресі_PiSqlClient_теж_відхиляється()
    {
        var refused = await TryCreate(
            ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af.corp.local", secondary: "af02.corp.local");

        Assert.Equal(SaveDataSourceHandler.NotConnectionStringKey, refused!.Details!["messageKey"]);
        Assert.Equal("secondaryEndpoint", refused.Details!["field"]);
    }

    [Fact]
    public async Task Запасна_адреса_Sql_не_зберігається_бо_Sql_її_не_читає()
    {
        var refused = await TryCreate(
            ExternalTransport.Sql, "Server=sql01.corp.local;Integrated Security=true",
            secondary: "Server=sql02.corp.local;Integrated Security=true");

        Assert.Equal(SaveDataSourceHandler.FieldNotReadKey, refused!.Details!["messageKey"]);
        Assert.Equal("secondaryEndpoint", refused.Details!["field"]);

        // Для PiSqlClient і PiWebApi запасна адреса діє (EndpointFailover).
        Assert.Null(await TryCreate(
            ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af.corp.local",
            secondary: "Driver={PI SQL Client};AF Server=af02.corp.local"));
    }

    [Fact]
    public async Task Каталог_PiSqlClient_не_зберігається_бо_PiSqlClient_його_не_читає()
    {
        var refused = await TryCreate(
            ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af.corp.local", catalog: "ECR");

        Assert.Equal(SaveDataSourceHandler.FieldNotReadKey, refused!.Details!["messageKey"]);
        Assert.Equal("catalog", refused.Details!["field"]);

        // Sql і PiWebApi каталог читають.
        Assert.Null(await TryCreate(
            ExternalTransport.Sql, "Server=sql01.corp.local;Integrated Security=true", catalog: "Reports"));
        Assert.Null(await TryCreate(ExternalTransport.PiWebApi, "https://pi.corp.local/piwebapi", catalog: "ECR"));
    }

    [Fact]
    public async Task Правка_з_уже_збереженим_каталогом_PiSqlClient_теж_відмовляє_а_з_очищеним_проходить()
    {
        var stored = new DataSource(
            EcrCode.Create("AF1"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "AF" }),
            ExternalTransport.PiSqlClient,
            "Driver={PI SQL Client};AF Server=af.corp.local",
            "DataSource.AF1");
        stored.Configure(null, "ECR", 4);
        typeof(DataSource).GetProperty(nameof(DataSource.RowVersion))!.SetValue(stored, new byte[] { 1 });

        var store = Substitute.For<IDataSourceStore>();
        store.FindAsync(7, Arg.Any<CancellationToken>()).Returns(stored);
        store.CountUsageAsync(7, Arg.Any<CancellationToken>()).Returns(new DataSourceUsage(0, 0));

        Task Update(string? catalog) => Handler(store).UpdateAsync(
            7, new Dictionary<string, string> { ["uk"] = "AF" }, ExternalTransport.PiSqlClient,
            stored.Endpoint, null, catalog, 4, true, "\"AQ==\"", null, false, default);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Update("ECR"));
        Assert.Equal(SaveDataSourceHandler.FieldNotReadKey, refused.Details!["messageKey"]);
        Assert.Equal("ECR", stored.Catalog);

        await Update(null);
        Assert.Null(stored.Catalog);
    }
}
