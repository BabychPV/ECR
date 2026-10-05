// tests/Ecr.Application.Tests/Sources/SourceEventMapChangeHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Зміна й видалення мапінгу подій джерела (HSE301 A6): заміна полів, пауза, слід зі старим і новим станом,
/// заборона видалення мапінгу зі зв'язками.
/// </summary>
public sealed class SourceEventMapChangeHandlersTests : SourceEventMapTestBase
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Без_права_Integration_Manage_чи_гранта_403_на_зміні_й_видаленні()
    {
        Store.FindMapAsync(77, Arg.Any<CancellationToken>()).Returns(NewMap());

        Access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("Integration.View").Grant(ResourceKind.Project, ProjectId, GrantLevel.Manage).Build());
        await Assert.ThrowsAsync<AccessDeniedException>(() => Update().HandleAsync(77, UpdateCommand(), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Delete().HandleAsync(77, default));

        // Manage без гранта на проєкт документа — так само.
        Profile(GrantLevel.Write);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Update().HandleAsync(77, UpdateCommand(), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Delete().HandleAsync(77, default));

        await Store.DidNotReceiveWithAnyArgs().SaveAsync(default);
        await Store.DidNotReceiveWithAnyArgs().RemoveMapAsync(default!, default);
    }

    /// <summary>
    /// S18: мапінг документа невидимого проєкту — та сама 404, що й неіснуючий мапінг, а не 403 з
    /// <c>projectId</c>. Видимий проєкт без <c>Manage</c> лишається 403 (попередній тест).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "S18")]
    public async Task Мапінг_документа_невидимого_проєкту_на_зміні_й_видаленні_це_404_як_неіснуючий()
    {
        Store.FindMapAsync(77, Arg.Any<CancellationToken>()).Returns(NewMap());
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(78, default));

        Profile(GrantLevel.None);
        var onUpdate = await Assert.ThrowsAsync<NotFoundException>(() => Update().HandleAsync(77, UpdateCommand(), default));
        var onDelete = await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(77, default));

        foreach (var ex in new[] { onUpdate, onDelete })
        {
            Assert.Equal(missing.ErrorCode, ex.ErrorCode);
            Assert.Equal(missing.Details!["messageKey"], ex.Details!["messageKey"]);
            Assert.False(ex.Details.ContainsKey("projectId"));
        }

        await Store.DidNotReceiveWithAnyArgs().SaveAsync(default);
        await Store.DidNotReceiveWithAnyArgs().RemoveMapAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Зміна_мапінгу_замінює_поля_режим_і_паузу_та_пише_старий_і_новий_стан()
    {
        var map = NewMap();
        Store.FindMapAsync(77, Arg.Any<CancellationToken>()).Returns(map);

        var dto = await Update().HandleAsync(
            77,
            new UpdateSourceEventMapCommand(
                SourceEventVolumeMode.RowWindow,
                IsActive: false,
                "Flare",
                SourceEventAttributeScope.PrimaryElement,
                "FL-370",
                [Field(1, "$start"), Field(2, "$end")]),
            default);

        Assert.Equal((SourceEventVolumeMode.RowWindow, false, "Flare"), (dto.VolumeMode, dto.IsActive, dto.FilterAttribute));
        Assert.Equal(["$start", "$end"], dto.Fields.Select(f => f.SourceAttribute));
        await Store.Received(1).SaveAsync(Arg.Any<CancellationToken>());

        // Старі поля позначено до видалення ДО заміни: усі FK моделі — Restrict.
        Store.Received(1).ReleaseFields(map);
        await Audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.Operation == UpdateSourceEventMapHandler.AuditOperation && r.EntityId == 77
                && r.OldJson!.Contains("\"isActive\":true", StringComparison.Ordinal)
                && r.NewJson!.Contains("\"isActive\":false", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AN-40 / L9-06: версія, яку бачив клієнт, звіряється до будь-якої зміни; збіг — мапінг позначено зміненим, щоб
    /// правка лише полів теж підняла версію.
    /// </summary>
    /// <remarks>
    /// Мутації (лише локально): прибрати звірку <c>command.RowVersion</c> — червоніє перша половина; прибрати
    /// <c>store.MarkChanged(map)</c> — червоніє друга.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Audit", "L9-06")]
    public async Task L9_06_застаріла_версія_мапінгу_дає_409_до_змін_а_чинна_позначає_мапінг_зміненим()
    {
        var map = NewMap();
        Store.FindMapAsync(77, Arg.Any<CancellationToken>()).Returns(map);
        var fresh = Convert.ToHexString(map.RowVersion);

        var stale = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Update().HandleAsync(77, UpdateCommand() with { IsActive = false, RowVersion = "00000000000007D1" }, default));

        Assert.Equal(("ECR-INT-0409", "err.ECR-INT-0409.eventMapConcurrency"), (stale.ErrorCode, stale.Details!["messageKey"]));
        Assert.Equal("77", stale.Details["eventMapId"]);
        Assert.True(map.IsActive);
        Store.DidNotReceiveWithAnyArgs().ReleaseFields(default!);
        Store.DidNotReceiveWithAnyArgs().MarkChanged(default!);
        await Store.DidNotReceiveWithAnyArgs().SaveAsync(default);

        var dto = await Update().HandleAsync(77, UpdateCommand() with { RowVersion = fresh.ToLowerInvariant() }, default);

        Store.Received(1).MarkChanged(map);
        await Store.Received(1).SaveAsync(Arg.Any<CancellationToken>());
        Assert.Equal(fresh, dto.RowVersion);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Зміна_без_обов_язкового_end_відмовляється_а_стан_у_сховищі_не_зберігається()
    {
        Store.FindMapAsync(77, Arg.Any<CancellationToken>()).Returns(NewMap());

        await Assert.ThrowsAsync<DomainException>(() => Update().HandleAsync(
            77, UpdateCommand() with { Fields = [Field(1, "$start")] }, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Update().HandleAsync(404, UpdateCommand(), default));

        await Store.DidNotReceiveWithAnyArgs().SaveAsync(default);

        // Відмова домену — до позначки полів: відстежених полів без власника не лишається.
        Store.DidNotReceiveWithAnyArgs().ReleaseFields(default!);
        await Audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Мапінг_зі_зв_язками_не_видаляється_а_без_них_видаляється_із_слідом()
    {
        var map = NewMap();
        Store.FindMapAsync(77, Arg.Any<CancellationToken>()).Returns(map);
        Store.CountLinksAsync(77, Arg.Any<CancellationToken>()).Returns(3);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Delete().HandleAsync(77, default));

        Assert.Equal(("ECR-INT-0409", "err.ECR-INT-0409.eventMapHasLinks"), (refused.ErrorCode, refused.Details!["messageKey"]));
        Assert.Equal(3, refused.Details["links"]);
        await Store.DidNotReceiveWithAnyArgs().RemoveMapAsync(default!, default);
        await Audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);

        Store.CountLinksAsync(77, Arg.Any<CancellationToken>()).Returns(0);
        await Delete().HandleAsync(77, default);

        await Store.Received(1).RemoveMapAsync(map, Arg.Any<CancellationToken>());
        await Audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.Operation == DeleteSourceEventMapHandler.AuditOperation && r.OldJson != null && r.NewJson == null),
            Arg.Any<CancellationToken>());

        await Assert.ThrowsAsync<NotFoundException>(() => Delete().HandleAsync(404, default));
    }

    private UpdateSourceEventMapHandler Update() => new(Store, Sources, Access, User, Uow, Audit, Clock);

    private DeleteSourceEventMapHandler Delete() => new(Store, Access, User, Uow, Audit, Clock);
}
