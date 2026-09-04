using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Обмеження на <see cref="EcrCode"/> продиктоване лексером виразів: код
/// вживається всередині <c>[...]</c> без екранування (R-B6).
/// </summary>
public sealed class EcrCodeTests
{
    [Theory]
    [InlineData("Water_07")]
    [InlineData("Main")]
    [InlineData("A")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Допустимий_код_приймається(string code) => Assert.Fail("not implemented");

    [Theory]
    [InlineData("7001001")]      // не може починатися з цифри
    [InlineData("Water 07")]     // пробіл зламає лексер
    [InlineData("Water.07")]     // крапка — роздільник у посиланні
    [InlineData("Water]07")]     // дужка закриє посилання
    [InlineData("")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Недопустимий_код_відхиляється(string code) => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Код_довший_за_64_символи_відхиляється() => Assert.Fail("not implemented");
}
