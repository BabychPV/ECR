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
    public void Допустимий_код_приймається(string code)
    {
        var created = EcrCode.Create(code);

        Assert.Equal(code, created.Value);
        Assert.True(EcrCode.TryCreate(code, out var viaTry));
        Assert.Equal(code, viaTry.Value);
    }

    [Theory]
    [InlineData("7001001")]      // не може починатися з цифри
    [InlineData("Water 07")]     // пробіл зламає лексер
    [InlineData("Water.07")]     // крапка — роздільник у посиланні
    [InlineData("Water]07")]     // дужка закриє посилання
    [InlineData("")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Недопустимий_код_відхиляється(string code)
    {
        // ⚠ Доменний виняток, а не `ArgumentException`: конвеєр мапить
        // перший у 422 з кодом і поясненням, а другий — у 500 «Внутрішня
        // помилка». Код є першим полем кожної форми створення, і аварія
        // сервера замість підказки коштувала б дорого (`A7-46`).
        Assert.Throws<Ecr.Domain.Abstractions.DomainException>(() => EcrCode.Create(code));

        // TryCreate не кидає, але й не створює: мовчазного «майже коду» бути не може.
        Assert.False(EcrCode.TryCreate(code, out var notCreated));
        Assert.Equal(default, notCreated);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Код_довший_за_64_символи_відхиляється()
    {
        var exactly64 = "A" + new string('x', 63);
        var tooLong = "A" + new string('x', 64);

        Assert.Equal(64, exactly64.Length);
        Assert.Equal(exactly64, EcrCode.Create(exactly64).Value);

        Assert.Equal(65, tooLong.Length);
        Assert.Throws<Ecr.Domain.Abstractions.DomainException>(() => EcrCode.Create(tooLong));
    }
}
