using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перевіряє фізичну модель на **реальному** SQL Server.
/// </summary>
/// <remarks>
/// Ці інваріанти неможливо перевірити на SQLite, а помилка в них виявляється
/// не при написанні коду, а на 108 млн рядків — коли міняти вже дорого.
/// </remarks>
[Collection("SqlServer")]
public sealed class PhysicalModelTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Первинний_ключ_CellValue_починається_з_партиційного_стовпця()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void CellValue_не_має_сурогатного_Id()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Усі_унікальні_індекси_партиційованих_таблиць_містять_PeriodKey()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Складений_FK_не_дає_записати_комірку_в_колонку_чужої_таблиці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void FK_на_рядок_складений_із_PeriodKey_і_TableRowId()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Запит_за_один_період_читає_рівно_одну_партицію()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Явна_порожнеча_без_значень_проходить_CHECK_а_з_значенням_ні()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Конверсію_між_різними_розмірностями_неможливо_вставити_в_таблицю()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Немає_жодної_колонки_типу_float_у_результатних_таблицях()
        => Assert.Fail("not implemented");
}
