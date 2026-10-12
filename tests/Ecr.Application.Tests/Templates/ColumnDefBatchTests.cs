using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// G13 (Land): пакетний запис колонок таблиці чернетки — одне читання структури, один скид кешу, один комміт;
/// перевірки ті самі, що в одиночному <c>PUT</c>, і всі — до першої мутації.
/// </summary>
/// <remarks>
/// Мутації: (1) у <c>SaveColumnDefsBatchHandler</c> перенести читання структури в цикл — червоний тест про
/// одне читання; (2) прибрати перевірку unknownUnit — червоний тест «усе або нічого»; (3) прибрати ліміт 500.
/// </remarks>
public sealed class ColumnDefBatchTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    private readonly TemplateBuilder _builder = new() { TemplateVersionId = 1 };
    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    private readonly TableDef _table;

    public ColumnDefBatchTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var sheet = _builder.Sheet("Water");
        _table = _builder.Table(sheet, "Main");
        _draft.AddSheet(sheet);

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["t"] = new(12, "t", 1) },
            new Dictionary<string, int>(StringComparer.Ordinal)));
    }

    private static SaveColumnDefItem Item(
        string code, string en = "Header", CellDataType dataType = CellDataType.Decimal, int? ordinal = null,
        int? unitId = null, bool required = false)
        => new(
            code,
            new SaveColumnDefCommand(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = en },
                ordinal, dataType, required, IsReadOnly: false, IsHidden: false,
                Precision: null, Scale: null, DefaultValue: null, DisplayFormat: null, StyleId: null,
                LookupRegistryDefId: null, LookupFilter: null, UnitId: unitId));

    private SaveColumnDefsBatchHandler Batch()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user, _units);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Пачка_нових_колонок_лягає_за_порядком_запиту_з_одним_читанням_і_одним_скидом_кешу()
    {
        var items = Enumerable.Range(0, 120).Select(i => Item($"C{i:000}", $"Col {i}")).ToList();

        var saved = await Batch().HandleAsync(1, _table.Id, items, CancellationToken.None);

        Assert.Equal(120, saved.Count);
        Assert.Equal(items.Select(i => i.Code), saved.Select(c => c.Code));
        Assert.Equal(Enumerable.Range(0, 120), saved.Select(c => c.Ordinal));
        Assert.Equal(120, _table.Columns.Count);

        // ⛔ Суть G13: вартість не росте з кількістю колонок.
        await _store.Received(1).GetWithStructureAsync(1, Arg.Any<CancellationToken>());
        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
        await _uow.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _uow.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());

        // Аудит — по рядку на колонку, як і в одиночному записі.
        await _audit.Received(120).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "ColumnDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Пачка_створює_нові_і_оновлює_наявні_за_кодом()
    {
        await Batch().HandleAsync(1, _table.Id, [Item("Jan", "January v1")], CancellationToken.None);

        var saved = await Batch().HandleAsync(
            1, _table.Id, [Item("Jan", "January v2", required: true), Item("Feb", "February")], CancellationToken.None);

        Assert.Equal(2, _table.Columns.Count);
        Assert.Equal("January v2", saved[0].HeaderL10n.Get("en"));
        Assert.True(saved[0].IsRequired);
        Assert.Equal(0, saved[0].Ordinal);
        Assert.Equal(1, saved[1].Ordinal);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.Operation == "Update"), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Пачка_понад_500_колонок_відхиляється_з_ключем_і_нічого_не_пишеться()
    {
        var items = Enumerable.Range(0, SaveColumnDefsBatchHandler.MaxColumns + 1).Select(i => Item($"C{i:000}")).ToList();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Batch().HandleAsync(1, _table.Id, items, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.columnBatchTooLarge", error.Details!["messageKey"]);
        Assert.Equal("500", error.Details["max"]);
        Assert.Empty(_table.Columns);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Рівно_500_колонок_проходить()
    {
        var items = Enumerable.Range(0, SaveColumnDefsBatchHandler.MaxColumns).Select(i => Item($"C{i:000}")).ToList();

        var saved = await Batch().HandleAsync(1, _table.Id, items, CancellationToken.None);

        Assert.Equal(500, saved.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Повтор_коду_в_пачці_відхиляється_з_ключем()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Batch().HandleAsync(1, _table.Id, [Item("Jan"), Item("Feb"), Item("Jan")], CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0422.columnBatchDuplicateCode", error.Details!["messageKey"]);
        Assert.Equal("Jan", error.Details["columnCode"]);
        Assert.Empty(_table.Columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-U1")]
    public async Task Одна_колонка_з_неіснуючою_одиницею_відхиляє_всю_пачку_до_запису()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Batch().HandleAsync(
                1, _table.Id, [Item("A", unitId: 12), Item("B", unitId: 777), Item("C")], CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0422.unknownUnit", error.Details!["messageKey"]);
        Assert.Equal("B", error.Details["columnCode"]);

        // Усе або нічого: перша, валідна, колонка теж не записана.
        Assert.Empty(_table.Columns);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
        await _metadataCache.DidNotReceiveWithAnyArgs().InvalidateAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Зміна_типу_наявної_колонки_в_пачці_відхиляє_пачку()
    {
        await Batch().HandleAsync(1, _table.Id, [Item("Jan", dataType: CellDataType.Decimal)], CancellationToken.None);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Batch().HandleAsync(
                1, _table.Id,
                [Item("New"), Item("Jan", dataType: CellDataType.String)],
                CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0422.columnDataTypeImmutable", error.Details!["messageKey"]);
        Assert.Single(_table.Columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_пачка_відхиляється()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Batch().HandleAsync(1, _table.Id, [Item("Jan")], CancellationToken.None));

        Assert.Empty(_table.Columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Порожня_пачка_нічого_не_читає_і_не_пише()
    {
        var saved = await Batch().HandleAsync(1, _table.Id, [], CancellationToken.None);

        Assert.Empty(saved);
        await _store.DidNotReceiveWithAnyArgs().GetWithStructureAsync(default, default);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
