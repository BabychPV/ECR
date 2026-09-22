// tests/Ecr.Application.Tests/Reporting/ReportParametersTests.cs
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Reporting;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// R6 (<c>D-52a</c>, <c>02b</c> §8a): параметри звіту — оголошення у версії
/// опису і значення, з якими будується зріз.
/// </summary>
public sealed class ReportParametersTests
{
    private static readonly ReportColumnCommand[] Columns = [new("OutputCode", "text"), new("Value", "number")];

    /// <summary>Ті самі налаштування, якими тіло запиту розбирає ASP.NET Core.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Параметри_пишуться_схемою_2_навіть_без_жодного_правила()
    {
        Assert.Equal(
            """{"rowSource":"CalculationResults","schema":2,"parameters":["""
            + """{"code":"Threshold","type":"Number","required":true,"default":0}]}""",
            ReportDefinitionSpec.RulesJson(
                new("CalculationResults", Parameters: [new("Threshold", "Number", Required: true, Default: 0m)]),
                Columns));

        // ⛔ Схема 1 параметрів не читає — прийняти їх означало б мовчки
        // проігнорувати те, на що посилаються вирази.
        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", 1, Parameters: [new("Threshold", "Number")]), Columns));

        Assert.Equal("schema", error.Details!["part"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("Threshold", "Money")] // тип невідомий
    [InlineData("1Threshold", "Number")] // ім'я не читається лексером як @Name
    [InlineData("", "Number")] // імені немає взагалі
    public void Зламане_оголошення_параметра_відмовляє_при_створенні_версії(string code, string type)
    {
        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Parameters: [new(code, type)]), Columns));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.parameter", error.Details!["messageKey"]);
        Assert.Equal(code, error.Details["code"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Два_параметри_з_одним_іменем_відмовляють_бо_імена_не_розрізняють_регістру()
    {
        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Parameters: [new("Threshold", "Number"), new("threshold", "Text")]),
            Columns));

        Assert.Equal("err.ECR-RPT-0422.parameter", error.Details!["messageKey"]);
        Assert.Equal("threshold", error.Details["code"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Замовчування_не_того_типу_відмовляє_при_створенні_версії()
    {
        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Parameters: [new("Threshold", "Number", Default: "багато")]), Columns));

