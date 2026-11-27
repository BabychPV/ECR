using Ecr.Application.Calculations;
using Ecr.Application.Documents.RequiredInputs;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ DOCUX (живий прохід 27.11): «Type of fuel» — ключ правил методології (<c>MatchJson</c>). Його спорожнили — рядок мовчки
/// випав із розрахунку: ні позначки, ні лічильника, лише «no calculation rule» +1 у підсумку. Тепер перелік таблиці
/// (<c>GET …/required-inputs</c>, «Перевірити») називає порожній ключ розпочатого рядка, якого не порахує жодна методологія, —
/// ПОПЕРЕДЖЕННЯМ (амбра, лічильник; подання не блокує — D-578 блокує лише Block).
/// </summary>
/// <remarks>
/// Мутації (локально, не комітити): (1) у <c>RequiredInputsScanner.Scan</c> передавати <c>ruleKeyInput: null</c> — червоний
/// <c>Порожній_ключ_…</c>; (2) у <c>EvaluateRow</c> прибрати умову <c>!anyMatched</c> — червоний <c>…інша_методологія…</c>;
/// (3) у <c>RuleKeyWarnings</c> прибрати перевірку розбіжності заповненого ключа — червоний «Boiler».
/// </remarks>
public sealed partial class PatchCellsTests
{
    private const string CategoryOnlyRule = """{"12":"Engine"}""";

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-578")]
    [InlineData(15)] // ключ не заведено вимогою (вимога — інша колонка)
    [InlineData(12)] // ключ — вимога Block, але рядок «не розпочато» в розумінні воріт запису (лише ключі-вимоги)
    public async Task Порожній_ключ_правила_розпочатого_рядка_попередження_в_переліку(int requiredColumnId)
    {
        WithMethodology(RequiredInputSeverity.Block, matchJson: CategoryOnlyRule, requiredColumnId: requiredColumnId);
        var snapshot = await _metadata.GetAsync(2, CancellationToken.None);
        var table = snapshot.Sheets[0].Tables[0];
        var plan = await new RequiredInputsScanner(_methodologies, _periods).PlanAsync(700, Period, 2, table, CancellationToken.None);
        Assert.NotNull(plan);

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1, ["R2"] = 2, ["R3"] = 3, ["R4"] = 4 };
        CellRecord[] cells =
        [
            Cell(1, VolumeColumnId, new CellValueData { ValueNumeric = 5m }),       // розпочатий, Category порожня → попередження
            Cell(2, VolumeColumnId, new CellValueData { ValueNumeric = 6m }),
            Cell(2, 12, new CellValueData { ValueString = "Boiler" }),            // інший варіант: ключ заповнено, правило не збіглося
            Cell(3, VolumeColumnId, new CellValueData { ValueNumeric = 7m }),
            Cell(3, 12, new CellValueData { ValueString = "Engine" }),            // збіглося
            // R4 — порожній запасний рядок: без позначок (Q4).
        ];

        var messages = RequiredInputsScanner.Scan(plan!, table, snapshot.ColumnsById, cells, rowIds, "en")
            .Where(m => m.ColumnCode == "Category")
            .ToList();

        var only = Assert.Single(messages);
        Assert.Equal("R1", only.RowKey);
        Assert.Equal(ValidationSeverity.Warning, only.Severity);
        Assert.False(only.BlocksSave);
        Assert.Equal(["ECW_TEST"], only.Methodologies);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-578")]
    [InlineData("optional")]
    [InlineData("calculated")]
    public async Task Порожній_ключ_правила_мовчить_якщо_методолог_зняв_вимогу_або_колонка_не_вхід(string kind)
    {
        WithMethodology(
            kind == "optional" ? RequiredInputSeverity.Optional : RequiredInputSeverity.Block,
            categoryType: kind == "calculated" ? CellDataType.Calculated : CellDataType.String,
            matchJson: CategoryOnlyRule,
            requiredColumnId: kind == "optional" ? 12 : 15);
        var snapshot = await _metadata.GetAsync(2, CancellationToken.None);
        var table = snapshot.Sheets[0].Tables[0];
        var plan = await new RequiredInputsScanner(_methodologies, _periods).PlanAsync(700, Period, 2, table, CancellationToken.None);

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1 };
        CellRecord[] cells = [Cell(1, VolumeColumnId, new CellValueData { ValueNumeric = 5m })];

        var messages = plan is null
            ? []
            : RequiredInputsScanner.Scan(plan, table, snapshot.ColumnsById, cells, rowIds, "en").Where(m => m.ColumnCode == "Category").ToList();

        Assert.Empty(messages);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-578")]
    public void Рядок_який_рахує_інша_методологія_ключів_цієї_не_вимагає()
    {
        // Дві методології таблиці: A відбирає рядки за Category (12), B — за Stream (16). Рядок B (Stream = Gas) з порожньою
        // Category порахує B — попередження про ключ A було б хибним. Рядок без жодного збігу — попередження про обидва ключі.
        var a = new ApplicableMethodology("A", [new LocalizedRule("a", 1, """{"12":"Engine"}""", [])], [], [], null);
        var b = new ApplicableMethodology("B", [new LocalizedRule("b", 1, """{"16":"Gas"}""", [])], [], [], null);
        List<int> needed = [12, 16];

        List<string> Warned(string? stream)
            => [.. MethodologyRequiredInputs
                .EvaluateRow([a, b], needed, id => id == 16 ? stream : null, _ => false, humanBatch: true, ruleKeyInput: _ => true)
                .Messages(3, new Dictionary<int, Domain.Entities.Configuration.ColumnDef>(), "R1", "en")
                .Select(m => $"{m.ColumnCode}:{m.Severity}")];

        Assert.Empty(Warned("Gas"));
        Assert.Equal(["12:Warning", "16:Warning"], Warned(null));
        // Заповнений ключ B розходиться з правилом — рядок не віднесено ні до A (ключ A порожній), ні до B: лише ключ A.
        Assert.Equal(["12:Warning"], Warned("Coal"));

        // Ворота запису (ruleKeyInput = null) — як доти: тиша.
        Assert.Empty(MethodologyRequiredInputs
            .EvaluateRow([a, b], needed, _ => null, _ => false, humanBatch: true)
            .Messages(3, new Dictionary<int, Domain.Entities.Configuration.ColumnDef>(), "R1", "en"));
    }
}
