using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Оптимістичне блокування.
/// </summary>
/// <remarks>
/// Ключовий тест — <c>Зміна_комірки_піднімає_RowVersion_рядка</c>.
/// <c>rowversion</c> на <c>doc.TableRow</c> **не змінюється сам**, коли ми
/// пишемо в <c>doc.CellValue</c>; забути про «дотик» рядка = зламати
/// блокування **тихо**: конфлікти перестануть виявлятися, і користувачі
/// почнуть непомітно затирати роботу один одного (B04 §2.4).
/// </remarks>
[Collection("SqlServer")]
public sealed class ConcurrencyTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Зміна_комірки_піднімає_RowVersion_рядка()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Запис_зі_застарілою_версією_рядка_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Конфлікт_в_одному_рядку_відхиляє_весь_батч()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Відповідь_на_конфлікт_містить_чуже_значення_автора_і_момент_зміни()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Два_користувачі_в_різних_рядках_не_конфліктують()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Структурна_зміна_таблиці_ловиться_через_If_Match_на_TableInstance()
        => Assert.Fail("not implemented");
}
