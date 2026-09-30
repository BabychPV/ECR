// tests/Ecr.Application.Tests/Sources/SourceEventMapHandlersTests.cs
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
/// Перелік, читання й створення мапінгу подій джерела (HSE301 A6): права й грант проєкту, валідація цілей, слід у
/// журналі структурних змін.
/// </summary>
/// <remarks>
/// ⚠ Обробники без бази; що запис справді проходить крізь індекси EF, доводить <c>SourceEventsApiTests</c>
/// (Ecr.Api.Tests) на реальному SQL Server.
/// </remarks>
public sealed class SourceEventMapHandlersTests : SourceEventMapTestBase
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Створення_мапінгу_пише_слід_у_журнал_структурних_змін_з_новим_станом_і_відповіддю_з_полями()
    {
        var dto = await Create().HandleAsync(Command(), default);

        Assert.Equal((77, EntityId, DocumentId, TableId, true), (dto.Id, dto.SourceEntityId, dto.DocumentId, dto.TableDefId, dto.IsActive));
        Assert.Equal(["$start", "$end", "$name", "Category"], dto.Fields.Select(f => f.SourceAttribute));
        await Audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "ext.SourceEventMap" && r.Operation == CreateSourceEventMapHandler.AuditOperation
                && r.EntityId == 77 && r.OldJson == null && r.ChangedByUserId == Actor
                && r.NewJson!.Contains("\"$start\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Явна_відповідність_значення_лягає_у_відповідь_і_лише_з_довідника_колонки()
    {
        var withMap = Command(category: Category(SourceEventValueKind.ValueMap, new SourceEventValueInput("зима", 501)));
        var dto = await Create().HandleAsync(withMap, default);

        var value = Assert.Single(dto.Fields.Single(f => f.SourceAttribute == "Category").Values);
        Assert.Equal(("зима", 501L), (value.SourceValue, value.RegistryEntryId));

        // Запис іншого довідника — 404, а не мовчазна відповідність, що ніколи не спрацює.
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => Create().HandleAsync(
            Command(category: Category(SourceEventValueKind.ValueMap, new SourceEventValueInput("літо", 888))), default));
        Assert.Equal("err.ECR-INT-0405.registryEntry", foreign.Details!["messageKey"]);

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Create().HandleAsync(
            Command(category: Category(SourceEventValueKind.ValueMap, new SourceEventValueInput("літо", 999))), default));
        Assert.Equal("err.ECR-INT-0405.registryEntry", missing.Details!["messageKey"]);
    }

    [Theory]
    [InlineData(GrantLevel.Write)]
    [InlineData(GrantLevel.Read)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Без_гранта_Manage_на_проєкт_документа_403_і_нічого_не_записано(GrantLevel level)
    {
        Profile(level);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Create().HandleAsync(Command(), default));

        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", denied.Details!["messageKey"]);
        await Store.DidNotReceiveWithAnyArgs().AddMapAsync(default!, default);
        await Audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Читання_мапінгів_доступне_View_а_без_права_403()
    {
        Access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("Integration.View").Build());
        Store.FindMapAsync(77, Arg.Any<CancellationToken>()).Returns(NewMap());
        Store.ListMapsAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns([NewMap()]);

        var reader = new ListSourceEventMapsHandler(Store, Access, User);
        Assert.Single(await reader.ListAsync(null, default));
        Assert.Equal(77, (await reader.GetAsync(77, default)).Id);
        await Assert.ThrowsAsync<NotFoundException>(() => reader.GetAsync(404, default));

        Access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("System.ViewHealth").Build());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.ListAsync(null, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.GetAsync(77, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Відмови_валідації_цілей_не_пишуть_нічого()
    {
        // Немає обов'язкового $end — відмова домену (ECR-INT-0422), з ключем.
        var noEnd = Command() with { Fields = Command().Fields.Where(f => f.SourceAttribute != "$end").ToList() };
        var domain = await Assert.ThrowsAsync<DomainException>(() => Create().HandleAsync(noEnd, default));
        Assert.Equal("err.ECR-INT-0422.eventMapStartEndRequired", domain.Details!["messageKey"]);

        // Не динамічна таблиця.
        Table = NewTable(TableRowMode.Fixed);
        var fixedTable = await Assert.ThrowsAsync<DomainException>(() => Create().HandleAsync(Command(), default));
        Assert.Equal("err.ECR-INT-0422.eventMapTargetNotDynamic", fixedTable.Details!["messageKey"]);
        Table = NewTable(TableRowMode.Dynamic);

        // Таблиця іншої версії шаблону, ніж у проєкту документа.
        Store.FindTargetTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new EventMapTableInfo(Table, VersionId + 1));
        var version = await Assert.ThrowsAsync<BusinessRuleException>(() => Create().HandleAsync(Command(), default));
        Assert.Equal("err.ECR-INT-0422.eventMapTableNotInDocument", version.Details!["messageKey"]);
        Store.FindTargetTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new EventMapTableInfo(Table, VersionId));

        // Другий мапінг тієї самої трійки.
        Store.MapExistsAsync(EntityId, DocumentId, TableId, Arg.Any<CancellationToken>()).Returns(true);
        var duplicate = await Assert.ThrowsAsync<BusinessRuleException>(() => Create().HandleAsync(Command(), default));
        Assert.Equal(("ECR-INT-0409", "err.ECR-INT-0409.eventMapExists"), (duplicate.ErrorCode, duplicate.Details!["messageKey"]));
        Store.MapExistsAsync(EntityId, DocumentId, TableId, Arg.Any<CancellationToken>()).Returns(false);

        // Невідомі сутність, документ, таблиця, колонка.
        await Assert.ThrowsAsync<NotFoundException>(() => Create().HandleAsync(Command() with { SourceEntityId = 404 }, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Create().HandleAsync(Command() with { DocumentId = 404 }, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Create().HandleAsync(Command() with { TableDefId = 404 }, default));
        var column = Command() with { Fields = [.. Command().Fields, new SourceEventFieldInput(404, "X", SourceEventAttributeScope.Event, SourceEventValueKind.Direct, null, null, null)] };
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => Create().HandleAsync(column, default));
        Assert.Equal("err.ECR-INT-0405.column", unknown.Details!["messageKey"]);

        await Store.DidNotReceiveWithAnyArgs().AddMapAsync(default!, default);
        await Audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    private CreateSourceEventMapHandler Create() => new(Store, Sources, Access, User, Uow, Audit, Clock);
}
