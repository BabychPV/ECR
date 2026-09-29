// tests/Ecr.Application.Tests/Calculations/SaveCalculationBindingLockTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// HSE301 C5b: прив'язка й відв'язка методології беруть блок рядка версії
/// шаблону ПЕРШОЮ дією своєї транзакції (<see cref="SaveCalculationBindingHandler"/>).
/// </summary>
/// <remarks>
/// ⛔ Публікація шаблону читає активні прив'язки (<c>ListBoundColumnIdsAsync</c>,
/// <c>ECR-TMPL-4226</c>) під блоком рядка <c>cfg.TemplateVersion</c>. Обробник
/// прив'язок блоку не брав, тож прив'язка чи відв'язка, закомічена між читанням
/// і комітом публікації, проходила повз перевірку.
///
/// ⚠ Лише блок, без заборони: прив'язка до колонки ОПУБЛІКОВАНОЇ версії —
/// штатний порядок ролей (<c>PublishChecks.CheckComputedColumns</c>), тож
/// окремий тест тримає, що вона й далі проходить.
/// </remarks>
public sealed class SaveCalculationBindingLockTests
{
    private const int MethodologyId = 7;
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

    /// <summary>Порядок викликів; елемент із «+» — зроблено всередині транзакції.</summary>
    private readonly List<string> _log = [];
    private bool _inTransaction;

