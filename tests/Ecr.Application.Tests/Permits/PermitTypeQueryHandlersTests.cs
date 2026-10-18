// tests/Ecr.Application.Tests/Permits/PermitTypeQueryHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>Читання типів дозволів: перелік зі сторінками й фільтрами та повне визначення (PM-03).</summary>
public sealed class PermitTypeQueryHandlersTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private readonly IPermitTypeStore _permits = Substitute.For<IPermitTypeStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    public PermitTypeQueryHandlersTests()
    {
        _permits.ListAllTablesAsync(Arg.Any<CancellationToken>()).Returns([]);
        _permits.CountPermitsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int>());
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _registries.GetUserDisplayNamesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());
    }

    [Fact]
    public async Task Перелік_віддає_сторінку_за_кодом_і_курсор_наступної()
    {
        var types = new[] { Type("AIR", PermitCategory.Air), Type("WASTE", PermitCategory.Waste), Type("WATER", PermitCategory.Water) };
        _permits.ListAsync(Arg.Any<CancellationToken>()).Returns(types);

        var first = await List().HandleAsync(new CursorRequest(2), null, null, null, default);
        var second = await List().HandleAsync(new CursorRequest(2, first.NextCursor), null, null, null, default);

        Assert.Equal(["AIR", "WASTE"], first.Items.Select(i => i.Code));
        Assert.Equal("WASTE", first.NextCursor);
        Assert.Equal(["WATER"], second.Items.Select(i => i.Code));
        Assert.Null(second.NextCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Розмір_сторінки_поза_межами_відхиляється(int limit)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => List().HandleAsync(new CursorRequest(limit), null, null, null, default));

        Assert.Equal("err.ECR-REQ-0422.pageSizeOutOfRange", error.Details!["messageKey"]);
    }

    [Fact]
    public async Task Перелік_фільтрується_за_категорією_станом_і_підрядком()
    {
        var published = Type("WATER", PermitCategory.Water);
        published.Publish(7, Now);
        _permits.ListAsync(Arg.Any<CancellationToken>())
            .Returns([Type("AIR", PermitCategory.Air), published, Type("WASTE", PermitCategory.Waste)]);

        var byCategory = await List().HandleAsync(new CursorRequest(), "Air", null, null, default);
        var byStatus = await List().HandleAsync(new CursorRequest(), null, "Published", null, default);
        var byText = await List().HandleAsync(new CursorRequest(), null, null, "was", default);

        Assert.Equal(["AIR"], byCategory.Items.Select(i => i.Code));
        Assert.Equal(["WATER"], byStatus.Items.Select(i => i.Code));
        Assert.Equal(["WASTE"], byText.Items.Select(i => i.Code));
    }

    [Fact]
    public async Task Перелік_показує_таблиці_і_ознаку_незавершених_змін_з_чернетки()
    {
        var type = Type("AIR", PermitCategory.Air);
        var draft = new PermitTypeContent(
            Name("Air"), "Air",
            [new PermitTypeTableDto("Emissions", Name("Emissions"), 1, [Column("A"), Column("B")], [])],
            []);
        type.SaveDraft(PermitTypeCodec.Serialize(draft), 7, Now);
        _permits.ListAsync(Arg.Any<CancellationToken>()).Returns([type]);

        var item = Assert.Single((await List().HandleAsync(new CursorRequest(), null, null, null, default)).Items);

        Assert.True(item.HasUnpublishedChanges);
        var table = Assert.Single(item.Tables);
        Assert.Equal(("Emissions", 2), (table.Code, table.ColumnCount));
    }

    [Fact]
    public async Task Невідомий_тип_і_тип_під_забороною_відповідають_однаково_404()
    {
        _permits.FindByCodeAsync("MISSING", Arg.Any<CancellationToken>()).Returns((PermitTypeDef?)null);
        var denied = Type("DENIED", PermitCategory.Air);
        _permits.FindByCodeAsync("DENIED", Arg.Any<CancellationToken>()).Returns(denied);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }.Permission("Registry.View").Deny(ResourceKind.Registry, denied.HeaderRegistryDefId).Build());

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Get(access).HandleAsync("MISSING", default));
        var hidden = await Assert.ThrowsAsync<NotFoundException>(() => Get(access).HandleAsync("DENIED", default));

        Assert.Equal("ECR-PRM-0404", missing.ErrorCode);
        Assert.Equal(missing.ErrorCode, hidden.ErrorCode);
        Assert.Equal("err.ECR-PRM-0404.type", hidden.Details!["messageKey"]);
    }

    [Fact]
    public async Task Читання_без_права_Registry_View_відхиляється()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Get(access).HandleAsync("AIR", default));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => List(access).HandleAsync(new CursorRequest(), null, null, null, default));
    }

    [Fact]
    public async Task Повне_визначення_типу_з_чернеткою_віддає_чернетку_шапку_й_версію_чернетки()
    {
        var type = Type("AIR", PermitCategory.Air);
        type.Publish(7, Now);
        var draft = new PermitTypeContent(
            Name("Air v2"), "Air",
            [new PermitTypeTableDto("Emissions", Name("Emissions"), 1, [Column("A")], [])],
            []);
        type.OpenNewVersion(PermitTypeCodec.Serialize(draft), 7, Now);
        _permits.FindByCodeAsync("AIR", Arg.Any<CancellationToken>()).Returns(type);

        var dto = await Get(Viewer()).HandleAsync("AIR", default);

        Assert.Equal(("Published", 1, (int?)2, true), (dto.Status, dto.Version, dto.DraftVersion, dto.HasUnpublishedChanges));
        Assert.Equal("Air v2", dto.NameL10n["en"]);
        Assert.Equal("Permit_Number", dto.HeaderFields[0].Code);
        Assert.Single(dto.Tables);
    }

    private ListPermitTypesHandler List(IAccessDecisionService? access = null)
        => new(_permits, _registries, access ?? Viewer(), User());

    private GetPermitTypeHandler Get(IAccessDecisionService access) => new(_permits, _registries, access, User());

    private static IAccessDecisionService Viewer()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());
        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        return user;
    }

    private static PermitTypeDef Type(string code, PermitCategory category)
        => new(EcrCode.Create(code), new LocalizedText(Name(code == "WASTE" ? "Waste permits" : code)), category, 100 + code.Length);

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };

    private static PermitTypeColumnDto Column(string code)
        => new(code, Name(code), "String", null, false, false, null, null, null, null, null, null, null, 1);
}
