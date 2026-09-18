// tests/Ecr.Migration.Tests/LegacyRowSerializerTests.cs

using System.Globalization;
using System.Text;
using Ecr.Migration.PiAf;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Migration.Tests;

/// <summary>
/// Побайтне відтворення legacy-рядка <c>Attribute_XXXX</c>.
/// </summary>
/// <remarks>
/// ⛔ Кожен тест тут написаний так, щоб ПАДАТИ від зняття одного правила, а не
/// щоб бути зеленим. Найгостріші приклади:
/// <list type="bullet">
/// <item><description>локаль із комою — прибери
/// <see cref="CultureInfo.InvariantCulture"/> з форматування числа, і
/// <c>125.5</c> стане <c>125,5</c>; на машині розробника з англійською
/// локаллю така мутація лишилася б невидимою;</description></item>
/// <item><description>завершальний <c>;</c> — прибери його, і рядок далі
/// виглядає правдоподібно, а звірка покаже розбіжність у КОЖНОМУ
/// рядку;</description></item>
/// <item><description>роздільник усередині значення — прибери перевірку, і
/// серіалізатор мовчки видасть рядок, у якому всі наступні поля зсунуті на
/// одну позицію. Саме такі дані «проходять будь-яку перевірку „схоже на
/// правду“ і спливають через місяці».</description></item>
/// </list>
///
/// ⚠ Усі тести навмисно в ОДНОМУ класі: xUnit не виконує тести одного класу
/// паралельно, а два з них підміняють <see cref="CultureInfo.CurrentCulture"/>
/// потоку. У сусідньому класі того ж збирання ця підміна давала б плаваючі
/// падіння там, де про локаль ніхто не думав.
/// </remarks>
public sealed class LegacyRowSerializerTests
{
    /// <summary>Порядок полів, як він лежить у <c>ext.LegacyColumnMapping</c>.</summary>
    private static readonly string[] Order = ["Land_Flag", "Land_ItemId", "Land_Kind"];

    // ── Порядок полів ────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Поля_йдуть_у_порядку_fieldOrder_а_не_в_порядку_словника()
    {
        // ⚠ Словник заповнений у ЗВОРОТНОМУ порядку навмисно: позиція поля —
        // це дані з `ext.LegacyColumnMapping.LegacyFieldIndex`, і порядок
        // ~90 таблиць індивідуальний. Серіалізатор, який іде по словнику,
        // дасть правдоподібний рядок із переплутаними колонками.
        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Land_Kind"] = "ITEM",
            ["Land_ItemId"] = 7001001,
            ["Land_Flag"] = true,
        };

