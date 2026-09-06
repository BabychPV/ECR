using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Попередній перегляд матриці доступу <c>період × аркуш</c> (ФВ-2.18).
/// </summary>
/// <remarks>
/// ⛔ Перевіряється не «матриця повертається», а те, що вона показує ТЕ САМЕ,
/// що зробить система в документі. Перегляд, який розходиться з поведінкою,
/// шкідливіший за його відсутність: він переконує, що правила перевірені.
/// </remarks>
public sealed class AccessMatrixTests
{
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.18")]
    public async Task Шаблон_без_правил_доступний_у_всіх_періодах()
    {
        // ⚠ Замовчування — ДОСТУП. Зворотне («немає правила — заборонено»)
        // зробило б кожен новий аркуш недоступним, і ніхто б не зрозумів чому.
        Arrange(sheets: 1, tablesPerSheet: 2, rules: []);

        var matrix = await Handler().HandleAsync(1, CancellationToken.None);

        Assert.Equal(12, matrix.PeriodCount);
        Assert.All(matrix.Sheets[0].Cells, c => Assert.Equal(AccessMatrixState.Editable, c.State));
        Assert.False(matrix.Sheets[0].DependsOnData);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.18")]
    public async Task Вікно_номерів_періодів_видно_в_матриці()
    {
        var structure = Arrange(sheets: 1, tablesPerSheet: 1, rules: []);

        var rule = PeriodAccessRuleDef
            .EditablePeriodOnly(1, OutOfWindowBehavior.ReadOnly, fromSequence: 1, toSequence: 3)
            .ForSheet(structure.SheetId);

        Rules(rule);

        var matrix = await Handler().HandleAsync(1, CancellationToken.None);
        var cells = matrix.Sheets[0].Cells;

        Assert.Equal(AccessMatrixState.Editable, cells[0].State);
        Assert.Equal(AccessMatrixState.Editable, cells[2].State);
        Assert.Equal(AccessMatrixState.Blocked, cells[3].State);
        Assert.Equal(EditDenyReason.OutOfAccessWindow, cells[3].Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.18")]
    public async Task Правило_на_одну_таблицю_дає_частковий_стан()
    {
        // ⛔ Саме цей стан найчастіше й буває помилкою конфігурації: правило
        // писали на аркуш, а прикріпили до однієї таблиці з двох. Показати
        // його як «доступно» або як «заблоковано» означало б сховати помилку,
        // заради якої перегляд і відкривають.
        var structure = Arrange(sheets: 1, tablesPerSheet: 2, rules: []);

        Rules(PeriodAccessRuleDef
            .AlwaysReadOnly(1)
            .ForTable(structure.FirstTableId));

        var matrix = await Handler().HandleAsync(1, CancellationToken.None);

        Assert.All(matrix.Sheets[0].Cells, c => Assert.Equal(AccessMatrixState.Partial, c.State));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.18")]
    public async Task Правило_що_залежить_від_даних_оголошується_а_не_замовчується()
    {
        // ⛔ `SourceWindow` неможливо обчислити в шаблоні: запису довідника
        // рядок ще не обрав. Показати клітинку зеленою і промовчати означало б
        // збрехати — у документі там буде замок. Матриця каже про це прямо.
        var structure = Arrange(sheets: 1, tablesPerSheet: 1, rules: []);

        Rules(PeriodAccessRuleDef
            .ForSourceWindow(1, sourceColumnDefId: 99, OutOfWindowBehavior.ReadOnly)
            .ForTable(structure.FirstTableId));

        var matrix = await Handler().HandleAsync(1, CancellationToken.None);

        Assert.True(matrix.Sheets[0].DependsOnData);
        Assert.All(matrix.Sheets[0].Cells, c => Assert.Equal(AccessMatrixState.Editable, c.State));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.18")]
    public async Task Правило_іншого_аркуша_не_чіпає_цей()
    {
        var structure = Arrange(sheets: 2, tablesPerSheet: 1, rules: []);

        Rules(PeriodAccessRuleDef.AlwaysReadOnly(1).ForSheet(structure.SheetId));

        var matrix = await Handler().HandleAsync(1, CancellationToken.None);

        Assert.All(matrix.Sheets[0].Cells, c => Assert.Equal(AccessMatrixState.Blocked, c.State));
        Assert.All(matrix.Sheets[1].Cells, c => Assert.Equal(AccessMatrixState.Editable, c.State));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-2.18")]
    public async Task Без_права_Template_View_матриця_не_читається()
    {
        // ⛔ `A7-53`. Матриця показує КОНФІГУРАЦІЮ шаблону — які періоди
        // відкриті на кожному аркуші. Право на це оголошене контрактом, і
        // перевіряти його має обробник, а не `[Authorize]`.
        Arrange(sheets: 1, tablesPerSheet: 1, rules: []);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        var denied = await Assert.ThrowsAsync<Ecr.Application.Errors.AccessDeniedException>(
            () => Handler().HandleAsync(1, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    private GetAccessMatrixHandler Handler() => new(_metadata, _versions, _access, _user);

    private void Rules(params PeriodAccessRuleDef[] rules)
        => _versions.ListPeriodAccessRulesAsync(1, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PeriodAccessRuleDef>)rules);

    private Structure Arrange(int sheets, int tablesPerSheet, PeriodAccessRuleDef[] rules)
    {
        var builder = new TemplateBuilder();
        var firstSheetId = 0;
        var firstTableId = 0;

        for (var s = 1; s <= sheets; s++)
        {
            var sheet = builder.Sheet($"S{s}");
            if (s == 1)
            {
                firstSheetId = sheet.Id;
            }

            for (var t = 1; t <= tablesPerSheet; t++)
            {
                var table = builder.Table(sheet, $"T{s}_{t}");
                if (s == 1 && t == 1)
                {
                    firstTableId = table.Id;
                }

                builder.Column(table, $"C{s}_{t}");
            }
        }

        _metadata.GetAsync(1, Arg.Any<CancellationToken>()).Returns(builder.Build());
        Rules(rules);

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        return new Structure(firstSheetId, firstTableId);
    }

    private sealed record Structure(int SheetId, int FirstTableId);
}
