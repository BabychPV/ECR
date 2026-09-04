using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>Структурна валідація значення проти опису колонки.</summary>
public sealed class ColumnDefValidationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Текст_у_числовій_колонці_відхиляється_з_ECR_CELL_0422()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Порожнє_значення_в_обовязковій_колонці_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Lookup_колонка_вимагає_посилання_на_запис_реєстру_а_не_текст()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Колонка_типу_Unit_зберігає_посилання_на_одиницю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Запис_в_обчислену_колонку_відхиляється_з_ECR_CELL_4221()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Число_з_більшою_кількістю_знаків_ніж_Scale_відхиляється()
        => Assert.Fail("not implemented");
}
