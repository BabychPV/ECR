using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Поведінка поза вікном доступу до періоду (<c>ФВ-2.16</c>, <c>H-1</c>).
/// </summary>
/// <remarks>
/// ⛔ <c>ФВ-2.16</c> називає **три** поведінки — «заборонити», «дозволити з
/// позначкою», «дозволити після явного підтвердження». Приховування серед них
/// немає, і не було ніколи: воно з'явилося в коді як <c>0</c> і нізвідки не
/// випливало.
///
/// ⚠ Значення лишається в переліку заради **наявних рядків** — це
/// <c>tinyint</c> у базі, і перенумерація мовчки змінила б зміст уже
/// збережених правил. Але завести його наново більше не можна.
/// </remarks>
public sealed class OutOfWindowBehaviorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.16")]
    public void Нове_правило_не_можна_завести_з_приховуванням()
    {
        // ⛔ Заборона стоїть у ФАБРИЦІ, а не у перевірці публікації, і це
        // сильніше: до публікації правило вже лежало б у базі, і прибирати
        // його довелося б із даних, а не з форми.
#pragma warning disable CS0618 // Саме заборона застарілого значення тут і перевіряється.
        var error = Assert.Throws<DomainException>(
            () => PeriodAccessRuleDef.AlwaysReadOnly(
                templateVersionId: 1, OutOfWindowBehavior.Hide));
#pragma warning restore CS0618

        Assert.Equal("ECR-CFG-0422", error.ErrorCode);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-2.16")]
    [InlineData(OutOfWindowBehavior.ReadOnly)]
    [InlineData(OutOfWindowBehavior.Warn)]
    [InlineData(OutOfWindowBehavior.AllowWithConfirmation)]
    public void Три_канонічні_поведінки_заводяться(OutOfWindowBehavior behavior)
    {
        // Рівно ті три, які називає вимога, — не більше і не менше.
        var rule = PeriodAccessRuleDef.AlwaysReadOnly(templateVersionId: 1, behavior);

        Assert.Equal(behavior, rule.OnOutOfWindow);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Приховування_лишається_в_переліку_зі_своїм_номером()
    {
        // ⛔ Нуль не переприсвоюється іншій поведінці. Перенумерація мовчки
        // змінила б зміст рядків, записаних до `H-1`: правило, яке вчора
        // забороняло правку, почало б означати щось інше — і помітити це не
        // зміг би ніхто.
#pragma warning disable CS0618
        Assert.Equal(0, (byte)OutOfWindowBehavior.Hide);
#pragma warning restore CS0618
        Assert.Equal(1, (byte)OutOfWindowBehavior.ReadOnly);
    }
}
