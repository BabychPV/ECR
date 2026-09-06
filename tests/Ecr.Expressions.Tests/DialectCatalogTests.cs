using Ecr.Domain.Enums;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Каталог функцій діалекту методологій (`B03` §2, директива №05 §3).
/// </summary>
/// <remarks>
/// ⛔ Попередній набір — `POWER`, `TRUNC`, `MOD`, `SWITCH`, `COALESCE` плюс
/// функції діалекту шаблонів — був **вигаданий цілком**. Він не ламав
/// компіляцію; він ламав би **числа**, і ламав би тихо: методолог написав би
/// формулу, яку наш рушій приймає, а чинний ніколи не рахував, — і звірка на
/// ній нічого не довела б.
///
/// ⚠ Склад тут звіряється із **заміром** (`tests/Ecr.Legacy.Probe`,
/// `docs/legacy-ncalc-1.3.8.md`), а не з документом.
/// </remarks>
public sealed class DialectCatalogTests
{
    [Theory]
    [InlineData("Abs")]
    [InlineData("Ceiling")]
    [InlineData("Floor")]
    [InlineData("IEEERemainder")]
    [InlineData("Log")]
    [InlineData("Max")]
    [InlineData("Pow")]
    [InlineData("Round")]
    [InlineData("Sqrt")]
    [InlineData("Truncate")]
    [InlineData("in")]
    [InlineData("if")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функція_чинного_рушія_є_в_каталозі(string name)
    {
        Assert.NotNull(DialectCatalog.Find(name));
        Assert.Equal(FunctionTier.Core, DialectCatalog.TierOf(name));
    }

    [Theory]
    [InlineData("POWER")]
    [InlineData("TRUNC")]
    [InlineData("MOD")]
    [InlineData("SWITCH")]
    [InlineData("COALESCE")]
    [InlineData("SUM")]
    [InlineData("AVERAGE")]
    [InlineData("IFERROR")]
    [InlineData("SUMIF")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вигаданих_функцій_у_діалекті_методологій_немає(string name)
    {
        // ⛔ Агрегатів діалекту шаблонів тут немає **за побудовою мови**:
        // у діалекті B немає діапазонів. Операнди — скаляри `@Arg`, `CST.X`,
        // `!Formula`. Агрегація — робота звітного рушія, а не виразу.
        Assert.Null(DialectCatalog.Find(name));
    }

    [Theory]
    [InlineData("POW")]
    [InlineData("ROUND")]
    [InlineData("abs")]
    [InlineData("SQRT")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Регістр_значущий(string name)
    {
        // ⛔ `EvaluateOptions.IgnoreCase` у чинній збірці не виставлений
        // (`Utilities.cs:42`), тому `POW(2,3)` там — помилка. Прийняти його
        // в нас означало б прийняти формулу, якої чинна система не рахувала.
        Assert.Null(DialectCatalog.Find(name));
    }

    [Theory]
    [InlineData("Ceiling", 1, 1)]
    [InlineData("Floor", 1, 1)]
    [InlineData("Truncate", 1, 1)]
    [InlineData("Max", 2, 2)]
    [InlineData("Min", 2, 2)]
    [InlineData("Log", 2, 2)]
    [InlineData("Round", 2, 2)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Арність_строга(string name, int min, int max)
    {
        // ⚠ Наш попередній каталог дозволяв `CEILING(a, significance)` і
        // варіативний `MAX` — обидва вигадані. У корпусі `Max` вкладений до
        // дванадцяти рівнів саме тому, що він бінарний.
        var signature = DialectCatalog.Find(name);

        Assert.NotNull(signature);
        Assert.Equal(min, signature.MinArgs);
        Assert.Equal(max, signature.MaxArgs);
    }

    [Theory]
    [InlineData("Ln")]
    [InlineData("ifs")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функції_каталогу_редактора_яких_немає_в_рушії_це_Extension(string name)
    {
        // ⛔ Замір (`Ecr.Legacy.Probe`) показав: у NCalc 1.3.8 обидві дають
        // «Function not found». Директива №05 §3 називає їх серед 24, бо той
        // перелік — набір редактора PI Vision, а не рушія.
        //
        // ⚠ Отже формула з `Ln` НІКОЛИ не була чинною, і в `Legacy`-версії їй
        // нічого відтворювати. Це рівно те правило, яким директива обґрунтовує
        // ярус `Extension`.
        Assert.Equal(FunctionTier.Extension, DialectCatalog.TierOf(name));
        Assert.False(DialectCatalog.IsAllowedIn(name, NumericMode.Legacy));
        Assert.True(DialectCatalog.IsAllowedIn(name, NumericMode.Strict));
    }

    [Theory]
    [InlineData("CONVERT")]
    [InlineData("SUBSTANCE")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Наші_розширення_недоступні_в_Legacy(string name)
    {
        // `Legacy` існує, щоб відтворити числа чинного рушія. Вираз, якого
        // чинний рушій обчислити не міг, за визначенням нічого не відтворює.
        Assert.Equal(FunctionTier.Extension, DialectCatalog.TierOf(name));
        Assert.False(DialectCatalog.IsAllowedIn(name, NumericMode.Legacy));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ядро_це_рівно_двадцять_дві_функції()
    {
        // ⚠ Число — вихід заміру, а не ціль. Якщо воно зміниться, зміна має
        // прийти з `tests/Ecr.Legacy.Probe`, а не з документа.
        var core = DialectCatalog.Names.Count(n => DialectCatalog.TierOf(n) == FunctionTier.Core);

        Assert.Equal(22, core);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Усі_функції_корпусу_на_місці()
    {
        // Шість функцій, реально вживаних у 2010 формулах чинної системи
        // (директива №05, поправка 1). Якщо бодай однієї немає — мігрувати
        // нема чого.
        foreach (var name in new[] { "if", "Round", "Pow", "Max", "Floor", "Sqrt" })
        {
            Assert.NotNull(DialectCatalog.Find(name));
            Assert.True(DialectCatalog.IsAllowedIn(name, NumericMode.Legacy));
        }
    }
}
