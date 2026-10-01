using System.Globalization;
using System.Text.Json;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Паритет умовного форматування клієнт ↔ сервер (ФВ-2.7) на спільній фікстурі
/// <c>conditional-format-parity.json</c>; той самий файл читає клієнтський
/// <c>conditionalFormatParity.test.ts</c>.
/// </summary>
/// <remarks>
/// Значення йде тим самим шляхом, що в зрізі сітки (<c>GetTableSliceHandler</c>) і в
/// Excel-експорті: <see cref="CellValueData"/> → <see cref="CellValueMapping.ToRuleValue"/>
/// → <see cref="ConditionalFormatEvaluator.Evaluate"/>.
///
/// ⛔ Мутаційний доказ (2026-10-01, окремий detached-worktree): <c>"ne" =&gt; true</c> для
/// нечислового значення — червоніють вектори «ne: текст …»; прибрати
/// <c>Math.Min/Max</c> у <c>between</c> — червоніє «межі навпаки»; <c>isEmpty</c> без
/// <c>IsNullOrWhiteSpace</c> — червоніють «пробіли»; повернути <c>int or long</c> у
/// <c>TryNumber</c> — червоніють «елемент довідника» й «одиниця».
/// </remarks>
public sealed class ConditionalFormatParityTests
{
    private static readonly JsonElement Fixture = Load();

    private static readonly string[] AllOperators =
        ["between", "empty", "eq", "ge", "gt", "le", "lt", "ne", "notEmpty"];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var testCase in Fixture.GetProperty("cases").EnumerateArray())
        {
            data.Add(testCase.GetProperty("id").GetString()!);
        }

        return data;
    }

    [Fact]
    public void Fixture_covers_every_operator()
    {
        var operators = Fixture.GetProperty("cases").EnumerateArray()
            .Select(c => c.GetProperty("operator").GetString())
            .ToHashSet();

        Assert.Equal(
            AllOperators,
            operators.Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Server_matches_shared_vector(string id)
    {
        var testCase = Fixture.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == id);

        var rule = new ConditionalFormatRule(
            1,
            "A",
            1,
            testCase.GetProperty("operator").GetString()!,
            Text(testCase, "value"),
            Text(testCase, "valueTo"),
            "#ff0000",
            null,
            false);

        var value = CellValueMapping.ToRuleValue(Cell(testCase.GetProperty("cell")));

        Assert.Equal(
            testCase.GetProperty("matches").GetBoolean(),
            ConditionalFormatEvaluator.Evaluate([rule], value) is not null);
    }

    private static string? Text(JsonElement testCase, string name)
        => testCase.GetProperty(name).ValueKind == JsonValueKind.Null ? null : testCase.GetProperty(name).GetString();

    private static CellValueData? Cell(JsonElement cell)
    {
        if (cell.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var kind = cell[0].GetString();
        var text = cell[1].ValueKind == JsonValueKind.Null ? null : cell[1].GetString();

        return kind switch
        {
            "Numeric" => new CellValueData { ValueNumeric = decimal.Parse(text!, NumberStyles.Number, CultureInfo.InvariantCulture) },
            "String" => new CellValueData { ValueString = text },
            "Bool" => new CellValueData { ValueBool = bool.Parse(text!) },
            "Date" => new CellValueData { ValueDate = DateTime.Parse(text!, CultureInfo.InvariantCulture) },
            "RegistryEntry" => new CellValueData { ValueRegistryEntryId = long.Parse(text!, CultureInfo.InvariantCulture) },
            "Unit" => new CellValueData { ValueUnitId = int.Parse(text!, CultureInfo.InvariantCulture) },
            "Empty" => CellValueData.Empty,
            _ => throw new InvalidOperationException($"Невідомий тип комірки у фікстурі: {kind}."),
        };
    }

    private static JsonElement Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestFixtures.Path("conditional-format-parity.json")));
        return document.RootElement.Clone();
    }
}
