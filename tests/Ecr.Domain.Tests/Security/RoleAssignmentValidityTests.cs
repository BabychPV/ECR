// tests/Ecr.Domain.Tests/Security/RoleAssignmentValidityTests.cs
using Ecr.Domain.Entities.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Security;

/// <summary>
/// Межі дії призначення ролі (<c>H-23a</c>).
/// </summary>
/// <remarks>
/// ⛔ Строкове призначення — це підміна на час відпустки. Поки її межі не
/// перевіряв ніхто, вона діяла БЕЗСТРОКОВО, а адміністратор вважав її
/// закритою: діра в доступі з хибним відчуттям, що вона закрита.
///
/// ⚠ Тут перевіряється сам доменний метод; те, що ним КОРИСТУЄТЬСЯ побудова
/// профілю, доводить окремо
/// <c>Ecr.Infrastructure.Tests.Security.RoleValidityRuleTests</c>.
/// </remarks>
public sealed class RoleAssignmentValidityTests
{
    private static readonly DateOnly May = new(2026, 5, 20);

    /// <summary>
    /// Призначення з межами дії.
    /// </summary>
    /// <remarks>
    /// ⚠ Межі виставляються рефлексією, і це не зручність тесту, а факт про
    /// систему: <c>ValidFrom</c>/<c>ValidTo</c> не має чим заповнити ЖОДЕН
    /// прикладний шлях — ні фабрика домену, ні ендпоінт видачі ролей. Строкове
    /// призначення сьогодні можна завести лише руками в базі. Це названо в
    /// звіті кроку окремо; сам метод меж від цього не стає менш потрібним —
    /// саме він вирішує, чи діють такі рядки.
    /// </remarks>
    private static RoleAssignment Assignment(DateOnly? from, DateOnly? to)
    {
        var assignment = new RoleAssignment(roleId: 5, userId: 9, principalSid: null);

        Set(assignment, nameof(RoleAssignment.ValidFrom), from);
        Set(assignment, nameof(RoleAssignment.ValidTo), to);

        return assignment;
    }

    private static void Set(RoleAssignment assignment, string property, DateOnly? value)
        => typeof(RoleAssignment)
            .GetProperty(property)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(assignment, [value]);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23a")]
    public void Призначення_без_меж_діє_завжди()
    {
        // Основний випадок: постійна роль. Якщо він зламається, права зникнуть
        // у всіх одночасно — і це помітять того ж дня.
        Assert.True(Assignment(null, null).IsEffectiveOn(May));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23a")]
    public void Строкове_призначення_закінчується_саме()
    {
        // ⛔ Регресія: підміна на час відпустки знову діє безстроково.
        // Ззовні це не ламає нічого — просто людина назавжди лишається з
        // чужими правами, і дізнаються про це не з помилки, а з наслідків.
        var vacation = Assignment(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 10));

        Assert.False(vacation.IsEffectiveOn(May));
        Assert.True(vacation.IsEffectiveOn(new DateOnly(2026, 5, 5)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23a")]
    public void Межі_включні_з_обох_боків()
    {
        // ⚠ Наказ каже «з 1 до 10 травня», і обидва ці дні — робочі дні
        // підміни. Напівінтервал відібрав би права на день раніше, ніж
        // написано в наказі, і людина побачила б це вранці десятого.
        var window = Assignment(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 10));

        Assert.True(window.IsEffectiveOn(new DateOnly(2026, 5, 1)));
        Assert.True(window.IsEffectiveOn(new DateOnly(2026, 5, 10)));
        Assert.False(window.IsEffectiveOn(new DateOnly(2026, 4, 30)));
        Assert.False(window.IsEffectiveOn(new DateOnly(2026, 5, 11)));
    }
}
