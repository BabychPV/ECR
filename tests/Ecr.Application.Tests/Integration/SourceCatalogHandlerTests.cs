// tests/Ecr.Application.Tests/Integration/SourceCatalogHandlerTests.cs
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

/// <summary>Каталог імен джерела для мапінгу (ФВ-13.13): сторінки, пошук, межа очікування.</summary>
public sealed class SourceCatalogHandlerTests
{
    private const int Actor = 11;
    private const int SourceId = 3;

    private readonly IDataSourceStore _store = Substitute.For<IDataSourceStore>();
    private readonly ISourceCatalogReader _reader = Substitute.For<ISourceCatalogReader>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public SourceCatalogHandlerTests()
    {
        _user.UserId.Returns(Actor);
        Allow("Integration.Manage");

        var source = new DataSource(
            EcrCode.Create("PI_MAIN"), new LocalizedText(new Dictionary<string, string> { ["en"] = "PI" }),
            ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", "DataSource.PI_MAIN");
        typeof(Entity<int>).GetProperty("Id")!.SetValue(source, SourceId);
        _store.FindAsync(SourceId, Arg.Any<CancellationToken>()).Returns(source);

        _reader.BrowseAsync(SourceId, null, Arg.Any<CancellationToken>()).Returns(
            [.. Enumerable.Range(1, 5).Select(i => Element($"Unit-0{i}"))]);
        _reader.BrowseAsync(SourceId, @"\Db\Unit-01", Arg.Any<CancellationToken>()).Returns([Element("Pump-A")]);
        _reader.AttributesAsync(SourceId, @"\Db\Unit-01", Arg.Any<CancellationToken>()).Returns(
            [new SourceEntityDescriptor("Flow", "Steam flow", @"\Db\Unit-01", "t/h", "Float64")]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.13")]
    public async Task Сторінки_йдуть_курсором_до_кінця_без_пропусків()
    {
        var first = await Handler().HandleAsync(SourceId, null, null, null, 2, default);
        var second = await Handler().HandleAsync(SourceId, null, null, first.NextCursor, 2, default);
        var last = await Handler().HandleAsync(SourceId, null, null, second.NextCursor, 2, default);

        Assert.Equal(["Unit-01", "Unit-02", "Unit-03", "Unit-04", "Unit-05"],
            first.Items.Concat(second.Items).Concat(last.Items).Select(i => i.Code));
        Assert.Equal("2", first.NextCursor);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.13")]
    public async Task Шлях_дає_дочірні_елементи_й_атрибути_з_одиницею_а_пошук_звужує()
    {
        var node = await Handler().HandleAsync(SourceId, @"\Db\Unit-01", null, null, null, default);

        Assert.Equal(["Element", "Attribute"], node.Items.Select(i => i.Kind));
        Assert.Equal("t/h", node.Items[1].UnitSymbol);

        var found = await Handler().HandleAsync(SourceId, @"\Db\Unit-01", "STEAM", null, null, default);
        Assert.Equal("Flow", Assert.Single(found.Items).Code);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.13")]
    [InlineData(null, 201)]
    [InlineData(null, 0)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    public async Task Позамежна_сторінка_або_чужий_курсор_422(string? cursor, int? limit)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(SourceId, null, null, cursor, limit, default));

        Assert.Equal("err.ECR-REQ-0422.catalogQueryInvalid", refused.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.13")]
    public async Task Джерело_що_мовчить_за_межею_дає_503_а_не_висить()
    {
        _reader.BrowseAsync(SourceId, null, Arg.Any<CancellationToken>())
            .Returns(call => Hang(call.Arg<CancellationToken>()));

        var handler = new BrowseSourceCatalogHandler(
            _store, _reader, new SourceCatalogPolicy(TimeSpan.FromMilliseconds(200)), _access, _user);

        // ⚠ WaitAsync: без межі в обробнику тест падає за 5 с, а не висить.
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(SourceId, null, null, null, null, default).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("ECR-INT-0503", refused.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.catalogTimeout", refused.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.13")]
    public async Task Відмова_транспорту_503_а_відмова_автентифікації_проходить_як_є()
    {
        _reader.BrowseAsync(SourceId, null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SourceEntityDescriptor>>(_ => throw new HttpRequestException("connection refused"));

        var down = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(SourceId, null, null, null, null, default));
        Assert.Equal("err.ECR-INT-0503.catalogUnavailable", down.Details!["messageKey"]);

        _reader.BrowseAsync(SourceId, null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SourceEntityDescriptor>>(_ => throw new SourceAuthenticationException("ECR-INT-0502", "401"));

        await Assert.ThrowsAsync<SourceAuthenticationException>(
            () => Handler().HandleAsync(SourceId, null, null, null, null, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-13.13")]
    public async Task Лише_View_не_відкриває_каталог()
    {
        Allow("Integration.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(SourceId, null, null, null, null, default));
        await _reader.DidNotReceiveWithAnyArgs().BrowseAsync(default, default, default);
    }

    private static async Task<IReadOnlyList<SourceEntityDescriptor>> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

        return [];
    }

    private static SourceEntityDescriptor Element(string name)
        => new(name, null, $@"\Db\{name}", null, "Element");

    private BrowseSourceCatalogHandler Handler()
        => new(_store, _reader, new SourceCatalogPolicy(TimeSpan.FromSeconds(10)), _access, _user);

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