    public SaveCalculationBindingLockTests()
    {
        _user.UserId.Returns(9);
        _clock.UtcNow.Returns(new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc));

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(SaveCalculationBindingHandler.Permission)
                .Build());

        // Транзакція виконує операцію, як справжня, і позначає її межі.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                _log.Add("begin");
                _inTransaction = true;
                try
                {
                    await call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None);
                }
                finally
                {
                    _inTransaction = false;
                    _log.Add("commit");
                }
            });

        // Адреса колонки: колонка → таблиця → аркуш → версія шаблону.
        _bindings.FindColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns(new BoundColumnRef(TableDefId, "CALC", CellDataType.Calculated));
        _tables.FindAsync(TableDefId, Arg.Any<CancellationToken>())
            .Returns(new TableDef(
                SheetDefId, EcrCode.Create("T1"), Name("T1"), 1,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed));
        _sheets.FindAsync(SheetDefId, Arg.Any<CancellationToken>())
            .Returns(new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Name("S1"), 1));

        _drafts.FindAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(new Methodology(EcrCode.Create("M1"), Name("M1")));

        // Єдина версія методології оголошує рівно вихід OUT1 (F-09).
        var version = new MethodologyVersion(
            MethodologyId, "1.0", CalculationLevel.Configuration, 9,
            new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, 70);
        _drafts.GetAllVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyVersion>)[version]);
        _methodologies.GetOutputsAsync(70, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyOutput>)[new MethodologyOutput(70, EcrCode.Create("OUT1"), 5)]);

        _templateVersions.LockVersionForUpdateAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionStatus.Draft);

        // Журнал викликів: що саме й чи всередині транзакції.
        _templateVersions
            .When(s => s.LockVersionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()))
            .Do(call => Log($"lock:{call.ArgAt<int>(0)}"));
        _drafts.When(s => s.FindAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())).Do(_ => Log("methodology"));
        _bindings.When(s => s.FindColumnAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())).Do(_ => Log("column"));
        _bindings
            .When(s => s.FindAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => Log("existing"));
        _bindings.When(s => s.Add(Arg.Any<CalculationBinding>())).Do(_ => Log("add"));
        _uow.When(u => u.SaveChangesAsync(Arg.Any<CancellationToken>())).Do(_ => Log("save"));
        _audit
            .When(a => a.WriteStructureChangeAsync(Arg.Any<StructureChangeRecord>(), Arg.Any<CancellationToken>()))
            .Do(_ => Log("audit"));
    }

    private SaveCalculationBindingHandler Handler() => new(
        _bindings, _drafts, _methodologies, _uow, _access, _user, _audit, _clock,
        _templateVersions, _tables, _sheets);

    /// <summary>
    /// Блок версії — перша дія транзакції, до читання методології, колонки й
    /// наявної прив'язки і до запису: для нової прив'язки, для правки й для
    /// відв'язки (<c>isActive = false</c>).
    /// </summary>
    /// <remarks>
    /// Мутація: прибрати виклик <c>LockVersionForUpdateAsync</c> у
    /// <c>HandleAsync</c> — першим у транзакції стає «methodology», тест червоний.
    /// Перенести його після <c>SaveLockedAsync</c> — теж червоний.
    /// </remarks>
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("unbind")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Блок_версії_шаблону_береться_першою_дією_транзакції_до_читань_і_запису(string kind)
    {
        if (kind != "create")
        {
            _bindings.FindAsync(ColumnDefId, MethodologyId, "OUT1", Arg.Any<CancellationToken>())
                .Returns(new CalculationBinding(TableDefId, ColumnDefId, MethodologyId, "OUT1", "{}"));
        }

        var matchJson = kind == "update" ? """{"kind":"stack"}""" : "{}";

        // ⚠ Налаштування `Returns` саме викликає метод підстановки й будить
        // `When…Do`: журнал до цього рядка — не обробника.
        _log.Clear();

        await Handler().HandleAsync(
            MethodologyId, ColumnDefId, "OUT1", matchJson, isActive: kind != "unbind", CancellationToken.None);

        var inside = _log
            .SkipWhile(e => e != "begin").Skip(1)
            .TakeWhile(e => e != "commit")
            .ToList();

        Assert.True(inside.Count > 0, $"Транзакції не було: {string.Join(", ", _log)}");
        Assert.Equal($"+lock:{TemplateVersionId}", inside[0]);
        Assert.Single(_log, e => e.EndsWith($"lock:{TemplateVersionId}", StringComparison.Ordinal));

        // Запис і журнал — у тій самій транзакції, що тримає блок, а не окремою.
        Assert.Contains("+save", inside);
        Assert.Contains("+audit", inside);
        Assert.Contains("+methodology", inside);
        Assert.Contains("+existing", inside);
        Assert.DoesNotContain(_log, e => e is "save" or "audit" or "add" or "existing");
    }

    /// <summary>
    /// Прив'язка до колонки ОПУБЛІКОВАНОЇ версії шаблону проходить: блок лише
    /// впорядковує її з публікацією, а не забороняє.
    /// </summary>
    /// <remarks>
    /// ⛔ Рішення C5b, а не пропуск: джерело <c>Calculated</c>-колонки заводить
    /// методолог уже ПІСЛЯ публікації структури
    /// (<c>PublishChecks.CheckComputedColumns</c>,
    /// <c>CalculationOrchestratorConcurrencyScenarios</c>). Мутація: замінити
    /// блок на <c>DraftVersionLock.EnsureDraftUnderLockAsync</c> (заборона
    /// <c>ECR-TMPL-0409</c>) — тест червоний.
    /// </remarks>
    [Theory]
    [InlineData(TemplateVersionStatus.Published)]
    [InlineData(TemplateVersionStatus.Deprecated)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Прив_язка_до_колонки_опублікованої_версії_дозволена_блок_лише_впорядковує(
        TemplateVersionStatus status)
    {
        _templateVersions.LockVersionForUpdateAsync(TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(status);

        var saved = await Handler().HandleAsync(
            MethodologyId, ColumnDefId, "OUT1", "{}", isActive: true, CancellationToken.None);

        Assert.Equal(ColumnDefId, saved.ColumnDefId);
        _bindings.ReceivedWithAnyArgs(1).Add(null!);
        await _templateVersions.Received(1).LockVersionForUpdateAsync(TemplateVersionId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Колонки немає — блокувати нічого, відмова та сама, що й до C5b (<c>ECR-TMPL-0404</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонки_немає_блок_не_береться_відмова_404()
    {
        _bindings.FindColumnAsync(ColumnDefId, Arg.Any<CancellationToken>())
            .Returns((BoundColumnRef?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(
                MethodologyId, ColumnDefId, "OUT1", "{}", isActive: true, CancellationToken.None));

        Assert.Equal("err.ECR-TMPL-0404.column", error.Details!["messageKey"]);
        await _templateVersions.DidNotReceiveWithAnyArgs().LockVersionForUpdateAsync(default, default);
        _bindings.DidNotReceiveWithAnyArgs().Add(null!);
    }

    private void Log(string entry) => _log.Add(_inTransaction ? "+" + entry : entry);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
