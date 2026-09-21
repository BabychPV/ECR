// tests/Ecr.Domain.Tests/External/EntityFieldMapLifecycleTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Пауза, відновлення й приймання зміни одиниці джерела (директива №15,
/// <c>BE-27</c>).
/// </summary>
/// <remarks>
/// ⚠ Швидкий рівень доказу: правила переходів без бази й без HTTP. Що
/// призупинений мапінг справді НЕ ПИШЕ значень, доводить
/// <c>PausedMappingCollectionPathTests</c> на шляху збору — прапорець сам по
/// собі не доводить нічого.
/// </remarks>
public sealed class EntityFieldMapLifecycleTests
{
    private static EntityFieldMap Map() => EntityFieldMap.ToColumn(1, "Flare_01_CO", 10);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-27")]
    public void Пауза_і_відновлення_ходять_в_обидва_боки_а_повторні_відмовляють()
    {
        var map = Map();
        Assert.True(map.IsActive);

        map.Pause();
        Assert.False(map.IsActive);

        // ⚠ Повторна пауза — помилка, а не «нічого не сталося»: вона майже
        // завжди означає, що викликач вважає стан іншим, ніж він є.
        var paused = Assert.Throws<DomainException>(map.Pause);
        Assert.Equal("ECR-INT-0409", paused.ErrorCode);

        map.Resume();
        Assert.True(map.IsActive);

        Assert.Equal("ECR-INT-0409", Assert.Throws<DomainException>(map.Resume).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.9")]
    [Trait("Finding", "BE-27")]
    public void Приймання_зміни_одиниці_віддає_ПОПЕРЕДНЮ_одиницю()
    {
        // ⛔ Саме повернене значення й потрапляє в журнал безпеки як «з якої».
        // Якби метод його не віддавав, викликачеві довелося б читати
        // `SourceUnitId` ДО зміни — а забути це на другому викликачі коштувало
        // б запису «прийнято одиницю X замість X».
        var map = Map();
        map.SetUnits(sourceUnitId: 7, targetUnitId: 9);

        Assert.Equal(7, map.AcceptSourceUnitChange(42));
        Assert.Equal(42, map.SourceUnitId);

        // Цільова одиниця не чіпається: змінилося джерело, а не те, у чому
        // значення лягає в ECR.
        Assert.Equal(9, map.TargetUnitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-16.9")]
    [Trait("Finding", "BE-27")]
    public void Одиницю_не_можна_прийняти_двічі_і_не_можна_прийняти_без_оголошеної()
    {
        var declared = Map();
        declared.SetUnits(sourceUnitId: 7, targetUnitId: 9);
        _ = declared.AcceptSourceUnitChange(42);

        var again = Assert.Throws<DomainException>(() => declared.AcceptSourceUnitChange(42));
        Assert.Equal("ECR-INT-0409", again.ErrorCode);

        // ⛔ Мапінг без оголошеної одиниці зупинки збору не бачить узагалі
        // (`EnsureDeclaredUnit` виходить одразу), тож «зміни» для нього не
        // існує. Приймати тут означало б оголошувати одиницю вперше — це
        // редагування мапінгу, а не рішення про розбіжність.
        var undeclared = Map();

        Assert.Equal(
            "ECR-INT-0409",
            Assert.Throws<DomainException>(() => undeclared.AcceptSourceUnitChange(42)).ErrorCode);
    }
}
