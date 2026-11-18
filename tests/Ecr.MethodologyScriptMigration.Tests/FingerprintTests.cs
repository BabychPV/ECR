using Ecr.Domain.Enums;
using Ecr.MethodologyScriptMigration.Conversion;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

public sealed class FingerprintTests
{
    private static readonly SourceRule RuleA = new(
    [
        new SourceMatchPair("Fuel", SourceMatchValue.OfEntry("FUEL", "GAS")),
        new SourceMatchPair("Zone", SourceMatchValue.OfText("North")),
    ]);

    private static readonly SourceRule RuleB = new([new SourceMatchPair("Zone", SourceMatchValue.OfText("South"))]);

    [Fact]
    public void Формат_пакета_дорівнює_формату_відбитка_щоб_старі_пакети_відхилялися()
    {
        Assert.Equal(2, Ecr.Application.Calculations.Scripting.Migration.Fingerprint.FormatVersion);
        Assert.Equal(Ecr.Application.Calculations.Scripting.Migration.Fingerprint.FormatVersion, Cli.ScriptPackage.CurrentVersion);
    }

    private static VersionSource Base() => new()
    {
        MethodologyCode = "M",
        VersionLabel = "V1",
        NumericMode = NumericMode.Legacy,
        CalendarMode = CalendarMode.Actual,
        RowActivityFilter = "ST:RUN",
        CategoryRule = "@Fuel",
        Categories = ["A", "B"],
        Substances = ["0301", "0304"],
        Formulas =
        [
            new SourceFormula("Mass", "@Qty * 2", 1, MethodologyFormulaScope.Row, true, FormulaResultType.Number, "t", "@Qty?"),
            new SourceFormula("Em", "!Mass * CST.EF", 2),
        ],
        Outputs = [new SourceOutput("Em", "t", true, 1), new SourceOutput("Mass", "t", false, 2)],
        Constants =
        [
            SourceConstant.OfNumber("EF", 0.5m, "A", "0301", null, new DateOnly(2025, 1, 1), "kg"),
            SourceConstant.OfNumber("EF", 0.7m, "B", "0304"),
            SourceConstant.OfText("Label", "x"),
        ],
        Rules = [RuleA, RuleB],
        Imports =
        [
            new SourceImport("Lib", ["Dens", "Visc"], ["A", "B"], ["Qty"]),
            new SourceImport("Lib2", ["Q"], [], []),
        ],
        EffectiveFrom = new DateOnly(2024, 1, 1),
        NextEffectiveFrom = new DateOnly(2026, 1, 1),
        RowsClauses =
        [
            new SourceRowsClause(["T1", "T2"], [RuleA], [new SourceActiveColumn("State", "ST", "RUN")]),
            new SourceRowsClause(["T3"], [RuleA], []),
        ],
    };

