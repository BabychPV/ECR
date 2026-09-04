using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// **Два різні правила щодо `null`, які не можна плутати** (02b §6):
/// в агрегатах він поглинається, у бінарних операторах — поширюється.
/// Саме тут народжуються розбіжності зі старою системою.
/// </summary>
public sealed class NullSemanticsTests
{
    // ——— Поглинання в агрегатах ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUM_ігнорує_null_елементи()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUM_порожньої_множини_дорівнює_нулю_а_не_null()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVERAGE_не_рахує_null_ані_в_сумі_ані_в_дільнику()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVERAGE_порожньої_множини_дорівнює_null_а_не_нулю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void PRODUCT_порожньої_множини_дорівнює_одиниці()
        => Assert.Fail("not implemented");

    // ——— Поширення в бінарних операторах ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Додавання_до_null_дає_null()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Множення_null_на_нуль_дає_null_а_НЕ_нуль()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкатенація_трактує_null_як_порожній_рядок()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рівність_двох_null_дає_TRUE()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_null_з_числом_дає_null_а_не_FALSE()
        => Assert.Fail("not implemented");

    // ——— Порожня комірка проти явної порожнечі ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відсутня_комірка_бере_DefaultValue_колонки()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Явна_порожнеча_ігнорує_DefaultValue_і_дає_null()
        => Assert.Fail("not implemented");
}
