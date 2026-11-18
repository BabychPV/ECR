// tests/Ecr.Application.Tests/Templates/RowDefCellDefaultsTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ B175: дефолти комірок рядка в <see cref="SaveRowDefHandler"/> (запис, «не чіпати» при <c>null</c>, перевірки
/// <c>ECR-TMPL-0422</c>).
/// </summary>
/// <remarks>
/// Мутації: прибрати перевірку «колонка є» — червоніє «невідома колонка»; прибрати IsComputed/Unit — «обчислювана» і
/// «Unit»; прибрати пошук коду в довіднику — «невідомий код»; прибрати розбір значення — «непридатне значення».
/// </remarks>
public sealed class RowDefCellDefaultsTests
{
    private const int Registry = 900;
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    private readonly TemplateBuilder _builder = new() { TemplateVersionId = 1 };
    private readonly TemplateVersion _draft = new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
    private readonly TableDef _table;
    private readonly ColumnDef _qty;

    public RowDefCellDefaultsTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var sheet = _builder.Sheet("Land");
        _table = _builder.Table(sheet, "T7b", rowMode: TableRowMode.Fixed);
        var status = _builder.Column(_table, "Land_Status", CellDataType.Lookup);
        status.SetLookup(Registry);
        _qty = _builder.Column(_table, "Qty");
        _builder.Column(_table, "Qty_M01", isMonthColumn: true);
        _builder.Column(_table, "Out", CellDataType.Formula);
        _builder.Column(_table, "Uom", CellDataType.Unit);
        _draft.AddSheet(sheet);

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        _registries.FindEntriesByCodesAsync(Registry, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<RegistryEntry>)
            [
                .. new[] { ("RUNNING", 77L) }
                    .Where(e => call.ArgAt<IReadOnlyCollection<string>>(1).Contains(e.Item1))
                    .Select(e => RegistryEntry.ForSnapshot(
                        e.Item2, Registry, EcrCode.Create(e.Item1),
                        new LocalizedText(new Dictionary<string, string> { ["en"] = e.Item1 }), 1)),
            ]);
    }

    private SaveRowDefHandler Save()
        => new(
            _store, new ChangeClassifier(), Substitute.For<IMetadataCache>(), Substitute.For<IAuditWriter>(), _uow,
            _clock, _access, _user, styles: null, registries: _registries);

    private static SaveRowDefCommand Command(IReadOnlyDictionary<string, string>? defaults)
        => new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Unit 1" }, null, RowKind.Item,
            null, false, CellDefaults: defaults);

    private static Dictionary<string, string> D(params (string Code, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Code, p => p.Value, StringComparer.Ordinal);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "B175")]
    public async Task Дефолти_зберігаються_null_не_чіпає_а_порожня_мапа_прибирає()
    {
        var saved = await Save().HandleAsync(
            1, _table.Id, "R1", Command(D(("Land_Status", "RUNNING"), ("Qty", "1,5"), ("Qty_M01", ""))), CancellationToken.None);

        Assert.Equal("RUNNING", saved.CellDefaults!["Land_Status"]);
        Assert.Equal("", saved.CellDefaults["Qty_M01"]);

        var kept = await Save().HandleAsync(1, _table.Id, "R1", Command(null), CancellationToken.None);
        Assert.Equal(3, kept.CellDefaults!.Count);

        var cleared = await Save().HandleAsync(1, _table.Id, "R1", Command(D()), CancellationToken.None);
        Assert.Null(cleared.CellDefaults);
        Assert.Null(Assert.Single(_table.Rows).CellDefaultsJson);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "B175")]
    [InlineData("Nope", "x", "err.ECR-TMPL-0422.rowDefaultUnknownColumn")]
    [InlineData("Out", "1", "err.ECR-TMPL-0422.rowDefaultColumnNotInput")]
    [InlineData("Uom", "1", "err.ECR-TMPL-0422.rowDefaultColumnNotInput")]
    [InlineData("Land_Status", "REMOVED", "err.ECR-TMPL-0422.rowDefaultLookupCodeUnknown")]
    [InlineData("Qty", "abc", "err.ECR-TMPL-0422.rowDefaultValueInvalid")]
    public async Task Непридатний_дефолт_рядка_відхиляється_окремим_messageKey(string column, string value, string messageKey)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, _table.Id, "R1", Command(D((column, value))), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);
        Assert.Equal(column, error.Details["columnCode"]);
        Assert.Empty(_table.Rows);   // нічого не записано
    }
}
