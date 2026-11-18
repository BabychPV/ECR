using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// Чисте порівняння паритету (MS, план §8.4): збіг не дає розбіжностей, а кожен аспект порівняння — вихід, значення до
/// останнього знака, одиниця, склад виходів, множини рядків, <c>MissingArguments</c>, <c>StepError</c>, відхилення —
/// дає саме свою розбіжність. Послаблення порівняння має червонити відповідний тест.
/// </summary>
public sealed class ParityDiffTests
{
    private static readonly ParityTarget Target = new("M1", "1.0", 77, 5, 209901, 900);

    private static ParityValue V(string code, decimal value, int? substance = null, int unit = 1, CalculationResultKind kind = CalculationResultKind.Output)
        => new(code, substance, kind, value, unit);

    private static ParityRowOutcome Row(string key, params ParityValue[] values) => new(key, values);

    private static ParitySide Side(
        string[] selected, string[]? excluded = null, string[]? all = null, params ParityRowOutcome[] rows)
        => new(selected, excluded ?? [], all ?? [.. selected, .. excluded ?? []], rows.ToDictionary(r => r.RowKey));

    private static ParitySide One(ParityRowOutcome row) => Side([row.RowKey], null, null, row);

    private static ParityReport Compare(ParitySide old, ParitySide script) => ParityDiff.Compare(Target, old, script);