    [Fact]
    public void Відбиток_детермінований_і_має_64_hex_символи()
    {
        var a = Fingerprint.ComputeHex(Base());
        var b = Fingerprint.ComputeHex(Base());
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Інший_порядок_рядків_дає_той_самий_відбиток()
    {
        var source = Base();
        var shuffled = source with
        {
            Categories = [.. source.Categories.Reverse()],
            Substances = [.. source.Substances.Reverse()],
            Formulas = [.. source.Formulas.Reverse()],
            Outputs = [.. source.Outputs.Reverse()],
            Constants = [.. source.Constants.Reverse()],
            Rules = [.. source.Rules.Reverse().Select(r => new SourceRule([.. r.Pairs.Reverse()]))],
            Imports =
            [
                .. source.Imports.Reverse().Select(i => new SourceImport(
                    i.LibraryCode, [.. i.Members.Reverse()], [.. i.ConstantCategories!.Reverse()], [.. i.OptionalArguments!.Reverse()])),
            ],
            RowsClauses =
            [
                .. source.RowsClauses.Reverse().Select(c => new SourceRowsClause(
                    [.. c.TableCodes.Reverse()], c.Rules, [.. c.ActiveColumns.Reverse()])),
            ],
        };

        Assert.Equal(Fingerprint.ComputeHex(source), Fingerprint.ComputeHex(shuffled));
    }

    [Fact]
    public void Мітка_версії_у_відбиток_не_входить_бо_це_лише_коментар_заголовка()
    {
        var source = Base();
        var changed = source with { VersionLabel = "V9" };

        Assert.Equal(Fingerprint.ComputeHex(source), Fingerprint.ComputeHex(changed));
    }

    public static TheoryData<string> Changes =>
    [
        "methodology-kind", "numeric-mode", "calendar-mode", "activity-filter", "activity-filter-null", "category-rule",
        "category-added", "substance-added", "substance-changed",
        "formula-expression", "formula-scope", "formula-visible", "formula-result-type", "formula-unit", "formula-order",
        "formula-code", "formula-removed",
        "output-unit", "output-per-substance", "output-ordinal", "output-removed",
        "constant-number", "constant-text", "constant-unit", "constant-category", "constant-substance", "constant-from",
        "constant-to", "constant-kind", "constant-removed",
        "rule-text-value", "rule-entry-value", "rule-column", "rule-removed", "rule-pair-added",
        "import-library", "import-member", "import-removed",
        "formula-arguments-csv", "formula-arguments-csv-null", "import-categories", "import-optional", "import-optional-null",
        "life-from", "life-next", "life-next-null", "rows-table", "rows-table-added", "rows-active-column", "rows-active-entry",
        "rows-active-removed", "rows-clause-removed",
    ];

    private static VersionSource Change(VersionSource s, string change) => change switch
    {
        "methodology-kind" => s with { IsLibrary = true },
        "numeric-mode" => s with { NumericMode = NumericMode.Strict },
        "calendar-mode" => s with { CalendarMode = Enum.GetValues<CalendarMode>().First(m => m != s.CalendarMode) },
        "activity-filter" => s with { RowActivityFilter = "ST:STOP" },
        "activity-filter-null" => s with { RowActivityFilter = null },
        "category-rule" => s with { CategoryRule = "@Zone" },
        "category-added" => s with { Categories = [.. s.Categories, "C"] },
        "substance-added" => s with { Substances = [.. s.Substances, "0330"] },
        "substance-changed" => s with { Substances = ["0301", "0305"] },
        "formula-expression" => ReplaceFormula(s, 1, f => f with { Expression = "!Mass * CST.EF * 2" }),
        "formula-scope" => ReplaceFormula(s, 0, f => f with { Scope = MethodologyFormulaScope.Substance }),
        "formula-visible" => ReplaceFormula(s, 0, f => f with { IsVisible = false }),
        "formula-result-type" => ReplaceFormula(s, 0, f => f with { ResultType = FormulaResultType.Text }),
        "formula-unit" => ReplaceFormula(s, 0, f => f with { UnitCode = "kg" }),
        "formula-order" => ReplaceFormula(s, 0, f => f with { EvaluationOrder = 7 }),
        "formula-code" => ReplaceFormula(s, 0, f => f with { Code = "Mass2" }),
        "formula-removed" => s with { Formulas = [s.Formulas[0]] },
        "output-unit" => s with { Outputs = [s.Outputs[0] with { UnitCode = "kg" }, s.Outputs[1]] },
        "output-per-substance" => s with { Outputs = [s.Outputs[0] with { IsPerSubstance = false }, s.Outputs[1]] },
        "output-ordinal" => s with { Outputs = [s.Outputs[0] with { Ordinal = 9 }, s.Outputs[1]] },
        "output-removed" => s with { Outputs = [s.Outputs[0]] },
        "constant-number" => ReplaceConstant(s, 0, c => c with { Number = 0.51m }),
        "constant-text" => ReplaceConstant(s, 2, c => c with { Text = "y" }),
        "constant-unit" => ReplaceConstant(s, 0, c => c with { UnitCode = "g" }),
        "constant-category" => ReplaceConstant(s, 0, c => c with { Category = "B" }),
        "constant-substance" => ReplaceConstant(s, 0, c => c with { SubstanceCode = "0304" }),
        "constant-from" => ReplaceConstant(s, 0, c => c with { ValidFrom = new DateOnly(2025, 1, 2) }),
        "constant-to" => ReplaceConstant(s, 0, c => c with { ValidTo = new DateOnly(2030, 1, 1) }),
        "constant-kind" => ReplaceConstant(s, 2, c => c with { Kind = ConstantKind.CategoryLabel }),
        "constant-removed" => s with { Constants = [.. s.Constants.Skip(1)] },
        "rule-text-value" => s with { Rules = [RuleA, new SourceRule([new SourceMatchPair("Zone", SourceMatchValue.OfText("East"))])] },
        "rule-entry-value" => s with
        {
            Rules =
            [
                new SourceRule([new SourceMatchPair("Fuel", SourceMatchValue.OfEntry("FUEL", "OIL")), RuleA.Pairs[1]]),
                RuleB,
            ],
        },
        "rule-column" => s with { Rules = [RuleA, new SourceRule([new SourceMatchPair("Area", SourceMatchValue.OfText("South"))])] },
        "rule-removed" => s with { Rules = [RuleA] },
        "rule-pair-added" => s with { Rules = [RuleA, new SourceRule([.. RuleB.Pairs, new SourceMatchPair("Fuel", SourceMatchValue.OfText("1"))])] },
        "import-library" => s with { Imports = [new SourceImport("Other", ["Dens", "Visc"]), s.Imports[1]] },
        "import-member" => s with { Imports = [new SourceImport("Lib", ["Dens"]), s.Imports[1]] },
        "import-removed" => s with { Imports = [s.Imports[0]] },
        "formula-arguments-csv" => ReplaceFormula(s, 0, f => f with { ArgumentsCsv = "@Qty" }),
        "formula-arguments-csv-null" => ReplaceFormula(s, 0, f => f with { ArgumentsCsv = null }),
        "import-categories" => s with { Imports = [new SourceImport("Lib", ["Dens", "Visc"], ["A"], ["Qty"]), s.Imports[1]] },
        "import-optional" => s with { Imports = [new SourceImport("Lib", ["Dens", "Visc"], ["A", "B"], []), s.Imports[1]] },
        "import-optional-null" => s with { Imports = [new SourceImport("Lib", ["Dens", "Visc"], ["A", "B"], null), s.Imports[1]] },
        "life-from" => s with { EffectiveFrom = new DateOnly(2024, 1, 2) },
        "life-next" => s with { NextEffectiveFrom = new DateOnly(2026, 1, 2) },
        "life-next-null" => s with { NextEffectiveFrom = null },
        "rows-table" => s with { RowsClauses = [new SourceRowsClause(["T1", "T9"], [RuleA], s.RowsClauses[0].ActiveColumns), s.RowsClauses[1]] },
        "rows-table-added" => s with { RowsClauses = [.. s.RowsClauses, new SourceRowsClause(["T4"], [RuleA], [])] },
        "rows-active-column" => s with
        {
            RowsClauses = [new SourceRowsClause(["T1", "T2"], [RuleA], [new SourceActiveColumn("Status", "ST", "RUN")]), s.RowsClauses[1]],
        },
        "rows-active-entry" => s with
        {
            RowsClauses = [new SourceRowsClause(["T1", "T2"], [RuleA], [new SourceActiveColumn("State", "ST", "STOP")]), s.RowsClauses[1]],
        },
        "rows-active-removed" => s with { RowsClauses = [new SourceRowsClause(["T1", "T2"], [RuleA], []), s.RowsClauses[1]] },
        "rows-clause-removed" => s with { RowsClauses = [s.RowsClauses[0]] },
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };

    private static VersionSource ReplaceFormula(VersionSource s, int index, Func<SourceFormula, SourceFormula> change)
        => s with { Formulas = [.. s.Formulas.Select((f, i) => i == index ? change(f) : f)] };

    private static VersionSource ReplaceConstant(VersionSource s, int index, Func<SourceConstant, SourceConstant> change)
        => s with { Constants = [.. s.Constants.Select((c, i) => i == index ? change(c) : c)] };

    [Theory]
    [MemberData(nameof(Changes))]
    public void Будь_яка_зміна_вмісту_змінює_відбиток(string change)
    {
        var source = Base();

        Assert.NotEqual(Fingerprint.ComputeHex(source), Fingerprint.ComputeHex(Change(source, change)));
    }

    [Fact]
    public void Запис_довідника_і_текст_із_однаковими_рядками_не_плутаються()
    {
        var entry = Base() with { Rules = [new SourceRule([new SourceMatchPair("Fuel", SourceMatchValue.OfEntry("FUEL", "GAS"))])] };
        var text = Base() with { Rules = [new SourceRule([new SourceMatchPair("Fuel", SourceMatchValue.OfText("GAS"))])] };

        Assert.NotEqual(Fingerprint.ComputeHex(entry), Fingerprint.ComputeHex(text));
    }

    [Fact]
    public void Число_з_різною_кількістю_нулів_дає_той_самий_відбиток()
    {
        var a = Base() with { Constants = [SourceConstant.OfNumber("K", 1.50m)] };
        var b = Base() with { Constants = [SourceConstant.OfNumber("K", 1.5m)] };

        Assert.Equal(Fingerprint.ComputeHex(a), Fingerprint.ComputeHex(b));
    }
}
