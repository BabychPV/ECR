// tests/Ecr.Calculations.Tests/TraceJsonTests.cs
using System.Text.Json;
using Ecr.Domain.Enums;
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
