using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Z4-02 / S1-06: форма SID, який адміністратор набирає руками, — лише ASCII-цифри, обмежена довжина, жодного
/// кінцевого LF; усе інше — 422 <c>windowsSidMalformed</c>, а не 500 далі.
/// </summary>
/// <remarks>
/// ⛔ До виправлення: <c>\d</c> у .NET збігається з цифрами Unicode («٣»), довжина цифр не обмежувалась, а <c>$</c>
/// пропускав кінцевий LF. Мутація: повернути <c>\d+</c> / <c>$</c> / прибрати межі <c>{1,15}</c>, <c>{1,10}</c> —
/// відповідні випадки червоніють.
/// </remarks>
public sealed class WindowsSidFormatTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-512")]
    [InlineData("s-1-5-32-544")]
    [InlineData("  S-1-5-21-1  ")]
    [InlineData("S-1-5-18")]
    public void Справжній_SID_канонізується(string input)
    {
        var sid = WindowsSidFormat.Canonicalize(input);

        Assert.Equal(input.Trim().ToUpperInvariant(), sid);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("S-1-5-21-٣")]                                  // арабсько-індійська цифра
    [InlineData("S-1-٥-21-1")]
    [InlineData("S-1-5-21-1\nS-1-5-21-2")]                           // два SID через LF
    [InlineData("S-1-5")]                                            // немає підоргану
    [InlineData("S-1-5-21-12345678901")]                             // підорган понад 32 біти (11 цифр)
    [InlineData("S-1-1234567890123456-21")]                          // орган понад 15 цифр
    [InlineData("S-1-5-1-2-3-4-5-6-7-8-9-10-11-12-13-14-15")]        // 15 підорганів: більше за 14 після органу
    public void Не_SID_дає_422_а_не_500(string input)
    {
        var error = Assert.Throws<BusinessRuleException>(() => WindowsSidFormat.Canonicalize(input));

        Assert.Equal("ECR-USR-0422", error.ErrorCode);
        Assert.Equal("err.ECR-USR-0422.windowsSidMalformed", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Дуже_довгий_рядок_відхиляється_за_довжиною_а_не_розбирається()
    {
        var huge = "S-1-5-21-" + new string('9', 100_000);

        var error = Assert.Throws<BusinessRuleException>(() => WindowsSidFormat.Canonicalize(huge));

        Assert.Equal("err.ECR-USR-0422.windowsSidMalformed", error.Details!["messageKey"]);
    }

    /// <summary>Кінцевий LF — не SID: <c>Canonicalize</c> обрізає краї, але сама форма (її читає й призначення ролі на групу) LF не терпить.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("S-1-5-21-1\n")]
    [InlineData("S-1-5-21-1\r\n")]
    public void Форма_SID_не_терпить_кінцевого_переведення_рядка(string text)
    {
        Assert.False(WindowsSidFormat.IsWellFormed(text));
        Assert.True(WindowsSidFormat.IsWellFormed(text.TrimEnd()));
    }
}
