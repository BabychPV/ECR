using System.Globalization;
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Application.Ports;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Entities.Calculations;
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

    private static Dictionary<string, CalculationInput> Inputs(
        IReadOnlyList<(string Key, CalculationArgument[] Arguments)> rows,
        MethodologyDescriptor? descriptor = null,
        long documentId = ScriptStand.DocumentId,
        int period = ScriptStand.PeriodValue)
        => rows.ToDictionary(
            r => r.Key,
            r => new CalculationInput(
                descriptor ?? ScriptStand.Descriptor(NumericMode.Strict), documentId, 500, new PeriodKey(period), r.Key, r.Arguments),
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

        var oldRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, oldModule, null!, false, CancellationToken.None);
        var scriptRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, scriptModule, null!, false, CancellationToken.None);

        // Контроль: набір не порожній і справді розрізняє результати (не «усі відхилено»).
        Assert.Equal(10m, oldRows["exact"].Values.First(v => v.SubstanceEntryId == 9001).Value);
        Assert.Equal(10m, oldRows["double"].Values.First(v => v.SubstanceEntryId == 9001).Value);
        Assert.Equal(30m, oldRows["other"].Values.First(v => v.SubstanceEntryId == 9001).Value);
        Assert.True(oldRows["unknown"].Rejected);

        Assert.Equal(Show(oldRows), Show(scriptRows));
    }

    /// <summary>
    /// Три стани обов'язкового аргументу: значення є / запис комірки є, але порожній / запису немає зовсім. Старий рушій
    /// у другому стані дає <c>null</c> без помилки, <c>#ARG</c> — лише в третьому; перенесений скрипт — так само.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожній_запис_комірки_обовязкового_аргументу_дає_null_а_відсутній_запис_дає_ARG_як_у_старого_рушія()
    {
        var oldStand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceFormula("Pick", "if(@X > 0, 1, 2)")
            .WithSubstanceCodes("0301", "0304")
            .WithConstants(RuleStand.Number("EF", 2m, null!));

        var source = new VersionSource
        {
            MethodologyCode = "WS",
            NumericMode = NumericMode.Strict,
            Formulas = [new SourceFormula("Total", "@X * CST.EF", 1), new SourceFormula("Pick", "if(@X > 0, 1, 2)", 2)],
            Outputs = [new SourceOutput("Total", "kg"), new SourceOutput("Pick", "kg")],
            Constants = [SourceConstant.OfNumber("EF", 2m)],
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
            ("value", [Number("X", 10m), Text("Other", "a")]),
            ("emptyRecord", [new CalculationArgument("X", null, null, null), Text("Other", "a")]),
            ("noRecord", [Text("Other", "a")]),
        ];
        var inputs = Inputs(rows);
        var keys = rows.Select(r => r.Key).ToList();

        var oldModule = new DelegateModule(input => oldStand.RunAsync([.. input.Arguments]));
        var scriptModule = new DelegateModule(input => scriptStand.RunArgumentsAsync([.. input.Arguments]));
        var oldRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, oldModule, null!, false, CancellationToken.None);
        var scriptRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, scriptModule, null!, false, CancellationToken.None);

        Assert.Equal(20m, oldRows["value"].Values.First(v => v.OutputCode == "Total").Value);
        Assert.True(Show(oldRows) == Show(scriptRows), "OLD:\n" + Show(oldRows) + "\nNEW:\n" + Show(scriptRows));
    }

    /// <summary>
    /// D-556: необов'язковий аргумент (<c>Y?</c>), якого немає в рядку, НЕ «відсутній обов'язковий вхід»: рядок без жодного
    /// значення називає в <c>MissingArguments</c> лише обов'язкові поля, і старий рушій тут збігається зі скриптом
    /// (у колі 3 це були 504 розбіжності діагностики C11_10). Обов'язковий <c>X</c> без запису називається в обох.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Необовязковий_аргумент_без_запису_не_потрапляє_в_відсутні_ні_в_старому_ні_в_скрипті()
    {
        var oldStand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", "@X * @Y", argumentsCsv: "X;Y?")
            .WithSubstanceCodes("0301", "0304");

        var source = new VersionSource
        {
            MethodologyCode = "WS",
            NumericMode = NumericMode.Strict,
            Formulas = [new SourceFormula("Total", "@X * @Y", 1, ArgumentsCsv: "X;Y?")],
            Outputs = [new SourceOutput("Total", "kg")],
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
            ("both", [Number("X", 2m), Number("Y", 3m)]),
            ("noOptional", [Number("X", 2m)]),
            ("noRequired", [Number("Y", 3m)]),
            ("neither", [Text("Other", "a")]),
        ];
        var inputs = Inputs(rows);
        var keys = rows.Select(r => r.Key).ToList();

        var oldModule = new DelegateModule(input => oldStand.RunAsync([.. input.Arguments]));
        var scriptModule = new DelegateModule(input => scriptStand.RunArgumentsAsync([.. input.Arguments]));
        var oldRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, oldModule, null!, false, CancellationToken.None);
        var scriptRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, scriptModule, null!, false, CancellationToken.None);

        // Контроль: «noOptional» - порожній результат без назви відсутнього; обов'язковий X названо в обох.
        Assert.Null(oldRows["noOptional"].MissingArguments);
        Assert.Equal(["X"], oldRows["noRequired"].MissingArguments);
        Assert.Equal(["X"], oldRows["neither"].MissingArguments);
        Assert.True(Show(oldRows) == Show(scriptRows), "OLD:\n" + Show(oldRows) + "\nNEW:\n" + Show(scriptRows));
    }

    private static MethodologyConstant Scoped(string code, decimal value, string category, long substanceEntryId)
    {
        var constant = RuleStand.Number(code, value, category);
        constant.SetScope(category, substanceEntryId);
        return constant;
    }

    /// <summary>
    /// D-556: константи «категорія x речовина» з однаковим значенням по речовинах категорії стискаються в мапу
    /// «категорія -> значення» зі спільною картою наявності (у C11_01 це 383 тис. знаків проти ліміту 200 тис.). Результат
    /// той самий, що в старого рушія: наявна пара дає значення, відсутня (у K2 немає речовини 0304 у категорії B) - ні, як і раніше;
    /// категорія з різними значеннями по речовинах (D) лишається мапою мап.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Константи_категорія_речовина_зі_спільною_картою_наявності_дають_ті_самі_числа_що_старий_рушій()
    {
        const long a = RuleStand.SubstanceA;
        const long b = RuleStand.SubstanceB;
        MethodologyConstant[] constants =
        [
            Scoped("EF", 2m, "A", a), Scoped("EF", 2m, "A", b),
            Scoped("EF", 5m, "B", a), Scoped("EF", 5m, "B", b),
            Scoped("EF", 1m, "D", a), Scoped("EF", 9m, "D", b),
            Scoped("K2", 10m, "A", a), Scoped("K2", 10m, "A", b),
            Scoped("K2", 20m, "B", a),
            Scoped("K2", 30m, "D", a), Scoped("K2", 30m, "D", b),
        ];
        var oldStand = new RuleStand(rule: "@" + Fuel)
            .WithSubstanceFormula("Total", "@X * CST.EF / CST.K2")
            .WithSubstanceCodes("0301", "0304")
            .WithConstants(constants);

        var source = new VersionSource
        {
            MethodologyCode = "WS",
            NumericMode = NumericMode.Strict,
            Formulas = [new SourceFormula("Total", "@X * CST.EF / CST.K2", 1)],
            Outputs = [new SourceOutput("Total", "kg")],
            Constants =
            [
                SourceConstant.OfNumber("EF", 2m, "A", "0301"), SourceConstant.OfNumber("EF", 2m, "A", "0304"),
                SourceConstant.OfNumber("EF", 5m, "B", "0301"), SourceConstant.OfNumber("EF", 5m, "B", "0304"),
                SourceConstant.OfNumber("EF", 1m, "D", "0301"), SourceConstant.OfNumber("EF", 9m, "D", "0304"),
                SourceConstant.OfNumber("K2", 10m, "A", "0301"), SourceConstant.OfNumber("K2", 10m, "A", "0304"),
                SourceConstant.OfNumber("K2", 20m, "B", "0301"),
                SourceConstant.OfNumber("K2", 30m, "D", "0301"), SourceConstant.OfNumber("K2", 30m, "D", "0304"),
            ],
            CategoryRule = "@" + Fuel,
            Substances = ["0301", "0304"],
            RowsClauses = [new SourceRowsClause(["T1"], [new SourceRule([])], [])],
        };
        var converted = ScriptConverter.Convert(source);
        Assert.True(converted.Text is not null, string.Join(" | ", converted.Issues.Select(i => i.Code + ": " + i.Message)));

        // Компактна форма справді застосована (інакше тест нічого не доводить) і спільна карта оголошена один раз.
        Assert.Contains("Present[", converted.Text, StringComparison.Ordinal);
        Assert.Single(converted.Text.Split("const Present = ", StringSplitOptions.None).Skip(1));

        var scriptStand = new ScriptStand(converted.Text, NumericMode.Strict)
            .WithSubstances(("0301", (int)RuleStand.SubstanceA), ("0304", (int)RuleStand.SubstanceB));
        scriptStand.AddTable("T1");

        (string Key, CalculationArgument[] Arguments)[] rows =
        [
            ("A", [Number("X", 10m), Text(Fuel, "A")]),
            ("B", [Number("X", 10m), Text(Fuel, "B")]),
            ("D", [Number("X", 10m), Text(Fuel, "D")]),
            ("unknown", [Number("X", 10m), Text(Fuel, "Z")]),
        ];
        var inputs = Inputs(rows);
        var keys = rows.Select(r => r.Key).ToList();

        var oldModule = new DelegateModule(input => oldStand.RunAsync([.. input.Arguments]));
        var scriptModule = new DelegateModule(input => scriptStand.RunArgumentsAsync([.. input.Arguments]));
        var oldRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, oldModule, null!, false, CancellationToken.None);
        var scriptRows = await ParityComparer.ExecuteRowsAsync(keys, inputs, scriptModule, null!, false, CancellationToken.None);

        // Контроль: набір розрізняє категорії (A: 10*2/10 = 2; D: 0304 -> 10*9/30 = 3), а в B є лише речовина 0301 (у K2 немає 0304).
        Assert.Equal(2m, oldRows["A"].Values.First(v => v.SubstanceEntryId == 9001).Value);
        Assert.Equal(2m, oldRows["A"].Values.First(v => v.SubstanceEntryId == 9002).Value);
        Assert.Equal(3m, oldRows["D"].Values.First(v => v.SubstanceEntryId == 9002).Value);
        Assert.Single(oldRows["B"].Values);
        Assert.True(Show(oldRows) == Show(scriptRows), "OLD:\n" + Show(oldRows) + "\nNEW:\n" + Show(scriptRows));
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

    /// <summary>
    /// Необов'язковість «викликач → бібліотека»: маркер <c>Idle?</c> стоїть у формулі бібліотеки, на яку викликач не
    /// посилається, — старий рушій його не бачить (бере лише ПОТРІБНІ формули), тож <c>@Idle</c> у викликача обов'язковий.
    /// Імпорти будує справжній <see cref="ImportClosureSource"/> над тим самим сховищем, що й старий рушій.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Маркер_необовязковості_у_невикористаній_формулі_бібліотеки_не_діє_на_імпортера()
    {
        var stand = new Library.LibraryStand();
        const int commonVersion = Library.LibraryStand.CommonVersionId;
        const int callerVersion = Library.LibraryStand.CallerVersionId;
        var libraryFormulas = new List<MethodologyFormula>
        {
            Library.LibraryStand.Formula(commonVersion, "Common_M", "@Volume * CST.RHO", 1, MethodologyFormulaScope.Row, true),
            Library.LibraryStand.Formula(commonVersion, "Common_W", "!Common_M * CST.EF", 2, MethodologyFormulaScope.Substance, true),
            Library.LibraryStand.Formula(commonVersion, "Common_Spare", "@Idle * 2", 3, MethodologyFormulaScope.Row),
        };
        libraryFormulas[2].SetArguments("Idle?");
        var content = Library.LibraryStand.CommonContent(libraryFormulas);

        var callerFormulas = new List<MethodologyFormula>
        {
            Library.LibraryStand.Formula(callerVersion, "M_total", "!Common_M * 2 + @Idle", 1, MethodologyFormulaScope.Row),
            Library.LibraryStand.Formula(callerVersion, "tons", "!Common_W + CST.RHO", 2, MethodologyFormulaScope.Substance),
        };
        callerFormulas[0].SetArguments("Idle");
        stand.Caller(
            callerFormulas,
            [Library.LibraryStand.Output("M_total", perSubstance: false), Library.LibraryStand.Output("tons", perSubstance: true)]);
        stand.Imports(callerVersion, content);

        var imports = await ImportClosureSource.BuildAsync(
            stand.Store, new RealFormulaEngine(), Library.LibraryStand.CallerId, callerVersion,
            callerFormulas, Library.LibraryStand.BusinessDate, [content.Library], CancellationToken.None);
        Assert.Empty(Assert.Single(imports).OptionalArguments!);

        var caller = new VersionSource
        {
            MethodologyCode = "HSE400",
            NumericMode = NumericMode.Strict,
            Formulas =
            [
                new SourceFormula("M_total", "!Common_M * 2 + @Idle", 1, MethodologyFormulaScope.Row, false, FormulaResultType.Number, "t", "Idle"),
                new SourceFormula("tons", "!Common_W + CST.RHO", 2, MethodologyFormulaScope.Substance, false, FormulaResultType.Number, "t"),
            ],
            Outputs = [new SourceOutput("M_total", "t", false, 1), new SourceOutput("tons", "t", true, 2)],
            Constants = [SourceConstant.OfNumber("RHO", 0.5m)],
            Substances = ["S901", "S902"],
            Imports = imports,
            RowsClauses = [new SourceRowsClause(["T1"], [new SourceRule([])], [])],
        };
        var library = new VersionSource
        {
            MethodologyCode = "Common",
            IsLibrary = true,
            NumericMode = NumericMode.Strict,
            Formulas =
            [
                new SourceFormula("Common_M", "@Volume * CST.RHO", 1, MethodologyFormulaScope.Row, true),
                new SourceFormula("Common_W", "!Common_M * CST.EF", 2, MethodologyFormulaScope.Substance, true),
                new SourceFormula("Common_Spare", "@Idle * 2", 3, MethodologyFormulaScope.Row, false, FormulaResultType.Number, null, "Idle?"),
            ],
            Constants =
            [
                SourceConstant.OfNumber("RHO", 0.8m),
                SourceConstant.OfNumber("EF", 0.1m, substanceCode: "S901"),
                SourceConstant.OfNumber("EF", 0.2m, substanceCode: "S902"),
            ],
        };
        var callerText = ScriptConverter.Convert(caller);
        var libraryText = ScriptConverter.Convert(library);
        Assert.True(callerText.Text is not null, string.Join(" | ", callerText.Issues.Select(i => i.Code + ": " + i.Message)));
        Assert.True(libraryText.Text is not null, string.Join(" | ", libraryText.Issues.Select(i => i.Code + ": " + i.Message)));

        var scriptStand = new ScriptStand(callerText.Text, NumericMode.Strict)
            .WithLibrary("Common", 910, libraryText.Text)
            .WithSubstances(("S901", 901), ("S902", 902));
        scriptStand.AddTable("T1");

        (string Key, CalculationArgument[] Arguments)[] rows =
        [
            ("both", [Number("Volume", 100m), Number("Idle", 5m)]),
            ("noIdle", [Number("Volume", 100m)]),
        ];
        var keys = rows.Select(r => r.Key).ToList();
        var oldInputs = Inputs(rows, Library.LibraryStand.Input(TraceLevel.Full).Methodology, 700, 202601);
        var scriptInputs = Inputs(rows);

        var oldModule = new DelegateModule(input => stand.Module().ExecuteAsync(input, CancellationToken.None));
        var scriptModule = new DelegateModule(input => scriptStand.RunArgumentsAsync([.. input.Arguments]));
        var oldRows = await ParityComparer.ExecuteRowsAsync(keys, oldInputs, oldModule, null!, false, CancellationToken.None);
        var scriptRows = await ParityComparer.ExecuteRowsAsync(keys, scriptInputs, scriptModule, null!, false, CancellationToken.None);

        // Контроль: рядок з Idle рахується, а без нього старий рушій не вигадує значення.
        Assert.NotEmpty(oldRows["both"].Values);
        Assert.True(Show(oldRows) == Show(scriptRows), "OLD:\n" + Show(oldRows) + "\nNEW:\n" + Show(scriptRows));
    }
}
