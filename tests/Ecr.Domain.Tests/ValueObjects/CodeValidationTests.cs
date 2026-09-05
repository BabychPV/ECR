using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Недопустимий код — це помилка **введення**, а не збій сервера.
/// </summary>
/// <remarks>
/// ⛔ Знайдено при написанні тесту клонування проєкту: код «KASH-2026»
/// відхилявся <c>ArgumentException</c>. Конвеєр обробки помилок такого типу не
/// знає, тому відповідав <c>500</c> із «Внутрішня помилка, зверніться до
/// адміністратора» — а код є ПЕРШИМ полем кожної форми створення: проєкту,
/// шаблону, ролі, запису довідника. Природна форма з дефісом виглядала як
/// аварія системи.
/// </remarks>
public sealed class CodeValidationTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("KASH-2027")]
    [InlineData("2027KASH")]
    [InlineData("")]
    [InlineData("код")]
    [InlineData("KASH 2027")]
    public void Недопустимий_код_дає_доменну_відмову_а_не_збій(string value)
    {
        var ex = Assert.Throws<DomainException>(() => EcrCode.Create(value));

        // ⛔ Саме `DomainException`: конвеєр мапить її у 422 з кодом і текстом.
        // `ArgumentException` провалюється у гілку «невідомий виняток», а там
        // текст замінюється на «Внутрішня помилка» — тобто підказка, яка веде
        // до виправлення, губиться цілком.
        Assert.Equal("ECR-CFG-0422", ex.ErrorCode);

        // ⚠ У повідомленні — ПРАВИЛО, а не сам регулярний вираз: користувач
        // має зрозуміти, що виправити, не читаючи `^[A-Za-z][A-Za-z0-9_]{0,63}$`.
        Assert.Contains("літер", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("KASH_2027")]
    [InlineData("Water")]
    [InlineData("T1")]
    public void Допустимий_код_приймається(string value)
    {
        Assert.Equal(value, EcrCode.Create(value).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Недопустимий_ключ_рядка_теж_дає_доменну_відмову()
    {
        // ⚠ Той самий дефект був і тут, і наслідок гірший: ключ рядка приходить
        // із запиту на створення рядка динамічної таблиці, тобто з дії, яку
        // оператор робить щодня.
        var ex = Assert.Throws<DomainException>(() => RowKey.Create("7001 001"));

        Assert.Equal("ECR-CFG-0422", ex.ErrorCode);
    }
}
