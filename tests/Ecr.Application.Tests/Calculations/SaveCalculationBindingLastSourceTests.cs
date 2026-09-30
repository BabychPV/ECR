// tests/Ecr.Application.Tests/Calculations/SaveCalculationBindingLastSourceTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// HSE301 C5b, <c>D-215</c>: відв'язка останнього джерела колонки типу <c>Formula</c>
/// ОПУБЛІКОВАНОЇ версії шаблону — <c>409 ECR-TMPL-4091</c>.
/// </summary>
/// <remarks>
/// ⚠ Сховище прив'язок тут — фейк, що поводиться як база: <c>ListBoundColumnIdsAsync</c>
/// віддає ЗАКОМІЧЕНИЙ стан (знімок на момент останнього <c>SaveChanges</c>), а не живі
/// об'єкти в пам'яті. Інакше перевірка «до запису» й «після запису» дали б те саме, і
/// мутація «рахувати до зміни» лишилася б непоміченою.
///
/// ⚠ Клієнт замінює джерело двома окремими <c>PUT</c> (форма прив'язки в
/// <c>MethodologyBindingsPanel</c> не міняє ні колонку, ні вихід наявної прив'язки):
/// нова прив'язка — «додати», стара — зняти «активна». Тест заміни йде саме так.
/// </remarks>
public sealed class SaveCalculationBindingLastSourceTests
{
    private const int MethodologyId = 7;
    private const int OtherMethodologyId = 8;
    private const int TableDefId = 3;
    private const int ColumnDefId = 42;
    private const int TemplateVersionId = 501;
    private const int SheetDefId = 11;

    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ITemplateVersionStore _templateVersions = Substitute.For<ITemplateVersionStore>();
    private readonly IRepository<TableDef, int> _tables = Substitute.For<IRepository<TableDef, int>>();
    private readonly IRepository<SheetDef, int> _sheets = Substitute.For<IRepository<SheetDef, int>>();

    /// <summary>Рядки <c>cfg.CalculationBinding</c> разом із ще не збереженими.</summary>
    private readonly List<CalculationBinding> _rows = [];

    /// <summary>Колонки з активною прив'язкою — стан, який бачить база після останнього збереження.</summary>
    private HashSet<int> _committed = [];