    private static string[] Aspects(ParityReport report) => [.. report.Mismatches.Select(m => m.Aspect)];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Однакові_сторони_не_дають_розбіжностей_і_лічильники_рахують_рядки_й_значення()
    {
        var left = Side(
            ["R1", "R2"], ["R3"], ["R1", "R2", "R3"],
            Row("R1", V("Y", 1.5m), V("Z", 2m, substance: 9)),
            Row("R2", V("Y", 3m)));
        var right = Side(
            ["R1", "R2"], ["R3"], ["R1", "R2", "R3"],
            Row("R1", V("Y", 1.5m), V("Z", 2m, substance: 9)),
            Row("R2", V("Y", 3m)));

        var report = Compare(left, right);

        Assert.True(report.IsEqual, string.Join("; ", Aspects(report)));
        Assert.Equal(2, report.RowsCompared);
        Assert.Equal(3, report.ValuesCompared);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Значення_що_відрізняється_в_останньому_знаку_це_розбіжність_з_обома_числами()
    {
        var report = Compare(
            One(Row("R1", V("Y", 0.1234567890123456789012345678m))),
            One(Row("R1", V("Y", 0.1234567890123456789012345679m))));

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal(ParityAspect.ValueNumber, mismatch.Aspect);
        Assert.Equal("R1", mismatch.RowKey);
        Assert.Contains("0.1234567890123456789012345678", mismatch.Old, StringComparison.Ordinal);
        Assert.Contains("0.1234567890123456789012345679", mismatch.New, StringComparison.Ordinal);
        Assert.Equal(("M1", "1.0", 77, 5L, 209901, 900L), (mismatch.MethodologyCode, mismatch.Version, mismatch.VersionId, mismatch.DocumentId, mismatch.PeriodKey, mismatch.TableInstanceId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Рівні_числа_з_різною_кількістю_нулів_за_значенням_збігаються()
        => Assert.True(Compare(One(Row("R1", V("Y", 2.50m))), One(Row("R1", V("Y", 2.5m)))).IsEqual);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вихід_якого_немає_в_новому_і_зайвий_у_новому_розрізняються()
    {
        var missing = Compare(One(Row("R1", V("Y", 1m), V("Z", 2m))), One(Row("R1", V("Y", 1m))));
        var extra = Compare(One(Row("R1", V("Y", 1m))), One(Row("R1", V("Y", 1m), V("Z", 2m))));

        Assert.Equal([ParityAspect.ValueMissing], Aspects(missing));
        Assert.Equal([ParityAspect.ValueExtra], Aspects(extra));
        Assert.Equal(1, missing.ValuesCompared);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Різна_речовина_чи_вид_значення_це_інший_вихід()
    {
        var bySubstance = Compare(One(Row("R1", V("Y", 1m, substance: 1))), One(Row("R1", V("Y", 1m, substance: 2))));
        var byKind = Compare(
            One(Row("R1", V("Y", 1m))),
            One(Row("R1", V("Y", 1m, kind: CalculationResultKind.Intermediate))));

        Assert.Equal([ParityAspect.ValueMissing, ParityAspect.ValueExtra], Aspects(bySubstance));
        Assert.Equal([ParityAspect.ValueExtra, ParityAspect.ValueMissing], Aspects(byKind));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Різна_одиниця_це_розбіжність()
        => Assert.Equal(
            [ParityAspect.ValueUnit],
            Aspects(Compare(One(Row("R1", V("Y", 1m, unit: 1))), One(Row("R1", V("Y", 1m, unit: 2))))));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Рядок_відібраний_однією_стороною_і_виключені_множини_дають_розбіжності_за_ключем()
    {
        var left = Side(["R1", "R2"], ["R3"], ["R1", "R2", "R3", "R4"], Row("R1", V("Y", 1m)), Row("R2", V("Y", 2m)));
        var right = Side(["R1"], ["R4"], ["R1", "R2", "R3"], Row("R1", V("Y", 1m)));

        var report = Compare(left, right);

        Assert.Contains(report.Mismatches, m => m is { Aspect: ParityAspect.RowSelected, RowKey: "R2", Old: "selected", New: "absent" });
        Assert.Contains(report.Mismatches, m => m is { Aspect: ParityAspect.RowExcluded, RowKey: "R3", Old: "excluded", New: "absent" });
        Assert.Contains(report.Mismatches, m => m is { Aspect: ParityAspect.RowExcluded, RowKey: "R4", Old: "absent", New: "excluded" });
        Assert.Contains(report.Mismatches, m => m is { Aspect: ParityAspect.RowAll, RowKey: "R4", Old: "present", New: "absent" });

        // Рядок, відібраний однією стороною, за значеннями не порівнюється: лише спільний.
        Assert.Equal(1, report.RowsCompared);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void MissingArguments_наявність_і_склад_порівнюються_окремо()
    {
        var withA = new ParityRowOutcome("R1", [], ["A"]);
        var withAB = new ParityRowOutcome("R1", [], ["B", "A"]);
        var none = new ParityRowOutcome("R1", []);
        var empty = new ParityRowOutcome("R1", [], []);

        Assert.Equal([ParityAspect.MissingArgumentsPresence], Aspects(Compare(One(withA), One(none))));
        Assert.Equal([ParityAspect.MissingArgumentsSet], Aspects(Compare(One(withA), One(withAB))));
        Assert.True(Compare(One(withAB), One(new ParityRowOutcome("R1", [], ["A", "B"]))).IsEqual);

        // null і порожній перелік — «немає» для обох.
        Assert.True(Compare(One(none), One(empty)).IsEqual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void StepError_і_відхилення_рядка_порівнюються_за_наявністю()
    {
        var plain = new ParityRowOutcome("R1", []);

        Assert.Equal(
            [ParityAspect.StepErrorPresence],
            Aspects(Compare(One(plain with { HasStepError = true }), One(plain))));
        Assert.Equal(
            [ParityAspect.Rejected],
            Aspects(Compare(One(plain), One(plain with { Rejected = true }))));
        Assert.True(Compare(One(plain with { Rejected = true }), One(plain with { Rejected = true })).IsEqual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Виняток_одного_зі_шляхів_це_розбіжність_а_не_пропуск()
    {
        var plain = new ParityRowOutcome("R1", [V("Y", 1m)]);
        var report = Compare(One(plain), One(plain with { Error = "ECR-CALC-0422: err.x" }));

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal(ParityAspect.Error, mismatch.Aspect);
        Assert.Null(mismatch.Old);
        Assert.Equal("ECR-CALC-0422: err.x", mismatch.New);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дубль_трійки_в_одній_зі_сторін_видно()
    {
        var report = Compare(One(Row("R1", V("Y", 1m))), One(Row("R1", V("Y", 1m), V("Y", 1m))));

        Assert.Contains(ParityAspect.ValueDuplicate, Aspects(report));
    }
}