        Assert.Equal("1;7001001;ITEM;", new LegacyRowSerializer().Serialize(cells, Order));
    }

    // ── Числа ────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Число_пишеться_з_крапкою_навіть_на_локалі_з_комою()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");

            var serializer = new LegacyRowSerializer();

            Assert.Equal("125.5", serializer.FormatValue(125.5m));
            Assert.Equal("125.5", serializer.FormatValue(125.5d));
            Assert.Equal("125.5", serializer.FormatValue(125.5f));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Велике_число_йде_без_розділювачів_тисяч()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");

            var serializer = new LegacyRowSerializer();

            Assert.Equal("1234567.89", serializer.FormatValue(1234567.89m));
            Assert.Equal("1234567890", serializer.FormatValue(1234567890L));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дрібне_число_не_зривається_в_експоненту()
    {
        // ⛔ Саме тут `double` і ламається: `(0.0000001d).ToString()` дає
        // `1E-07`, а експоненти в legacy-рядку не буває взагалі.
        var serializer = new LegacyRowSerializer();

        Assert.Equal("0.0000001", serializer.FormatValue(0.0000001m));
        Assert.Equal("0.0000001", serializer.FormatValue(0.0000001d));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кількість_знаків_після_крапки_зберігається_як_у_значенні()
    {
        // ⚠ У чинній системі кількість знаків береться з формату комірки
        // (`04-domain-model.md` §8), тобто вона — дані. `125.50m` мусить
        // лишитися `125.50`: нормалізація до `125.5` дала б розбіжність на
        // кожному рядку, де формат комірки має два знаки.
        Assert.Equal("125.50", new LegacyRowSerializer().FormatValue(125.50m));
    }

    // ── Порожнє, null і булеве ───────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Відсутнє_значення_стає_рядком_NULL()
    {
        var serializer = new LegacyRowSerializer();

        // Явний `null` і колонка, якої в комірках немає взагалі, — це той
        // самий стан: порожні комірки не матеріалізуються (`R-B4`).
        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Land_Flag"] = null,
        };

        Assert.Equal("NULL;NULL;NULL;", serializer.Serialize(cells, Order));
        Assert.Equal("NULL", serializer.FormatValue(null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порожній_текст_лишається_порожнім_полем_а_не_стає_NULL()
    {
        // ⚠ РІШЕННЯ, а не недогляд: `NULL` і порожнє поле — дві різні форми
        // чинного формату. `CellValueForExport` пише `NULL` для порожньої
        // комірки і ПОРОЖНЬО для комірки з помилкою (`#REF!`)
        // (`04-domain-model.md` §8). Звести їх до одного означало б втратити
        // різницю, яку побайтна звірка зобов'язана бачити, і зробити круговий
        // обхід неточним для кожного рядка з помилкою в джерелі.
        var serializer = new LegacyRowSerializer();

        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Land_Flag"] = string.Empty,
            ["Land_ItemId"] = null,
            ["Land_Kind"] = string.Empty,
        };

        Assert.Equal(";NULL;;", serializer.Serialize(cells, Order));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Булеве_стає_одиницею_або_нулем()
    {
        var serializer = new LegacyRowSerializer();

        Assert.Equal("1", serializer.FormatValue(true));
        Assert.Equal("0", serializer.FormatValue(false));

        // ⛔ Не `True`/`False`: саме це дав би `bool.ToString()`, і саме це
        // єдина мутація, яку тут можна зробити непомітно.
        Assert.DoesNotContain("True", serializer.FormatValue(true), StringComparison.Ordinal);
    }

    // ── Дати ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дата_пишеться_оголошеним_форматом_незалежно_від_локалі()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");

            var serializer = new LegacyRowSerializer();

            Assert.Equal("2025-03-01 00:00:00", serializer.FormatValue(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.Equal("2025-03-01 00:00:00", serializer.FormatValue(new DateOnly(2025, 3, 1)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Формат_дати_замінюється_одним_аргументом_без_переробки()
    {
        // ⚠ Це тест ПРО ПРОГАЛИНУ: чинного формату дати AF немає в
        // репозиторії, і дефолт тут нейтральний. Коли замовник дасть
        // вивантаження, заміна має коштувати один аргумент конструктора —
        // саме це тут і зафіксовано.
        var serializer = new LegacyRowSerializer("dd.MM.yyyy");

        Assert.Equal("dd.MM.yyyy", serializer.DateFormat);
        Assert.Equal("01.03.2025", serializer.FormatValue(new DateOnly(2025, 3, 1)));
        Assert.Equal(LegacyRowSerializer.DefaultDateFormat, new LegacyRowSerializer().DateFormat);
    }

    // ── Guid ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Guid_пишеться_так_само_як_в_AF()
    {
        // `094f83a4-9560-…` — саме така форма стоїть у прикладі рядка
        // (`03-pi-af-integration.md` §2): малі літери, дефіси, без дужок.
        var id = Guid.Parse("094F83A4-9560-4E3F-9B62-0151202A9C95");

        Assert.Equal("094f83a4-9560-4e3f-9b62-0151202a9c95", new LegacyRowSerializer().FormatValue(id));
    }

    // ── Роздільник усередині значення ────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Роздільник_усередині_значення_відхиляється_з_іменем_поля()
    {
        // ⛔ Найдорожчий із можливих дефектів цього класу: без перевірки
        // рядок складається успішно, виглядає правдоподібно і зсуває ВСІ
        // наступні поля на одну позицію. Екранування в чинному форматі немає
        // (`Join(partList, ";")`), тому відновити такий рядок неможливо
        // ніколи.
        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Land_Flag"] = true,
            ["Land_ItemId"] = 7001001,
            ["Land_Kind"] = "ITEM;EXTRA",
        };

        var error = Assert.Throws<ArgumentException>(
            () => new LegacyRowSerializer().Serialize(cells, Order));

        // Повідомлення мусить назвати саме поле: у рядку на 40 полів «десь є
        // ';'» не веде нікуди.
        Assert.Contains("Land_Kind", error.Message, StringComparison.Ordinal);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    // ── Не-ASCII і дуже довге значення ───────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кирилиця_переноситься_байт_у_байт()
    {
        const string Value = "Скид у водойму № 3 — місячний обсяг, м³";

        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Land_Flag"] = true,
            ["Land_ItemId"] = 7001001,
            ["Land_Kind"] = Value,
        };

        var row = new LegacyRowSerializer().Serialize(cells, Order);

        Assert.Equal("1;7001001;" + Value + ";", row);

        // ⛔ Саме байти, а не рядок: транслітерація, нормалізація Unicode чи
        // втеча в `С` дали б рівні за виглядом рядки і різні байти, а
        // критерій приймання — ПОБАЙТНИЙ.
        Assert.Equal(
            Encoding.UTF8.GetBytes("1;7001001;" + Value + ";"),
            Encoding.UTF8.GetBytes(row));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дуже_довге_значення_не_обрізається()
    {
        var value = new string('я', 100_000);

        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Land_Flag"] = true,
            ["Land_ItemId"] = 7001001,
            ["Land_Kind"] = value,
        };

        var row = new LegacyRowSerializer().Serialize(cells, Order);

        Assert.Equal(value.Length + "1;7001001;;".Length, row.Length);
        Assert.EndsWith("я;", row, StringComparison.Ordinal);
    }

    // ── Розбір ───────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Розбір_повертає_значення_за_кодом_колонки_і_ознаку_завершального_роздільника()
    {
        var parsed = new LegacyRowSerializer().Deserialize("1;7001001;ITEM;", Order);

        Assert.Equal("1", parsed.Cells["Land_Flag"]);
        Assert.Equal("7001001", parsed.Cells["Land_ItemId"]);
        Assert.Equal("ITEM", parsed.Cells["Land_Kind"]);
        Assert.True(parsed.TrailingSeparator);

        Assert.False(new LegacyRowSerializer().Deserialize("1;7001001;ITEM", Order).TrailingSeparator);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Маркер_NULL_при_розборі_стає_відсутнім_значенням()
    {
        var parsed = new LegacyRowSerializer().Deserialize("NULL;;ITEM;", Order);

        Assert.Null(parsed.Cells["Land_Flag"]);

        // ⚠ Порожнє поле — НЕ те саме, що `NULL`, і розбір зобов'язаний
        // розрізняти їх так само, як складання.
        Assert.Equal(string.Empty, parsed.Cells["Land_ItemId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Розбіжність_кількості_полів_при_розборі_відхиляється()
    {
        // ⛔ Мовчазне доповнення порожніми — це і є зсув колонки, від якого
        // страждає чинна система: значення лягли б у чужі колонки, і кожне з
        // них виглядало б правдоподібно.
        var error = Assert.Throws<ArgumentException>(
            () => new LegacyRowSerializer().Deserialize("1;7001001;", Order));

        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Повторений_код_колонки_у_порядку_полів_відхиляється()
    {
        string[] broken = ["Land_Flag", "Land_ItemId", "Land_Flag"];

        var error = Assert.Throws<ArgumentException>(
            () => new LegacyRowSerializer().Deserialize("1;7001001;ITEM;", broken));

        Assert.Contains("Land_Flag", error.Message, StringComparison.Ordinal);
    }

    // ── Круговий обхід ───────────────────────────────────────────────────

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("1;7001001;ITEM;")]
    [InlineData("1;7001001;ITEM")]
    [InlineData("NULL;NULL;NULL;")]
    [InlineData(";;;")]
    [InlineData("NULL;;ITEM;")]
    [InlineData("1;125.50;Скид у водойму № 3 — місячний обсяг;")]
    [InlineData("NULL;0.0000001;094f83a4-9560-4e3f-9b62-0151202a9c95;")]
    public void Круговий_обхід_відтворює_вихідний_рядок_символ_у_символ(string row)
    {
        var serializer = new LegacyRowSerializer();

        var parsed = serializer.Deserialize(row, Order);
        var again = serializer.Serialize(AsValues(parsed.Cells), Order, parsed.TrailingSeparator);

        Assert.Equal(row, again);
        Assert.Null(serializer.Compare(again, row));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Круговий_обхід_повертає_ті_самі_значення_включно_з_кирилицею()
    {
        const string Value = "Скид у водойму № 3 — місячний обсяг";

        var serializer = new LegacyRowSerializer();

        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Land_Flag"] = Value,
            ["Land_ItemId"] = new string('я', 100_000),
            ["Land_Kind"] = null,
        };

        var parsed = serializer.Deserialize(serializer.Serialize(cells, Order), Order);

        Assert.Equal(Value, parsed.Cells["Land_Flag"]);
        Assert.Equal(new string('я', 100_000), parsed.Cells["Land_ItemId"]);
        Assert.Null(parsed.Cells["Land_Kind"]);
    }

    // ── Порівняння ───────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Однакові_рядки_розбіжності_не_дають()
        => Assert.Null(new LegacyRowSerializer().Compare("1;7001001;ITEM;", "1;7001001;ITEM;"));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Розбіжність_називає_поле_а_не_символ()
    {
        // ⛔ Ось для чого існує ця вимога: «символ 7» у звіті на тисячі рядків
        // не веде нікуди, а «поле 2: очікувалося 7001001, відтворено 7001002»
        // одразу називає і колонку, і природу дефекту.
        var report = new LegacyRowSerializer().Compare("1;7001002;ITEM;", "1;7001001;ITEM;");

        Assert.NotNull(report);
        Assert.Contains("поле 2", report, StringComparison.Ordinal);
        Assert.Contains("7001001", report, StringComparison.Ordinal);
        Assert.Contains("7001002", report, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Розбіжність_називає_позицію_першого_відмінного_символу()
    {
        // `1;7001001;ITEM;` проти `1;7001001;ITEN;`: перший відмінний символ —
        // чотирнадцятий (рахуючи з одиниці).
        var report = new LegacyRowSerializer().Compare("1;7001001;ITEN;", "1;7001001;ITEM;");

        Assert.NotNull(report);
        Assert.Contains("символ 14", report, StringComparison.Ordinal);
        Assert.Contains("поле 3", report, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Різна_кількість_полів_названа_окремо()
    {
        var report = new LegacyRowSerializer().Compare("1;7001001;", "1;7001001;ITEM;");

        Assert.NotNull(report);
        Assert.Contains("кількість полів", report, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Втрачений_завершальний_роздільник_названий_окремо()
    {
        // ⚠ Без окремої гілки ця розбіжність виглядала б як «поле 3
        // не збігається», хоча значення полів однакові до одного символу.
        var report = new LegacyRowSerializer().Compare("1;7001001;ITEM", "1;7001001;ITEM;");

        Assert.NotNull(report);
        Assert.Contains("завершальний", report, StringComparison.Ordinal);
    }

    /// <summary>Значення розбору у вигляді, який приймає складання.</summary>
    private static Dictionary<string, object?> AsValues(IReadOnlyDictionary<string, string?> cells)
    {
        var values = new Dictionary<string, object?>(cells.Count, StringComparer.Ordinal);

        foreach (var pair in cells)
        {
            values[pair.Key] = pair.Value;
        }

        return values;
    }
}
