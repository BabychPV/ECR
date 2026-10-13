// tests/Ecr.Architecture.Tests/LandSubstanceApplicabilityRuleWrapperTests.cs
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Обгортка правила категорії з моделі Land (<c>substanceApplicability.ruleTemplate</c>) на справжніх правилах
/// <c>af-land-category-rules.json</c> і на довіднику, зібраному з записів моделі: розбирається діалектом методологій,
/// у Row-фазі (без речовини) дає той самий ключ, що й правило без обгортки, а для речовини поза записами - порожній.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у <c>substance_applicability.py</c> змінити порядок частин ключа й
/// перегенерувати модель - червоніє <see cref="Обгортка_кожної_методології_розбирається_і_не_змінює_ключ_рядка"/>.
/// </remarks>
public sealed class LandSubstanceApplicabilityRuleWrapperTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static readonly string[] KeyFields = ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Обгортка_кожної_методології_розбирається_і_не_змінює_ключ_рядка()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));
        using var rulesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "af-land-category-rules.json")));
        var baseRules = rulesDoc.RootElement.GetProperty("rules").EnumerateArray()
            .ToDictionary(r => r.GetProperty("methodology").GetString()!, r => r.GetProperty("expression").GetString()!);

        var description = model.RootElement.GetProperty("substanceApplicability");
        var registry = description.GetProperty("registry").GetString()!;
        var template = description.GetProperty("ruleTemplate").GetString()!;
        var snapshot = Snapshot(model, registry);

        var checkedRules = 0;
        foreach (var item in description.GetProperty("methodologies").EnumerateObject())
        {
            var methodology = item.Name;
            var kind = item.Value.GetProperty("kind").GetString()!;
            var valueExpression = item.Value.GetProperty("valueExpression").GetString()!;
            var inner = baseRules.GetValueOrDefault(methodology, "@Land_NAE");

            var wrapped = template
                .Replace("{methodology}", methodology, StringComparison.Ordinal)
                .Replace("{kind}", kind, StringComparison.Ordinal)
                .Replace("{valueExpression}", valueExpression, StringComparison.Ordinal)
                .Replace("{rule}", inner, StringComparison.Ordinal);

            Assert.True(
                Expr.Parse(wrapped, ExpressionDialect.Methodology).IsSuccess,
                $"{methodology}: обгортка не розбирається: {wrapped}");

            // Селектор з перших записів довідника; C12_03 читає REGFIELD(@Land_TypeFuel, 'NAME') - Lookup, у цьому
            // стенді немає записів, тому лише розбір (правило C12_03 - @IDCodeNAEProject).
            if (valueExpression.StartsWith("REGFIELD", StringComparison.Ordinal) || !baseRules.ContainsKey(methodology))
            {
                continue;
            }

            var (selectorArgument, selector, code) = Sample(model, methodology, item.Value);
            var arguments = new Dictionary<string, ExpressionValue>
            {
                ["Land_NAE"] = ExpressionValue.Text("6640"),
            };
            arguments[selectorArgument] = ExpressionValue.Text(selector);   // для NAE-методологій селектор і є Land_NAE

            string Key(string expression, string? substance)
            {
                var inner = new TestEvaluationContext { CurrentSubstanceCode = substance };
                foreach (var (name, value) in arguments)
                {
                    inner.Arguments[name] = value;
                }

                var value2 = Expr.Eval(expression, snapshot.Attach(inner), ExpressionDialect.Methodology);
                Assert.False(value2.IsError, $"{methodology}: {expression} -> {value2.ErrorCode}");
                return (string)value2.Value!;
            }

            var expected = Key(inner, null);
            Assert.NotEqual(string.Empty, expected);

            // Row-фаза (без речовини) і застосовна речовина - той самий ключ; чужа речовина - порожній ключ.
            Assert.Equal(expected, Key(wrapped, null));
            Assert.Equal(expected, Key(wrapped, code));
            Assert.Equal(string.Empty, Key(wrapped, "9999"));
            checkedRules++;
        }

        // 9 з 10 обчислюються: правило C12_03 тепер є у файлі (читає @IDCodeNAEProject і Lookup REGFIELD) - лише розбір.
        Assert.True(checkedRules >= 8, $"перевірено правил: {checkedRules}");
    }

    private static (string Argument, string Selector, string Code) Sample(JsonDocument model, string methodology, JsonElement description)
    {
        var registry = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_SUBSTANCE_APPLICABILITY");
        var argument = description.GetProperty("argument").GetString()!;
        foreach (var entry in registry.GetProperty("entries").EnumerateArray())
        {
            var f = entry.GetProperty("fields");
            if (f.GetProperty("METHODOLOGY").GetString() != methodology || f.GetProperty("SELECTOR_KIND").GetString() == "*")
            {
                continue;
            }

            var value = f.GetProperty("SELECTOR_VALUE").GetString()!;

            // Токен diesel|oil будується правилом з тексту Land_TypeStoredMaterial: беремо текст, що дає токен.
            if (description.GetProperty("selector").GetString() == "contains")
            {
                return (argument, value == "diesel" ? "Diesel - Дизель" : "Lube oil", f.GetProperty("SUBSTANCE_CODE").GetString()!);
            }

            return (argument, value, f.GetProperty("SUBSTANCE_CODE").GetString()!);
        }

        throw new InvalidOperationException($"{methodology}: у довіднику немає записів");
    }

    private static InMemoryRegistrySnapshot Snapshot(JsonDocument model, string registry)
    {
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry(registry, KeyFields, primaryKey: KeyFields);
        var source = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == registry);

        long id = 0;
        foreach (var entry in source.GetProperty("entries").EnumerateArray())
        {
            var fields = new Dictionary<string, ExpressionValue>();
            foreach (var name in KeyFields)
            {
                fields[name] = ExpressionValue.Text(entry.GetProperty("fields").GetProperty(name).GetString()!);
            }

            snapshot.AddEntry(registry, ++id, entry.GetProperty("code").GetString()!, fields, ordinal: (int)id);
        }

        return snapshot;
    }
}
