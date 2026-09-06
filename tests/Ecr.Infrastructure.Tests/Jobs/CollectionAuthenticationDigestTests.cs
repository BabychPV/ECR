// tests/Ecr.Infrastructure.Tests/Jobs/CollectionAuthenticationDigestTests.cs
using Ecr.Application.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Відмова джерела в автентифікації має у зведенні <b>власний рядок</b>
/// (<c>H-20</c>).
/// </summary>
/// <remarks>
/// ⛔ «Зібрано 0 рядків» і «джерело не пускає» лікують різні люди й різними
/// діями: перше минає само разом із простоєм AF, друге не мине ніколи. Поки
/// обидва стани виглядали в <c>itg.MaintenanceRun</c> однаково, другий просто
/// не помічали.
///
/// ⚠ Тут перевіряється саме КЛАСИФІКАЦІЯ, без бази: правило «як зведення
/// впізнає відмову в автентифікації» винесене в окремий метод рівно для того,
/// щоб його можна було довести прогоном, а не читанням.
/// </remarks>
public sealed class CollectionAuthenticationDigestTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-20")]
    public void Відмова_в_автентифікації_дістає_окремий_вид_рядка_зведення()
    {
        // Регресія: класифікацію прибрали або звузили — і рядок про джерело,
        // яке не пускає, знову зіллється зі звичайним збоєм збору.
        var message = CollectionFailure.AuthenticationRefused("STACK-1");

        Assert.Equal(NotificationJob.AuthenticationKind, NotificationJob.KindOf(message));

        // Джерело назване поіменно: у зведенні за ніч рядків десятки, і
        // «збір не вдався» без коду не каже, куди йти.
        Assert.Contains("STACK-1", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-20")]
    public void Звичайна_недоступність_джерела_окремого_рядка_НЕ_дістає()
    {
        // ⚠ Зворотний бік не менш важливий за прямий. Класифікація, яка
        // вважає відмовою в автентифікації будь-який збій, зробила б новий вид
        // рядка беззмістовним — і за тиждень його перестали б читати разом із
        // рештою.
        Assert.Equal(
            NotificationJob.CollectionKind,
            NotificationJob.KindOf("ECR-INT-0503: джерело недоступне"));

        // «Помилки не було» — теж не автентифікація.
        Assert.Equal(NotificationJob.CollectionKind, NotificationJob.KindOf(null));
    }
}
