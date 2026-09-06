using Ecr.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Нуль, що приховує помилку (<c>H-24d-1</c>, <c>ФВ-9.14</c>).
/// </summary>
/// <remarks>
/// ⛔ Чинна система перетворює <c>NaN</c> і <c>±∞</c> на нуль **мовчки**
/// (<c>Utilities.cs:56-71</c>, <c>:102-105</c>): ані журналу, ані ознаки.
/// Директива каже дослівно: «Число не змінюється — змінюється тиша».
///
/// ⚠ Тому перевіряються дві речі одразу, і жодна не зайва: що число в
/// <c>Legacy</c> **те саме**, і що причина **записана**. Тест лише на число
/// пройшов би й на мовчазному маскуванні, тобто не стеріг би нічого.
/// </remarks>
public sealed class MaskedZeroTests
{
    private static ExpressionValue Inf => ExpressionValue.LegacyNumber(double.PositiveInfinity);

    private static ExpressionValue Nan => ExpressionValue.LegacyNumber(double.NaN);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public void Legacy_дає_нуль_і_називає_причину()
    {
        // ⛔ Головне твердження. Нуль — те саме число, що дала б чинна
        // система: інакше звірка розійшлася б на кожному такому рядку, і
        // розбіжність довелося б пояснювати як наш дефект, хоча ми лише
        // перестали мовчати.
        var masked = MaskedZero.Prepare(Inf, NumericMode.Legacy);

        Assert.Equal(0m, masked.Value);
        Assert.Equal(MaskedZeroReason.Infinity, masked.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public void Strict_не_дає_нуля_взагалі()
    {
        // ⛔ `null`, а НЕ нуль. Нуль тут гірший за відсутність: він виглядає
        // як виміряне значення, і побачити різницю можна лише в трейсі, куди
        // на цьому режимі ніхто не дивиться.
        var masked = MaskedZero.Prepare(Inf, NumericMode.Strict);

        Assert.Null(masked.Value);
        Assert.Equal(MaskedZeroReason.Infinity, masked.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public void Невизначеність_і_нескінченність_розрізняються()
    {
        // ⚠ Дві причини різні за природою: `∞` — «завелике», `NaN` —
        // «невизначене». У звіті вони мають стояти окремими рядками, бо й
        // лікуються по-різному: перше — межа даних, друге — сама формула.
        Assert.Equal(MaskedZeroReason.NotANumber, MaskedZero.ReasonFor(Nan));
        Assert.Equal(MaskedZeroReason.Infinity, MaskedZero.ReasonFor(Inf));
        Assert.Equal(
            MaskedZeroReason.Infinity,
            MaskedZero.ReasonFor(ExpressionValue.LegacyNumber(double.NegativeInfinity)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Звичайне_число_не_маскується()
    {
        var masked = MaskedZero.Prepare(ExpressionValue.LegacyNumber(42.5d), NumericMode.Legacy);

        Assert.Equal(42.5m, masked.Value);
        Assert.Equal(MaskedZeroReason.None, masked.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Справжній_нуль_не_плутається_із_замаскованим()
    {
        // ⛔ Найважливіше розрізнення всього кроку: у результаті обидва —
        // нуль, і відрізнити їх можна ЛИШЕ за причиною. Якби `Prepare`
        // повертав причину для справжнього нуля, звіт міграції наповнився б
        // рядками, у яких нічого не сталося, і його перестали б читати.
        var real = MaskedZero.Prepare(ExpressionValue.LegacyNumber(0d), NumericMode.Legacy);
        var fake = MaskedZero.Prepare(Nan, NumericMode.Legacy);

        Assert.Equal(0m, real.Value);
        Assert.Equal(0m, fake.Value);

        Assert.Equal(MaskedZeroReason.None, real.Reason);
        Assert.Equal(MaskedZeroReason.NotANumber, fake.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Текст_не_є_маскуванням()
    {
        // ⚠ Не число і не маска. Причина такого виходу вже в трейсі окремим
        // кроком, і позначити його як замаскований нуль означало б збрехати
        // про природу відмови.
        var masked = MaskedZero.Prepare(ExpressionValue.Text("Сверхнорматив"), NumericMode.Legacy);

        Assert.Null(masked.Value);
        Assert.Equal(MaskedZeroReason.None, masked.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.13")]
    public void Причина_пишеться_навіть_на_рівні_лише_помилок()
    {
        // ⛔ `ErrorsOnly` — типовий рівень трейсу, і кроки зі значеннями на
        // ньому не пишуться. Замаскований нуль — виняток, і в ньому весь
        // зміст `H-24d-1`: запис, якого не видно на типовому рівні, — це
        // рівно та тиша, яку ми прибираємо.
        var recorder = new TraceRecorder(TraceLevel.ErrorsOnly);
        recorder.Step("Normal", "1+1", 2m);
        recorder.Masked("Total", null, 0m, MaskedZeroReason.Infinity);

        var step = Assert.Single(recorder.Steps);

        Assert.Equal("Total", step.Code);
        Assert.Equal(MaskedZeroReason.Infinity, step.Masked);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вимкнений_трейс_не_пише_нічого_і_тут()
    {
        // ⚠ `Off` не пише навіть маскування: рівень обирають свідомо, і
        // виняток із нього був би прихованою вартістю там, де людина
        // просила тиші.
        var recorder = new TraceRecorder(TraceLevel.Off);
        recorder.Masked("Total", null, 0m, MaskedZeroReason.NotANumber);

        Assert.Empty(recorder.Steps);
    }
}
