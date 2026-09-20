// tests/Ecr.Infrastructure.Tests/Jobs/NotificationJobEventKeyTests.cs
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>BE-34</c>: як рядок зведення стає подією матриці правил і ключем
/// дедуплікації. Без бази — це чисте відображення.
/// </summary>
public sealed class NotificationJobEventKeyTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(NotificationJob.CollectionKind, NotificationEventKind.CollectionFailed)]
    [InlineData(NotificationJob.AuthenticationKind, NotificationEventKind.CollectionFailed)]
    [InlineData(NotificationJob.MaterializationKind, NotificationEventKind.JobFailed)]
    [InlineData("maintenance", NotificationEventKind.JobFailed)]
    public void Вид_рядка_зведення_лягає_у_свою_подію_матриці(string digestKind, NotificationEventKind expected)
    {
        // ⚠ Відмова джерела в автентифікації лишається збоєм ЗБОРУ, хоч дія і
        // термінова: адресат той самий, що й у звичайного збою збору.
        Assert.Equal(expected, NotificationJob.EventKindOf(digestKind));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Той_самий_набір_збоїв_дає_той_самий_ключ_незалежно_від_порядку()
    {
        var first = NotificationJob.EventKeyOf(NotificationEventKind.CollectionFailed, ["SRC-02", "SRC-01"]);
        var second = NotificationJob.EventKeyOf(NotificationEventKind.CollectionFailed, ["SRC-01", "SRC-02"]);

        Assert.Equal(first, second);
        Assert.Contains("SRC-01", first, StringComparison.Ordinal);

        // Інша подія — інший ключ, інакше збій збору глушив би збій задачі.
        Assert.NotEqual(first, NotificationJob.EventKeyOf(NotificationEventKind.JobFailed, ["SRC-01", "SRC-02"]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Довгий_набір_згортається_у_відбиток_а_не_обрізається()
    {
        // ⛔ Два набори, що збігаються перші двісті символів і різняться далі.
        // Обрізання дало б їм ОДИН ключ — тобто новий збій придушувався б як
        // «та сама подія», і сповіщення про нього не пішло б ніколи.
        var many = Enumerable.Range(1, 60).Select(i => $"SRC-{i:0000}").ToList();
        var other = many.Take(59).Append("SRC-9999").ToList();

        var first = NotificationJob.EventKeyOf(NotificationEventKind.CollectionFailed, many);
        var second = NotificationJob.EventKeyOf(NotificationEventKind.CollectionFailed, other);

        Assert.NotEqual(first, second);
        Assert.True(first.Length <= NotificationDelivery.EventKeyMaxLength);
        Assert.True(second.Length <= NotificationDelivery.EventKeyMaxLength);
    }
}
