using Ecr.Domain.Entities.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Яку версію методології брати, коли їх кілька (<c>ФВ-9.3</c>, <c>H-24d-4</c>).
/// </summary>
/// <remarks>
/// ⛔ Тестів не було, і саме тому дефект прожив у **трьох** місцях одразу:
/// <c>Methodology.VersionOn</c>, <c>MethodologyResolver.ResolveVersionAsync</c>
/// і <c>MethodologyStore</c> сортували лише за <c>EffectiveFrom</c>, а за
/// рівних дат брали «перший» — тобто відповідь визначав порядок рядків, який
/// поверне SQL Server.
///
/// ⚠ Це рівно дефект чинної системи, відтворений у нашому коді:
/// <c>Q_Common.cs:14-16</c> робить <c>SELECT</c> без <c>ORDER BY</c>,
/// <c>StagesBase.cs:23</c> бере <c>List.Find</c>. Різниця в тому, що там це
/// відкриття, а тут — наш власний код.
///
/// ⛔ Правило подається як **наш вибір**, а не як відтворення: чинної
/// поведінки як правила не існує, відтворювати нема чого. Дев'ять перекриттів
/// із корпусу йдуть методологу питанням, а не рішенням.
/// </remarks>
public sealed class MethodologyVersionOrderTests
{
    private static MethodologyVersionKey Key(string date, string version, int id = 1)
        => new(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), version, id);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Пізніша_дата_виграє()
    {
        var later = Key("2026-06-01", "1.0.0.0");
        var earlier = Key("2026-01-01", "9.9.9.9");

        // ⚠ Дата сильніша за номер: версія 9.9.9.9, чинна з січня, не має
        // перекривати скромну 1.0.0.0, яка почалася у червні.
        Assert.True(MethodologyVersionKey.Currency.Compare(later, earlier) > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void За_рівних_дат_виграє_старша_версія()
    {
        // ⛔ Головне твердження. Доти тут вигравав «перший рядок вибірки», і
        // перебудова індексу здатна була мовчки змінити, за якою формулою
        // пораховано рік.
        var v2 = Key("2026-01-01", "2.0.0.0", id: 10);
        var v1 = Key("2026-01-01", "1.0.0.0", id: 20);

        Assert.True(MethodologyVersionKey.Currency.Compare(v2, v1) > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Десята_версія_старша_за_дев_яту()
    {
        // ⛔ Порядкове порівняння рядків поставило б `1.10` ПЕРЕД `1.9`, бо
        // «1» < «9». Десята версія методології — не екзотика, і помилка
        // виглядала б як розрахунок за застарілою формулою без жодної ознаки.
        var tenth = Key("2026-01-01", "1.10.0.0");
        var ninth = Key("2026-01-01", "1.9.0.0");

        Assert.True(MethodologyVersionKey.Currency.Compare(tenth, ninth) > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Нерозбірний_номер_поступається_розбірному()
    {
        // ⚠ Не з естетики: про версію, номер якої ми не розуміємо, не можна
        // сказати нічого, і надавати їй перевагу означало б обирати те, чого
        // не знаєш.
        var parsable = Key("2026-01-01", "1.0.0.0");
        var garbage = Key("2026-01-01", "чернетка");

        Assert.True(MethodologyVersionKey.Currency.Compare(parsable, garbage) > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Повний_збіг_дати_і_номера_вирішується_сталою_відповіддю()
    {
        // ⚠ Тут будь-яка відповідь однаково довільна — але вона має бути ТІЄЮ
        // САМОЮ сьогодні й після перебудови індексу. Саме сталість, а не
        // «правильність», і є змістом третього правила.
        var older = Key("2026-01-01", "1.0.0.0", id: 5);
        var newer = Key("2026-01-01", "1.0.0.0", id: 6);

        Assert.True(MethodologyVersionKey.Currency.Compare(newer, older) > 0);
        Assert.Equal(0, MethodologyVersionKey.Currency.Compare(newer, newer));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Неопублікована_версія_поступається_будь_якій_опублікованій()
    {
        var published = Key("2026-01-01", "1.0.0.0");
        var draft = new MethodologyVersionKey(null, "2.0.0.0", 99);

        Assert.True(MethodologyVersionKey.Currency.Compare(published, draft) > 0);
    }
}