    private CellDataType _columnType = CellDataType.Formula;
    private TemplateVersion _structure = new(1, "1.0.0.0", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    public SaveCalculationBindingLastSourceTests()
    {
        _user.UserId.Returns(9);
        _clock.UtcNow.Returns(new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc));

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(SaveCalculationBindingHandler.Permission)
                .Build());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        // Адреса колонки: колонка → таблиця → аркуш → версія шаблону.
        _bindings.FindColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns(_ => new BoundColumnRef(TableDefId, "CF", _columnType));
        _tables.FindAsync(TableDefId, Arg.Any<CancellationToken>())
            .Returns(new TableDef(
                SheetDefId, EcrCode.Create("T1"), Name("T1"), 1,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed));
        _sheets.FindAsync(SheetDefId, Arg.Any<CancellationToken>())
            .Returns(new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Name("S1"), 1));

        foreach (var id in new[] { MethodologyId, OtherMethodologyId })
        {
            _drafts.FindAsync(id, Arg.Any<CancellationToken>())
                .Returns(new Methodology(EcrCode.Create($"M{id}"), Name("M")));

            // Кожна методологія оголошує виходи OUT1 і OUT2 (F-09).
            var version = new MethodologyVersion(
                id, "1.0", CalculationLevel.Configuration, 9,
                new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
            typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, id * 10);
            _drafts.GetAllVersionsAsync(id, Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<MethodologyVersion>)[version]);
            _methodologies.GetOutputsAsync(id * 10, Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<MethodologyOutput>)
                [
                    new MethodologyOutput(id * 10, EcrCode.Create("OUT1"), 5),
                    new MethodologyOutput(id * 10, EcrCode.Create("OUT2"), 5),
                ]);
        }

        // Фейк сховища прив'язок поверх `_rows` / `_committed`.
        _bindings.FindAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _rows.FirstOrDefault(b =>
                b.ColumnDefId == call.ArgAt<int>(0)
                && b.MethodologyId == call.ArgAt<int>(1)
                && b.OutputCode == call.ArgAt<string>(2)));
        _bindings.When(s => s.Add(Arg.Any<CalculationBinding>())).Do(call => _rows.Add(call.Arg<CalculationBinding>()));
        _uow.When(u => u.SaveChangesAsync(Arg.Any<CancellationToken>())).Do(_ => Commit());
        _bindings.ListBoundColumnIdsAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlySet<int>)new HashSet<int>(_committed));

        _templateVersions.GetWithStructureAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(_ => _structure);

        // Стартовий стан: колонку живить рівно одна активна прив'язка M7/OUT1.
        Existing(MethodologyId, "OUT1");
    }

    private SaveCalculationBindingHandler Handler() => new(
        _bindings, _drafts, _methodologies, _uow, _access, _user, _audit, _clock,
        _templateVersions, _tables, _sheets);

    /// <remarks>
    /// Мутації: прибрати виклик <c>RequireSourceLeftAsync</c> — відв'язка проходить, тест
    /// червоний; перенести його ДО <c>SaveChanges</c> (рахувати джерела до зміни) — у
    /// закоміченому стані ще є ця ж прив'язка, відмови немає, тест червоний.
    /// </remarks>
    [Theory]
    [InlineData(TemplateVersionStatus.Published)]
    [InlineData(TemplateVersionStatus.Deprecated)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Відв_язка_останнього_джерела_Formula_колонки_опублікованої_версії_409(
        TemplateVersionStatus status)
    {
        Locked(status);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Unbind(MethodologyId, "OUT1"));

        Assert.Equal(ErrorCodes.LastSourceOfPublishedColumn, error.ErrorCode);
        Assert.Equal("ECR-TMPL-4091", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-4091.lastSourceOfPublishedColumn", error.Details!["messageKey"]);
        Assert.Equal("CF", error.Details!["columnCode"]);
        Assert.Equal("1.0.0.0", error.Details!["templateVersion"]);

        // Відмова — до журналу: у журналі немає зміни, яку транзакція відкотить.
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Відв_язка_не_останнього_джерела_на_опублікованій_версії_проходить()
    {
        Locked(TemplateVersionStatus.Published);
        Existing(OtherMethodologyId, "OUT1");

        var saved = await Unbind(MethodologyId, "OUT1");

        Assert.False(saved.IsActive);
        await _templateVersions.DidNotReceiveWithAnyArgs().GetWithStructureAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Відв_язка_коли_колонку_рахує_формула_шаблону_проходить()
    {
        Locked(TemplateVersionStatus.Published);
        _structure = StructureWithColumnFormula();

        var saved = await Unbind(MethodologyId, "OUT1");

        Assert.False(saved.IsActive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Відв_язка_останнього_джерела_на_чернетці_проходить()
    {
        Locked(TemplateVersionStatus.Draft);

        var saved = await Unbind(MethodologyId, "OUT1");

        Assert.False(saved.IsActive);
        await _bindings.DidNotReceiveWithAnyArgs().ListBoundColumnIdsAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Відв_язка_Calculated_колонки_опублікованої_версії_не_підпадає_під_правило()
    {
        // ⚠ Правило D-215 — лише для `Formula`: `Calculated` без прив'язки публікація
        // пропускає свідомо (`PublishChecks.CheckComputedColumns`), і ловить його
        // `ConsistencyCheckJob`, а не гейт.
        Locked(TemplateVersionStatus.Published);
        _columnType = CellDataType.Calculated;

        var saved = await Unbind(MethodologyId, "OUT1");

        Assert.False(saved.IsActive);
    }

    /// <summary>
    /// Заміна джерела тим шляхом, яким іде клієнт: нова прив'язка (<c>PUT</c> з
    /// <c>isActive = true</c>), потім стара — без «активна».
    /// </summary>
    /// <remarks>
    /// Мутація «рахувати ДО зміни» тут лишається зеленою (до зміни джерело теж є) —
    /// червоніє перший тест; тест заміни тримає інше: що правило не рахує прив'язку,
    /// яку щойно вимкнули, і не заважає законній заміні.
    /// </remarks>
    [Theory]
    [InlineData(OtherMethodologyId, "OUT1")]
    [InlineData(MethodologyId, "OUT2")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Заміна_джерела_прив_язати_нове_потім_відв_язати_старе_проходить(
        int newMethodologyId, string newOutput)
    {
        Locked(TemplateVersionStatus.Published);

        var added = await Handler().HandleAsync(
            newMethodologyId, ColumnDefId, newOutput, "{}", isActive: true, CancellationToken.None);
        var removed = await Unbind(MethodologyId, "OUT1");

        Assert.True(added.IsActive);
        Assert.False(removed.IsActive);
        Assert.Contains(ColumnDefId, _committed);
    }

    private Task<Ecr.Application.Calculations.Dto.CalculationBindingDto> Unbind(int methodologyId, string outputCode)
        => Handler().HandleAsync(methodologyId, ColumnDefId, outputCode, "{}", isActive: false, CancellationToken.None);

    private void Locked(TemplateVersionStatus status)
        => _templateVersions.LockVersionForUpdateAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(status);

    private void Existing(int methodologyId, string outputCode)
    {
        _rows.Add(new CalculationBinding(TableDefId, ColumnDefId, methodologyId, outputCode, "{}"));
        Commit();
    }

    private void Commit() => _committed = [.. _rows.Where(b => b.IsActive).Select(b => b.ColumnDefId)];

    /// <summary>Версія, у якій колонку рахує формула шаблону (область <c>Column</c>).</summary>
    private static TemplateVersion StructureWithColumnFormula()
    {
        var version = new TemplateVersion(1, "1.0.0.0", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Name("S1"), 1);
        version.AddSheet(sheet);

        var table = new TableDef(
            SheetDefId, EcrCode.Create("T1"), Name("T1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(table, TableDefId);
        sheet.AddTable(table);

        var formula = new FormulaDef(TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
        formula.AssignColumn(ColumnDefId);
        table.AddFormula(formula);

        return version;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
