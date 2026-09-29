// tests/Ecr.Calculations.Tests/TraceJsonTests.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Трейс кроку в схемі <c>TraceJson</c> v1 зі входами, які прочитала формула (HSE301 A3b,
/// ФВ-9.13, дефект Д-5; FEATURE-HSE301-VIEW §7.2, рішення V-8).
/// </summary>
/// <remarks>
/// ⛔ До кроку в <c>TraceJson</c> лягав лише код помилки, а успішний крок не мав жодної
/// деталізації: на питання «з яких чисел вийшло це число» трейс відповісти не міг.
/// </remarks>
public sealed class TraceJsonTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.13")]
    public async Task M_t_прикладу_A_має_входи_V_Sm3_і_Rho20()
    {
        // Типовий рівень — ErrorsOnly: видима формула пишеться й на ньому (§7.1).
        var output = await new ScopeStand().RunAsync(TraceLevel.ErrorsOnly);

        var step = Assert.Single(output.Trace, s => s.StepCode == "M_t");
        using var json = JsonDocument.Parse(step.Detail!);
        var root = json.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("CONVERT(!V_Sm3 * @Rho20, 'kg', 't')", root.GetProperty("expr").GetString());
        Assert.Equal("0.2581914962", root.GetProperty("result").GetString());
        Assert.Equal("t", root.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);

        var inputs = root.GetProperty("inputs").EnumerateArray().ToList();
        Assert.Equal(["V_Sm3", "Rho20"], inputs.Select(i => i.GetProperty("code").GetString()));

        var volume = inputs[0];
        Assert.Equal("formula", volume.GetProperty("kind").GetString());
        Assert.Equal("269.258", volume.GetProperty("value").GetString());
        Assert.Equal("Sm3", volume.GetProperty("unit").GetString());

        var density = inputs[1];
        Assert.Equal("arg", density.GetProperty("kind").GetString());
        Assert.Equal("0.9589", density.GetProperty("value").GetString());
        var cell = density.GetProperty("cell");
        Assert.Equal(500, cell.GetProperty("tableInstanceId").GetInt64());
        Assert.Equal("E-2026-01-001", cell.GetProperty("row").GetString());
        Assert.Equal("Rho20", cell.GetProperty("column").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Константа_речовини_у_входах_зі_своєю_речовиною()
    {
        var output = await new ScopeStand().RunAsync(TraceLevel.Full);

        var step = Assert.Single(output.Trace, s => s.StepCode == "tons" && s.Value is not null && Substance(s) == 905);
        using var json = JsonDocument.Parse(step.Detail!);

        var inputs = json.RootElement.GetProperty("inputs").EnumerateArray().ToList();
        Assert.Equal(["M_t", "K_MASS", "W_COMP"], inputs.Select(i => i.GetProperty("code").GetString()));

        var constant = inputs[1];
        Assert.Equal("const", constant.GetProperty("kind").GetString());
        Assert.Equal("0.005", constant.GetProperty("value").GetString());
        Assert.Equal(905, constant.GetProperty("substance").GetInt64());
    }

    /// <remarks>
    /// ⚠ Невдалий крок пояснює, ЯКИЙ вхід зламався: константа, якої для Row-формули не
    /// існує, лежить у входах зі своїм <c>#REF</c>. А <c>TraceJson</c> порту лишається
    /// голим кодом — так його читає симуляція.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Невдалий_крок_має_помилку_і_вхід_що_її_дав()
    {
        var output = await new ScopeStand(massExpression: "!V_Sm3 * CST.K_MASS").RunAsync(TraceLevel.ErrorsOnly);

        // Крок формули (з виразом), а не запис «вихід не порахувався» того самого коду.
        var step = Assert.Single(
            output.Trace,
            s => s.StepCode == "M_t" && s.Expression is not null && s.TraceJson == ExpressionErrors.BadReference);
        using var json = JsonDocument.Parse(step.Detail!);
        var root = json.RootElement;

        Assert.Equal(ExpressionErrors.BadReference, root.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").ValueKind);

        var constant = Assert.Single(
            root.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("kind").GetString() == "const");
        Assert.Equal(ExpressionErrors.BadReference, constant.GetProperty("value").GetString());
    }

    /// <remarks>
    /// ⛔ Невидима формула на <c>ErrorsOnly</c> не пише кроку — обсяг наявних методологій
    /// не росте (ЗБР-3). На <c>Full</c> — пише, і теж зі входами.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Невидимі_кроки_лише_на_Full()
    {
        var hidden = await new ScopeStand(visible: false).RunAsync(TraceLevel.ErrorsOnly);
        var full = await new ScopeStand(visible: false).RunAsync(TraceLevel.Full);

        Assert.Empty(hidden.Trace);

        var step = Assert.Single(full.Trace, s => s.StepCode == "M_t");
        using var json = JsonDocument.Parse(step.Detail!);
        Assert.Equal(2, json.RootElement.GetProperty("inputs").GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Off_не_пише_жодного_кроку_навіть_видимого()
    {
        var output = await new ScopeStand().RunAsync(TraceLevel.Off);

        Assert.Empty(output.Trace);
    }

    private static long? Substance(CalculationTraceStep step)
    {
        using var json = JsonDocument.Parse(step.Detail!);
        var constant = json.RootElement.GetProperty("inputs").EnumerateArray()
            .FirstOrDefault(i => i.GetProperty("kind").GetString() == "const");

        return constant.ValueKind == JsonValueKind.Object && constant.TryGetProperty("substance", out var id)
            ? id.GetInt64()
            : null;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Збирач_бере_посилання_в_порядку_тексту_без_повторів()
    {
        var parsed = new Parser().Parse(
            "if(@A > 0, [Period].Hours * @A + !F, CST.K) + !F + [Period].Days + [Period].Hours",
            ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var references = ReferenceCollector.Collect(parsed.Expression!.Root);

        Assert.Equal(
            [
                (TraceInputKind.Argument, "A"),
                (TraceInputKind.Period, "Hours"),
                (TraceInputKind.Formula, "F"),
                (TraceInputKind.Constant, "K"),
                (TraceInputKind.Period, "Days"),
            ],
            references.Select(r => (r.Kind, r.Code)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крок_зі_входами_пишеться_схемою_v1()
    {
        var recorder = new TraceRecorder(TraceLevel.Full);
        recorder.Step(
            "M_t",
            "CONVERT(!V_Sm3 * @Rho20, 'kg', 't')",
            0.2581914962m,
            detail: new TraceDetail(
                "t",
                [
                    new TraceInput(TraceInputKind.Formula, "V_Sm3", "269.258", "Sm3"),
                    new TraceInput(
                        TraceInputKind.Argument, "Rho20", "0.9589", "kg_per_Sm3",
                        new TraceCell(500, "E-2026-01-001", "Rho20")),
                    new TraceInput(TraceInputKind.Constant, "K_MASS", "0.0024", "t_per_t", SubstanceEntryId: 905),
                    new TraceInput(TraceInputKind.Period, "Hours", "744", null),
                ]));

        var raw = Assert.Single(recorder.Steps).ToJson();
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;

        // Апостроф у виразі лишається апострофом: вираз у БД читається очима.
        Assert.Contains("'kg'", raw, StringComparison.Ordinal);
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("0.2581914962", root.GetProperty("result").GetString());
        Assert.Equal("t", root.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("masked").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);

        var inputs = root.GetProperty("inputs").EnumerateArray().ToList();
        Assert.Equal(["formula", "arg", "const", "period"], inputs.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal("E-2026-01-001", inputs[1].GetProperty("cell").GetProperty("row").GetString());
        Assert.Equal(905, inputs[2].GetProperty("substance").GetInt64());
        Assert.Equal("Hours", inputs[3].GetProperty("prop").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Маскований_крок_без_виразу_має_причину_і_порожні_входи()
    {
        var recorder = new TraceRecorder(TraceLevel.ErrorsOnly);
        recorder.Masked("tons", null, 0m, MaskedZeroReason.NotANumber);

        using var json = JsonDocument.Parse(Assert.Single(recorder.Steps).ToJson());
        var root = json.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(nameof(MaskedZeroReason.NotANumber), root.GetProperty("masked").GetString());
        Assert.Equal("0", root.GetProperty("result").GetString());
        Assert.Equal(0, root.GetProperty("inputs").GetArrayLength());
    }

    /// <remarks>
    /// ⛔ Видимий крок пишеться на будь-якому рівні, крім <c>Off</c>; невидимий — лише на
    /// <c>Full</c> (§7.1): обсяг наявних методологій на типовому <c>ErrorsOnly</c> не росте.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(TraceLevel.Off, false, false)]
    [InlineData(TraceLevel.Off, true, false)]
    [InlineData(TraceLevel.ErrorsOnly, false, false)]
    [InlineData(TraceLevel.ErrorsOnly, true, true)]
    [InlineData(TraceLevel.Full, false, true)]
    public void Видимий_крок_пишеться_на_будь_якому_рівні_крім_Off(TraceLevel level, bool visible, bool written)
    {
        var recorder = new TraceRecorder(level);
        recorder.Step("M_t", "!V", 1m, visible);

        Assert.Equal(written, recorder.Steps.Count == 1);
        Assert.Equal(written, recorder.Records(visible));
    }
}
