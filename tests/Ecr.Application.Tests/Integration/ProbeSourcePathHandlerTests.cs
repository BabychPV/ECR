// tests/Ecr.Application.Tests/Integration/ProbeSourcePathHandlerTests.cs
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
/// «Перевірити конфігурацію» до першого збору (ФВ-13.17): пробне читання
/// одного значення, і підказка схожих імен, коли шляху немає в каталозі.
/// </summary>
public sealed class ProbeSourcePathHandlerTests
{
    private const int Actor = 11;
    private const int SourceId = 3;
    private const string ElementPath = @"\Db\Unit-01";
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDataSourceStore _store = Substitute.For<IDataSourceStore>();
    private readonly ISourceCatalogReader _reader = Substitute.For<ISourceCatalogReader>();
    private readonly IExternalDataSource _adapter = Substitute.For<IExternalDataSource>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ProbeSourcePathHandlerTests()
    {
        _user.UserId.Returns(Actor);
        _clock.UtcNow.Returns(Now);
        Allow("Integration.Manage");

        var source = new DataSource(
            EcrCode.Create("PI_MAIN"), new LocalizedText(new Dictionary<string, string> { ["en"] = "PI" }),
            ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", "DataSource.PI_MAIN");
        typeof(Entity<int>).GetProperty("Id")!.SetValue(source, SourceId);
        _store.FindAsync(SourceId, Arg.Any<CancellationToken>()).Returns(source);

        _adapter.Transport.Returns(ExternalTransport.PiWebApi);

        // Каталог рівня `\Db\Unit-01`: атрибути Flow/Temp/Pressure, без дітей-елементів.
        _reader.BrowseAsync(SourceId, ElementPath, Arg.Any<CancellationToken>()).Returns([]);
        _reader.AttributesAsync(SourceId, ElementPath, Arg.Any<CancellationToken>()).Returns(
        [
            Attribute("Flow", "t/h"),
            Attribute("Temp", "C"),
            Attribute("Pressure", "bar"),
        ]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    public async Task Успішна_проба_повертає_значення_і_одиницю()
    {
        _adapter.ReadAsync(
            Arg.Is<CollectionRequest>(r => r.SourcePath == $@"{ElementPath}|Flow"), Arg.Any<CancellationToken>())
            .Returns(new CollectionResult(
                [new SourceDataPoint($@"{ElementPath}|Flow", Now, 12.5m, null, "t/h", "Good")], [], null));

        var result = await Handler().HandleAsync(SourceId, $@"{ElementPath}|Flow", default);

        Assert.True(result.HasValue);
        Assert.Equal(12.5m, result.ValueNumeric);
        Assert.Equal("t/h", result.UnitSymbol);
        Assert.Equal(Now, result.Timestamp);
        Assert.Equal("Good", result.Quality);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    public async Task Валідний_шлях_без_точок_у_вікні_дає_HasValue_false_а_не_відмову()
    {
        _adapter.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CollectionResult([], [], null));

        var result = await Handler().HandleAsync(SourceId, $@"{ElementPath}|Flow", default);

        Assert.False(result.HasValue);
        Assert.Null(result.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    public async Task Неіснуючий_шлях_дає_404_з_підказками_за_відстанню_Левенштейна()
    {
        var refused = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(SourceId, $@"{ElementPath}|Flow2", default));

        Assert.Equal("ECR-INT-0404", refused.ErrorCode);
        Assert.Equal("err.ECR-INT-0404.sourcePathNotFound", refused.Details!["messageKey"]);

        // "Flow2" відрізняється від "Flow" на одну вставку, від решти — набагато
        // більше: найближче ім'я мусить стояти першим.
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.Details["suggestions"]);
        Assert.Equal("Flow", suggestions[0]);
        Assert.Equal(["Flow", "Temp", "Pressure"], suggestions);

        await _adapter.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    public async Task Порожній_рівень_каталогу_дає_404_без_підказок()
    {
        _reader.BrowseAsync(SourceId, @"\Db\Empty", Arg.Any<CancellationToken>()).Returns([]);
        _reader.AttributesAsync(SourceId, @"\Db\Empty", Arg.Any<CancellationToken>()).Returns([]);

        var refused = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(SourceId, @"\Db\Empty|Flow", default));

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.Details!["suggestions"]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    public async Task Джерело_що_мовчить_за_межею_дає_503_а_не_висить()
    {
        _reader.AttributesAsync(SourceId, ElementPath, Arg.Any<CancellationToken>())
            .Returns(call => Hang(call.Arg<CancellationToken>()));

        var handler = new ProbeSourcePathHandler(
            _store, _reader, [_adapter], new SourceCatalogPolicy(TimeSpan.FromMilliseconds(200)), _access, _user,
            _clock);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(SourceId, $@"{ElementPath}|Flow", default).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("ECR-INT-0503", refused.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.probeTimeout", refused.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    public async Task Відмова_транспорту_після_підтвердження_шляху_дає_503()
    {
        _adapter.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<CollectionResult>(_ => throw new HttpRequestException("connection refused"));

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(SourceId, $@"{ElementPath}|Flow", default));

        Assert.Equal("err.ECR-INT-0503.probeUnavailable", refused.Details!["messageKey"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Порожній_шлях_дає_422(string? path)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(SourceId, path, default));

        Assert.Equal("err.ECR-REQ-0422.probePathInvalid", refused.Details!["messageKey"]);
        Assert.Empty(_reader.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.17")]
    public async Task Лише_View_не_відкриває_пробу()
    {
        Allow("Integration.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(SourceId, $@"{ElementPath}|Flow", default));
        await _reader.DidNotReceiveWithAnyArgs().BrowseAsync(default, default, default);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("Flow", "Flow", 0)]
    [InlineData("Flow", "Flow2", 1)]
    [InlineData("flow", "FLOW", 0)]
    [InlineData("cat", "dog", 3)]
    [InlineData("", "abc", 3)]
    public void EditDistance_рахує_Левенштейна_без_урахування_регістру(string a, string b, int expected)
        => Assert.Equal(expected, ProbeSourcePathHandler.EditDistance(a, b));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData(@"\Db\Unit-01|Flow", @"\Db\Unit-01", "Flow")]
    [InlineData(@"\Db\Unit-01", @"\Db", "Unit-01")]
    [InlineData("RootElement", null, "RootElement")]
    public void SplitPath_ділить_на_рівень_і_лист(string path, string? expectedParent, string expectedLeaf)
    {
        var (parent, leaf) = ProbeSourcePathHandler.SplitPath(path);

        Assert.Equal(expectedParent, parent);
        Assert.Equal(expectedLeaf, leaf);
    }

    private static async Task<IReadOnlyList<SourceEntityDescriptor>> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

        return [];
    }

    private static SourceEntityDescriptor Attribute(string code, string unit)
        => new(code, null, $@"{ElementPath}|{code}", unit, "Float64");

    private ProbeSourcePathHandler Handler()
        => new(_store, _reader, [_adapter], new SourceCatalogPolicy(TimeSpan.FromSeconds(10)), _access, _user, _clock);

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Actor };

        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }
}
