using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Application.Validation;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит L7-07: ключі зіставлення Rollup/Check — канонічні за типом, а не
/// рядком, у якому випадково опинилось значення.
/// </summary>
/// <remarks>
/// ⛔ Що було. Збережене число читається з <c>decimal(34,16)</c> як
/// <c>5.0000000000000000</c>, а ще не записаний результат формули цього прогону —
/// як <c>5</c>: той самий ключ не збігався, агрегат приймача був порожнім (Rollup
/// записував «очищення»). Колонки-ключі типу Date/Bool у ключ не потрапляли
/// взагалі — рядок джерела мовчки пропускався.
/// </remarks>
public sealed class RelationKeyCanonicalTests
{
    private static readonly RelationMatchSpec ByK = new([new RelationKey("K", "K")]);

    private static RelationRow NumKey(string row, decimal key, decimal? fact)
        => new(row, new Dictionary<string, decimal?> { ["K"] = key, ["Fact"] = fact }, new Dictionary<string, string?>());

    [Fact]
    public void Rollup_ключ_збереженого_й_порахованого_числа_збігається()
    {
        var stored = NumKey("s1", 5.0000000000000000m, 7m);
        var pending = NumKey("t1", 5m, null);

        var write = Assert.Single(RollupEvaluator.Evaluate(
            ByK, new RollupSpec("Fact", "Total", RollupAggregate.Sum), [stored], [pending], null).Writes);

        Assert.Equal(7m, write.Value);
    }

    [Theory]
    [InlineData("5.0000000000000000", "5")]
    [InlineData("0.5000", "0.5")]
    [InlineData("-12.30", "-12.3")]
    [InlineData("0.0000000000000000", "0")]
    public void Числовий_ключ_без_хвостових_нулів(string value, string expected)
        => Assert.Equal(expected, NumKey("r", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), null).KeyOf("K"));

    [Fact]
    public void Check_ключ_різного_масштабу_зіставляє_пару()
    {
        var spec = new CheckSpec("Fact", "Fact", 0m, CheckToleranceKind.Abs, CheckSeverity.Warn);

        var failures = CheckEvaluator.Failures(ByK, spec, [NumKey("s", 5.00m, 1m)], [NumKey("t", 5m, 2m)]);

        Assert.Single(failures);
    }

    [Fact]
    public void Ключі_типу_Date_і_Bool_потрапляють_у_рядки_зв_язку()
    {
        var b = new TemplateBuilder { TemplateVersionId = 7 };
        var table = b.Table(b.Sheet("S"), "T");
        var day = b.Column(table, "Day", CellDataType.Date);
        var flag = b.Column(table, "Flag", CellDataType.Bool);
        var period = PeriodKey.Parse(202601);

        CellRecord Cell(int column, CellValueData value) => new(new CellAddress(period, 11, column), table.Id, value);

        var rows = RelationCheckRunner.BuildRows(
            b.Build(),
            [
                Cell(day.Id, new CellValueData { ValueDate = new DateTime(2026, 1, 15) }),
                Cell(flag.Id, new CellValueData { ValueBool = true }),
            ],
            new Dictionary<string, long> { ["R1"] = 11 });

        var row = Assert.Single(rows);
        Assert.Equal("2026-01-15", row.KeyOf("Day"));
        Assert.Equal("TRUE", row.KeyOf("Flag"));
    }

    [Theory]
    [InlineData(CellDataType.String, CellDataType.Int)]
    [InlineData(CellDataType.Date, CellDataType.String)]
    [InlineData(CellDataType.Bool, CellDataType.Decimal)]
    [InlineData(CellDataType.Lookup, CellDataType.Int)]
    public void Несумісні_типи_ключів_відхиляються_при_збереженні(CellDataType source, CellDataType target)
    {
        var failure = RelationSpecValidator.Validate(
            TableRelationKind.Rollup,
            """{"keys":[{"source":"K","target":"K"}]}""",
            """{"sourceColumn":"Fact","targetColumn":"Total","aggregate":"sum"}""",
            new Dictionary<string, CellDataType> { ["K"] = source, ["Fact"] = CellDataType.Decimal },
            new Dictionary<string, CellDataType> { ["K"] = target, ["Total"] = CellDataType.Formula });

        Assert.Equal("keyTypeMismatch", failure?.Reason);
    }

    [Theory]
    [InlineData(CellDataType.Int, CellDataType.Decimal)]
    [InlineData(CellDataType.Formula, CellDataType.Int)]
    [InlineData(CellDataType.String, CellDataType.String)]
    public void Сумісні_типи_ключів_проходять(CellDataType source, CellDataType target)
    {
        var failure = RelationSpecValidator.Validate(
            TableRelationKind.Rollup,
            """{"keys":[{"source":"K","target":"K"}]}""",
            """{"sourceColumn":"Fact","targetColumn":"Total","aggregate":"sum"}""",
            new Dictionary<string, CellDataType> { ["K"] = source, ["Fact"] = CellDataType.Decimal },
            new Dictionary<string, CellDataType> { ["K"] = target, ["Total"] = CellDataType.Formula });

        Assert.Null(failure);
    }
}