        Assert.Equal("err.ECR-RPT-0422.parameterType", error.Details!["messageKey"]);
        Assert.Equal("default", error.Details["part"]);
        Assert.Equal("Number", error.Details["expectedType"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вираз_посилається_лише_на_оголошений_параметр()
    {
        // Оголошений — версія складається.
        Assert.Contains(
            "@Threshold",
            ReportDefinitionSpec.RulesJson(
                new(
                    "CalculationResults",
                    Rules: [new("[Value] > @Threshold", new(HideRow: true))],
                    Parameters: [new("Threshold", "Number")]),
                Columns),
            StringComparison.Ordinal);

        // Неоголошений — та сама відмова прив'язки, що й до R6.
        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new(
                "CalculationResults",
                Rules: [new("[Value] > @Limit", new(HideRow: true))],
                Parameters: [new("Threshold", "Number")]),
            Columns));

        Assert.Equal("err.ECR-RPT-0422.rule", error.Details!["messageKey"]);
        Assert.Equal("when", error.Details["part"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Тип_параметра_перевіряється_у_виразі_а_не_лише_при_підстановці()
    {
        // `@Mode` — текст, тож `[Value] > @Mode` порівнює число з текстом.
        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new(
                "CalculationResults",
                Rules: [new("[Value] > @Mode", new(HideRow: true))],
                Parameters: [new("Mode", "Text")]),
            Columns));

        Assert.Equal("when", error.Details!["part"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Значення_параметра_змінює_результат_правила()
    {
        var rules = ReportRowRules.Compile(
            2,
            "CalculationResults",
            [new("[Value] > @Threshold", new(Set: new("Value", "@Threshold")))],
            ["OutputCode", "Value"],
            [new("Threshold", "Number", Default: 5m)]);

        // Межа 5 — рядок зрізається до неї.
        var trimmed = Row(9m);
        Assert.True(rules.Apply(trimmed, Values(("Threshold", 5m))));
        Assert.Equal(5m, trimmed["Value"]);

        // Межа 100 — те саме правило на тому самому рядку не спрацьовує.
        var untouched = Row(9m);
        Assert.True(rules.Apply(untouched, Values(("Threshold", 100m))));
        Assert.Equal(9m, untouched["Value"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Обовязковий_параметр_без_значення_і_без_замовчування_відмовляє()
    {
        var required = ReportParameters.Compile([new("Threshold", "Number", Required: true)]);

        var error = Assert.Throws<BusinessRuleException>(() => ReportParameters.Bind(required, values: null));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.parameterRequired", error.Details!["messageKey"]);
        Assert.Equal("Threshold", error.Details["code"]);

        // Із замовчуванням той самий обов'язковий параметр будується мовчки.
        var withDefault = ReportParameters.Compile([new("Threshold", "Number", Required: true, Default: 5m)]);
        Assert.Equal(5m, ReportParameters.Bind(withDefault, values: null).Values["Threshold"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Зайве_імя_параметра_відмовляє_а_не_ігнорується()
    {
        var declared = ReportParameters.Compile([new("Threshold", "Number", Default: 5m)]);

        var error = Assert.Throws<BusinessRuleException>(
            () => ReportParameters.Bind(declared, Values(("Treshold", 5m))));

        Assert.Equal("err.ECR-RPT-0422.parameterUnknown", error.Details!["messageKey"]);
        Assert.Equal("Treshold", error.Details["code"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Значення_не_того_типу_відмовляє_при_побудові()
    {
        var declared = ReportParameters.Compile([new("Threshold", "Number")]);

        var error = Assert.Throws<BusinessRuleException>(
            () => ReportParameters.Bind(declared, Values(("Threshold", "багато"))));

        Assert.Equal("err.ECR-RPT-0422.parameterType", error.Details!["messageKey"]);
        Assert.Equal("value", error.Details["part"]);
        Assert.Equal("Number", error.Details["expectedType"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Значення_з_HTTP_приходить_JsonElement_ом_і_читається_за_оголошеним_типом()
    {
        // ⛔ Урок `A7-01` (`CellValueReader`): через HTTP жодного типу CLR не
        // буває — `System.Text.Json` віддає `JsonElement`. Тест, що конструює
        // словник із готовим `decimal`, цієї межі не переходить узагалі.
        var http = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """{"Threshold":12.5,"Mode":"strict","Flag":true,"Day":"2026-04-01"}""",
            Web)!;

        var declared = ReportParameters.Compile(
        [
            new("Threshold", "Number"), new("Mode", "Text"),
            new("Flag", "Boolean"), new("Day", "Date"),
        ]);

        var bound = ReportParameters.Bind(declared, http);

        Assert.Equal(12.5m, bound.Values["Threshold"]);
        Assert.Equal("strict", bound.Values["Mode"]);
        Assert.True((bool)bound.Values["Flag"]!);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), bound.Values["Day"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Число_рядком_з_HTTP_приймається_без_втрати_знаків()
    {
        // Рядок — канонічний дротовий формат десяткового: JS-число тут уже
        // загубило б знаки після ~15-го.
        var http = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """{"Threshold":"1234567890123.1234567890123456","Limit":"123.4567890123456789"}""", Web)!;

        var bound = ReportParameters.Bind(
            ReportParameters.Compile([new("Threshold", "Number"), new("Limit", "Number")]), http);

        Assert.Equal(1234567890123.1234567890123456m, bound.Values["Threshold"]);
        Assert.Equal(123.4567890123456789m, bound.Values["Limit"]); // рівно 16 знаків — межа, приймається
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("1,5")] // культурна кома — не десятковий роздільник і не тисячі
    [InlineData("5 т")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("123.45678901234567891")] // 17 знаків дробу > 16: відмова, не тихий обріз
    public void Нечисловий_рядок_у_числовому_параметрі_відмовляє(string text)
    {
        var declared = ReportParameters.Compile([new("Threshold", "Number")]);
        var http = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["Threshold"] = text }), Web)!;

        var error = Assert.Throws<BusinessRuleException>(() => ReportParameters.Bind(declared, http));

        Assert.Equal("err.ECR-RPT-0422.parameterType", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Зі_зрізом_зберігаються_ВИКОРИСТАНІ_значення_разом_із_замовчуваннями()
    {
        var declared = ReportParameters.Compile(
            [new("Threshold", "Number", Default: 5m), new("Mode", "Text")]);

        // Ім'я передано іншим регістром — `@Name` регістру не розрізняє (§3.4),
        // а записується воно так, як оголошене.
        var bound = ReportParameters.Bind(declared, Values(("threshold", 12m)));

        Assert.Equal("""{"Threshold":12,"Mode":null}""", bound.Json);

        // Версія без параметрів не пише нічого: порожній об'єкт у зрізі читався
        // б як «параметри були, і всі порожні».
        Assert.Null(ReportParameters.Bind([], values: null).Json);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Оголошення_читаються_зі_збереженої_версії_а_схема_1_їх_не_має()
    {
        var declared = ReportParameters.Of(
            """{"rowSource":"CalculationResults","schema":2,"parameters":[{"code":"Threshold","type":"Number","default":7}]}""");

        var only = Assert.Single(declared);
        Assert.Equal("Threshold", only.Code);
        Assert.Equal(7m, only.Default);

        Assert.Empty(ReportParameters.Of("""{"rowSource":"CalculationResults","schema":1}"""));
        Assert.Empty(ReportParameters.Of(null));
    }

    private static Dictionary<string, object?> Values(params (string Code, object? Value)[] values)
        => values.ToDictionary(v => v.Code, v => v.Value, StringComparer.Ordinal);

    private static Dictionary<string, object?> Row(decimal? value)
        => new(StringComparer.Ordinal) { ["OutputCode"] = "E_CO2", ["Value"] = value, ["UnitCode"] = "t" };
}
