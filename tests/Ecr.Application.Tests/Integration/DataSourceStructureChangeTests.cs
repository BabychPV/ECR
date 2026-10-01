// tests/Ecr.Application.Tests/Integration/DataSourceStructureChangeTests.cs
using Ecr.Application.Common;
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
/// D8: створення й правка з'єднання з джерелом (адреса — SSRF-чутлива) пишуть
/// <c>aud.StructureChange</c> зі старою й новою адресою — без секретів.
/// </summary>
public sealed class DataSourceStructureChangeTests
{
    private const string Secret = "TOP-SECRET-VALUE-42";

    private readonly IDataSourceStore _store = Substitute.For<IDataSourceStore>();
    private readonly ISecretProvider _secrets = Substitute.For<ISecretProvider>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public DataSourceStructureChangeTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.Manage").Build());

        _store.CountUsageAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new DataSourceUsage(0, 0));

        // Підробка UoW виконує замикання транзакції (інакше журнал не запишеться).
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
    }

    private SaveDataSourceHandler Handler()
        => new(_store, _secrets, _access, _uow, _audit, _user, Substitute.For<IClock>());

    private static readonly Dictionary<string, string> Name = new() { ["uk"] = "AF" };

    [Fact]
    public async Task Створення_пише_запис_Create_з_адресою_без_секрету()
    {
        await Handler().CreateAsync(
            "SRC", Name, ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", null, null, null, null, default);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "ext.DataSource" && r.Operation == "Create" && r.OldJson == null
                && r.NewJson!.Contains("https://pi.corp.example/piwebapi", StringComparison.Ordinal)
                && r.ChangedByUserId == 9
                // ent4 P3-1: причина — конверт із ключем каталогу, а не українська фраза.
                && r.ChangeReason == """{"k":"integrationAudit.dataSourceCreated","p":{"connection":"SRC"}}"""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Зміна_адреси_пише_стару_і_нову_адресу_а_секрет_не_потрапляє()
    {
        var source = new DataSource(
            EcrCode.Create("SRC"), new LocalizedText(Name), ExternalTransport.PiWebApi,
            "https://old.corp.example/piwebapi", "DataSource.SRC");
        typeof(DataSource).GetProperty(nameof(DataSource.RowVersion))!.SetValue(source, new byte[] { 1, 2 });
        _store.FindAsync(5, Arg.Any<CancellationToken>()).Returns(source);

        // Секрет середовища заданий — і мусить бути підтверджений, а не потрапити в журнал.
        _secrets.Find("DataSource.SRC").Returns(Secret);

        await Handler().UpdateAsync(
            5, Name, ExternalTransport.PiWebApi, "https://new.corp.example/piwebapi", null, null, null, true,
            "\"AQI=\"", Secret, false, default);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "ext.DataSource" && r.Operation == "Update"
                && r.OldJson!.Contains("old.corp.example", StringComparison.Ordinal)
                && r.NewJson!.Contains("new.corp.example", StringComparison.Ordinal)
                && !r.OldJson.Contains(Secret, StringComparison.Ordinal)
                && !r.NewJson.Contains(Secret, StringComparison.Ordinal)
                && !r.NewJson.Contains("DataSource.SRC", StringComparison.Ordinal)
                && r.ChangeReason == """{"k":"integrationAudit.dataSourceChanged","p":{"connection":"SRC"}}"""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Збереження_без_зміни_налаштувань_шуму_в_журнал_не_дає()
    {
        var source = new DataSource(
            EcrCode.Create("SRC"), new LocalizedText(Name), ExternalTransport.PiWebApi,
            "https://pi.corp.example/piwebapi", "DataSource.SRC");
        source.Configure(null, null, 4);
        typeof(DataSource).GetProperty(nameof(DataSource.RowVersion))!.SetValue(source, new byte[] { 1, 2 });
        _store.FindAsync(5, Arg.Any<CancellationToken>()).Returns(source);

        await Handler().UpdateAsync(
            5, Name, ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", null, null, 4, true,
            "\"AQI=\"", null, false, default);

        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }
}
