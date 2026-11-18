using System.Globalization;
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Application.Ports;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// Старий рушій і ПЕРЕНЕСЕНИЙ скрипт (текст дає справжній перетворювач) на одних і тих самих рядках через
/// <see cref="ParityComparer.ExecuteRowsAsync"/>, без бази: числа, відхилення рядка й помилки збігаються.
/// </summary>
/// <remarks>
/// Мутації: перетворювач не пише <c>StripSpaces</c> у правилі категорії — червоніє
/// <see cref="Ключ_категорії_з_іншим_розподілом_пробілів_дає_ті_самі_числа"/> (старий рушій приймає ключ
/// «Over  hauled», скрипт відхиляє).
/// </remarks>
public sealed class ConvertedEquivalenceTests
{
    private const string Fuel = "Fuel";

    private sealed class DelegateModule(Func<CalculationInput, Task<CalculationOutput>> run) : ICalculationModule
    {
        public string Code => "delegate";

        public CalculationLevel Level => CalculationLevel.Configuration;

        public bool CanHandle(MethodologyDescriptor methodology) => true;

        public Task<CalculationBindingContext> PrepareAsync(
            MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CalculationOutput> ExecuteAsync(CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
            => run(input);

        public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct) => run(input);
    }

    private static CalculationArgument Text(string code, string value) => new(code, null, value, null);

    private static CalculationArgument Number(string code, decimal value) => new(code, value, null, null);

    private static Dictionary<string, CalculationInput> Inputs(IReadOnlyList<(string Key, CalculationArgument[] Arguments)> rows)
        => rows.ToDictionary(
            r => r.Key,
            r => new CalculationInput(
                ScriptStand.Descriptor(NumericMode.Strict), ScriptStand.DocumentId, 500,
                new PeriodKey(ScriptStand.PeriodValue), r.Key, r.Arguments),
            StringComparer.Ordinal);

    private static string Show(IReadOnlyDictionary<string, ParityRowOutcome> outcomes)
        => string.Join(
            "\n",
            outcomes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                p.Key + " => "
                + string.Join(
                    "; ",
                    p.Value.Values.OrderBy(v => v.Key, StringComparer.Ordinal)
                        .Select(v => v.Key + "=" + v.Value.ToString(CultureInfo.InvariantCulture)))
                + " | rejected=" + p.Value.Rejected
                + " | stepError=" + p.Value.HasStepError
                + " | missing=" + string.Join(',', p.Value.MissingArguments ?? [])
                + " | error=" + p.Value.Error));

    /// <summary>Ключ категорії: старий і новий шлях дають однакові числа, коли пробіли в ключі розподілені інакше.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Ключ_категорії_з_іншим_розподілом_пробілів_дає_ті_самі_числа()
    {
        var oldStand = new RuleStand(rule: "@" + Fuel)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceCodes("0301", "0304")
            .WithConstants(
                RuleStand.Number("EF", 1m, "Over hauled"),
                RuleStand.Number("EF", 3m, "Heavy duty"),
                RuleStand.Number("EF", 5m, "Summer_A"));

        var source = new VersionSource
        {
            MethodologyCode = "WS",
            NumericMode = NumericMode.Strict,
            Formulas = [new SourceFormula("Total", "@X * CST.EF", 1)],
            Outputs = [new SourceOutput("Total", "kg")],
            Constants =
            [
                SourceConstant.OfNumber("EF", 1m, "Over hauled"),
                SourceConstant.OfNumber("EF", 3m, "Heavy duty"),
                SourceConstant.OfNumber("EF", 5m, "Summer_A"),
            ],
            CategoryRule = "@" + Fuel,
            Substances = ["0301", "0304"],
            RowsClauses = [new SourceRowsClause(["T1"], [new SourceRule([])], [])],
        };
        var converted = ScriptConverter.Convert(source);
        Assert.True(converted.Text is not null, string.Join(" | ", converted.Issues.Select(i => i.Code + ": " + i.Message)));

        var scriptStand = new ScriptStand(converted.Text, NumericMode.Strict)
            .WithSubstances(("0301", (int)RuleStand.SubstanceA), ("0304", (int)RuleStand.SubstanceB));
        scriptStand.AddTable("T1");

        (string Key, CalculationArgument[] Arguments)[] rows =
        [
            ("exact", [Number("X", 10m), Text(Fuel, "Over hauled")]),
            ("bare", [Number("X", 10m), Text(Fuel, "Overhauled")]),
            ("double", [Number("X", 10m), Text(Fuel, "Over  hauled")]),
            ("padded", [Number("X", 10m), Text(Fuel, "  Over hauled ")]),
            ("tab", [Number("X", 10m), Text(Fuel, "Over\thauled")]),
            ("other", [Number("X", 10m), Text(Fuel, "Heavy   duty")]),
            ("prefix", [Number("X", 10m), Text(Fuel, "Sum mer")]),
            ("unknown", [Number("X", 10m), Text(Fuel, "Unknown kind")]),
            ("blank", [Number("X", 10m), Text(Fuel, "   ")]),
        ];
        var inputs = Inputs(rows);
        var keys = rows.Select(r => r.Key).ToList();

        var oldModule = new DelegateModule(input => oldStand.RunAsync([.. input.Arguments]));
        var scriptModule = new DelegateModule(input => scriptStand.RunArgumentsAsync([.. input.Arguments]));

        var oldRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, oldModule, null!, CancellationToken.None);
        var scriptRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, scriptModule, null!, CancellationToken.None);

        // Контроль: набір не порожній і справді розрізняє результати (не «усі відхилено»).
        Assert.Equal(10m, oldRows["exact"].Values.First(v => v.SubstanceEntryId == 9001).Value);
        Assert.Equal(10m, oldRows["double"].Values.First(v => v.SubstanceEntryId == 9001).Value);
        Assert.Equal(30m, oldRows["other"].Values.First(v => v.SubstanceEntryId == 9001).Value);
        Assert.True(oldRows["unknown"].Rejected);

        Assert.Equal(Show(oldRows), Show(scriptRows));
    }

    /// <summary>Сама функція мови: прибирає будь-які пробільні символи, не чіпає не-текст.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task StripSpaces_прибирає_усі_пробільні_символи_і_не_чіпає_число()
    {
        var stand = new ScriptStand(
            "rows from T1;\nvar k = StripSpaces(key(row.K));\noutput Q : kg = k == \"AB\" ? 1 : 0;\noutput N : kg = StripSpaces(row.N) + 1;",
            NumericMode.Strict);
        stand.AddTable("T1");

        var output = await stand.RunArgumentsAsync(Text("K", " A \t B "), Number("N", 4m));

        Assert.Equal(1m, ScriptStand.Value(output, "Q"));
        Assert.Equal(5m, ScriptStand.Value(output, "N"));
    }
}
