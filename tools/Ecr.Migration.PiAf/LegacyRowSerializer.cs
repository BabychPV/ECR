// tools/Ecr.Migration.PiAf/LegacyRowSerializer.cs

using System.Globalization;
using System.Text;

namespace Ecr.Migration.PiAf;

/// <summary>Розібраний legacy-рядок.</summary>
/// <param name="Cells">
/// Текст поля за кодом колонки; <c>null</c> — у рядку стояв маркер
/// <see cref="LegacyRowSerializer.EmptyMarker"/>.
/// </param>
/// <param name="TrailingSeparator">Чи стояв у рядку завершальний <c>;</c>.</param>
/// <remarks>
/// ⚠ <paramref name="TrailingSeparator"/> — частина розбору, а не дрібниця
/// оформлення: без нього круговий обхід <c>Deserialize</c> → <c>Serialize</c>
/// не відтворює вихідний рядок побайтно, а саме побайтність і є критерієм
/// приймання міграції.
/// </remarks>
public sealed record LegacyRow(IReadOnlyDictionary<string, string?> Cells, bool TrailingSeparator);

/// <summary>
/// Відтворює legacy-формат <c>Attribute_XXXX = "1;7001001;ITEM;…"</c>.
/// </summary>
/// <remarks>
/// ⚠ Це **утиліта валідації міграції**, а не інтеграційний адаптер
/// (`R-A5`, `D-44`). Запису в PI AF немає взагалі, і цей клас не має
/// перетворитися на нього: єдиний його споживач — звірка перенесених даних.
///
/// Форматування значень має збігатися з VBA **побайтно**: інакше валідація
/// покаже розбіжності там, де даних не зіпсовано, і час піде на пошук
/// неіснуючої проблеми.
///
/// Джерело правил — `docs/reference/as-is/03-pi-af-integration.md` §2
/// (<c>Join(partList, ";") &amp; ";"</c>) і
/// `docs/reference/as-is/04-domain-model.md` §8 (таблиця
/// <c>CellValueForExport</c>).
///
/// ⛔ Екранування в цьому форматі НЕМАЄ. VBA просто склеює значення, тому
/// значення з <c>;</c> усередині мовчки зсуває всі наступні поля і робить
/// рядок нерозбірним назавжди. Тут таке значення — виняток, а не тихо
/// зіпсований рядок: утиліта існує рівно для того, щоб ловити зіпсовані дані,
/// і мовчки виробляти їх сама вона не має права.
/// </remarks>
public sealed class LegacyRowSerializer
{
    /// <summary>Роздільник полів.</summary>
    public const char FieldSeparator = ';';

    /// <summary>Порожнє значення — саме рядок <c>NULL</c>, а не порожнє поле.</summary>
    public const string EmptyMarker = "NULL";

    /// <summary>
    /// Формат дати за замовчуванням.
    /// </summary>
    /// <remarks>
    /// ⛔ **ПРОГАЛИНА, названа навмисно.** Специфікація каже «дати — у чинному
    /// форматі», але чинного формату немає ні в репозиторії, ні в описі
    /// as-is: `04-domain-model.md` §8 перелічує помилку, порожнечу, число,
    /// об'єднану комірку й текст — дати серед них немає. Формат, у якому VBA
    /// записує дату в <c>;</c>-рядок, — факт зі світу замовника, а не питання
    /// судження, і вигадана відповідь тут коштувала б дорожче за назване
    /// питання: розбіжність формату дасть «розбіжності» на КОЖНОМУ рядку з
    /// датою, тобто саме те, від чого застерігає опис утиліти.
    ///
    /// Тому тут стоїть нейтральний ISO-подібний дефолт, а сам формат — один
    /// параметр конструктора. Заміна на справжній формат після вивантаження з
    /// AF — це один аргумент, а не переробка.
    /// </remarks>
    public const string DefaultDateFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly string _dateFormat;

    /// <summary>Створює серіалізатор із дефолтним форматом дати.</summary>
    public LegacyRowSerializer()
        : this(DefaultDateFormat)
    {
    }

    /// <summary>Створює серіалізатор із заданим форматом дати.</summary>
    /// <param name="dateFormat">Формат дати; див. <see cref="DefaultDateFormat"/>.</param>
    public LegacyRowSerializer(string dateFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dateFormat);

        _dateFormat = dateFormat;
    }

    /// <summary>Чинний формат дати.</summary>
    public string DateFormat => _dateFormat;

    /// <summary>Складає <c>;</c>-рядок із комірок рядка.</summary>
    /// <param name="cells">Значення за кодом колонки.</param>
    /// <param name="fieldOrder">Порядок полів із <c>ext.LegacyColumnMapping</c>.</param>
    /// <returns>Рядок у legacy-форматі із завершальним <c>;</c>.</returns>
    /// <remarks>
    /// ⚠ Завершальний <c>;</c> ставиться, бо VBA пише
    /// <c>Join(partList, ";") &amp; ";"</c> — тобто в оригіналі він є завжди.
    /// Якщо конкретне вивантаження покаже інше, є перевантаження з явним
    /// прапорцем, і <see cref="Deserialize"/> повертає цей прапорець із
    /// розібраного рядка — саме щоб круговий обхід не втрачав його.
    /// </remarks>
    public string Serialize(IReadOnlyDictionary<string, object?> cells, IReadOnlyList<string> fieldOrder)
        => Serialize(cells, fieldOrder, trailingSeparator: true);

    /// <summary>Складає <c>;</c>-рядок із комірок рядка.</summary>
    /// <param name="cells">Значення за кодом колонки.</param>
    /// <param name="fieldOrder">Порядок полів із <c>ext.LegacyColumnMapping</c>.</param>
    /// <param name="trailingSeparator">Чи ставити завершальний <c>;</c>.</param>
    /// <returns>Рядок у legacy-форматі.</returns>
    /// <exception cref="ArgumentException">
    /// Значення поля містить <see cref="FieldSeparator"/>.
    /// </exception>
    public string Serialize(
        IReadOnlyDictionary<string, object?> cells,
        IReadOnlyList<string> fieldOrder,
        bool trailingSeparator)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(fieldOrder);

        var row = new StringBuilder();

        for (var i = 0; i < fieldOrder.Count; i++)
        {
            var code = fieldOrder[i];

            // ⚠ Колонки, якої в комірках немає, не існує і в AF-рядку як
            // значення: порожні комірки не матеріалізуються (`R-B4`), тож
            // «немає запису» і «порожньо» дають той самий `NULL`.
            var text = FormatValue(cells.TryGetValue(code, out var value) ? value : null);

            if (text.Contains(FieldSeparator))
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Поле {0} ({1}) містить роздільник '{2}': «{3}». Екранування в "
                        + "legacy-форматі немає, тому такий рядок зсунув би всі наступні поля.",
                        i + 1,
                        code,
                        FieldSeparator,
                        text),
                    nameof(cells));
            }

            if (i > 0)
            {
                row.Append(FieldSeparator);
            }

            row.Append(text);
        }

        if (trailingSeparator && fieldOrder.Count > 0)
        {
            row.Append(FieldSeparator);
        }

        return row.ToString();
    }

    /// <summary>Форматує одне значення так, як його пише VBA.</summary>
    /// <param name="value">Значення комірки.</param>
    /// <returns>Текст поля.</returns>
    /// <remarks>
    /// Таблиця (`04-domain-model.md` §8 плюс специфікація точки входу):
    /// <list type="bullet">
    /// <item><description><c>null</c> — <c>NULL</c> (рядок, не порожнє поле);</description></item>
    /// <item><description>текст — як є, зокрема й порожній: порожнє ПОЛЕ — це
    /// окрема легітимна форма (в чинній системі так виглядає комірка з
    /// помилкою <c>#REF!</c>), і зводити її до <c>NULL</c> означало б
    /// втратити різницю, яку побайтна звірка зобов'язана бачити;</description></item>
    /// <item><description>булеве — <c>1</c>/<c>0</c>;</description></item>
    /// <item><description>число — інваріантно, з крапкою, без розділювачів
    /// тисяч і без експоненти;</description></item>
    /// <item><description>дата — за <see cref="DateFormat"/>.</description></item>
    /// </list>
    ///
    /// ⚠ <see cref="decimal"/> зберігає кількість знаків після крапки
    /// (<c>125.50m</c> друкується як <c>125.50</c>), і саме тому він тут
    /// основний тип числа: у чинній системі кількість знаків береться з
    /// формату комірки, тобто вона — дані, а не наслідок округлення.
    /// <see cref="double"/> проходить через <see cref="decimal"/> навмисно:
    /// його власне форматування зривається в експоненту
    /// (<c>1E-07</c>), а експоненти в legacy-рядку не буває.
    /// </remarks>
    public string FormatValue(object? value)
        => value switch
        {
            null => EmptyMarker,
            string text => text,
            bool flag => flag ? "1" : "0",
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            double or float => Convert
                .ToDecimal(value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture),
            DateTime moment => moment.ToString(_dateFormat, CultureInfo.InvariantCulture),
            DateOnly day => day
                .ToDateTime(TimeOnly.MinValue)
                .ToString(_dateFormat, CultureInfo.InvariantCulture),
            Guid id => id.ToString("D", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? EmptyMarker,
        };

    /// <summary>Розбирає <c>;</c>-рядок назад у значення полів.</summary>
    /// <param name="row">Рядок у legacy-форматі.</param>
    /// <param name="fieldOrder">Порядок полів із <c>ext.LegacyColumnMapping</c>.</param>
    /// <returns>Значення за кодом колонки і ознака завершального <c>;</c>.</returns>
    /// <exception cref="ArgumentException">
    /// Кількість полів у рядку не збігається з порядком полів, або порядок
    /// полів містить повторений код колонки.
    /// </exception>
    /// <remarks>
    /// ⛔ Розбіжність кількості полів — виняток, а не мовчазне доповнення
    /// порожніми. Саме так і виглядає зсув колонки в чинній системі («вставка
    /// колонки в Excel ламає всі попередні дані»), і мовчки розкласти такий
    /// рядок по колонках означало б розкласти його НЕПРАВИЛЬНО.
    /// </remarks>
    public LegacyRow Deserialize(string row, IReadOnlyList<string> fieldOrder)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(fieldOrder);

        var fields = SplitFields(row, out var trailingSeparator);

        if (fields.Count != fieldOrder.Count)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Полів у рядку {0}, а в порядку полів {1}. Позиційний розбір такого рядка "
                    + "розклав би значення по чужих колонках.",
                    fields.Count,
                    fieldOrder.Count),
                nameof(row));
        }

        var cells = new Dictionary<string, string?>(fieldOrder.Count, StringComparer.Ordinal);

        for (var i = 0; i < fieldOrder.Count; i++)
        {
            var field = fields[i];
            var restored = string.Equals(field, EmptyMarker, StringComparison.Ordinal) ? null : field;

            if (!cells.TryAdd(fieldOrder[i], restored))
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Код колонки «{0}» стоїть у порядку полів двічі: позиції не можуть "
                        + "вказувати на одну колонку.",
                        fieldOrder[i]),
                    nameof(fieldOrder));
            }
        }

        return new LegacyRow(cells, trailingSeparator);
    }

    /// <summary>Порівнює відтворений рядок з оригіналом.</summary>
    /// <param name="reconstructed">Рядок, відтворений із наших даних.</param>
    /// <param name="original">Рядок, як він лежить в AF.</param>
    /// <returns>Опис розбіжності або <c>null</c>, якщо збігається.</returns>
    /// <remarks>
    /// ⚠ Позиція символу є в описі, але головне в ньому — ПОЛЕ: «символ 24»
    /// сам по собі не каже нічого, а «поле 6: очікувалося 125.5, відтворено
    /// 125,5» одразу називає і колонку, і природу дефекту. Звіт міграції
    /// читає людина, і читає його тисячами рядків.
    /// </remarks>
    public string? Compare(string reconstructed, string original)
    {
        ArgumentNullException.ThrowIfNull(reconstructed);
        ArgumentNullException.ThrowIfNull(original);

        if (string.Equals(reconstructed, original, StringComparison.Ordinal))
        {
            return null;
        }

        var actual = SplitFields(reconstructed, out var actualTrailing);
        var expected = SplitFields(original, out var expectedTrailing);
        var position = FirstDifference(reconstructed, original);
        var common = Math.Min(actual.Count, expected.Count);

        for (var i = 0; i < common; i++)
        {
            if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "поле {0}, символ {1}: очікувалося «{2}», відтворено «{3}»",
                    i + 1,
                    position,
                    expected[i],
                    actual[i]);
            }
        }

        if (actual.Count != expected.Count)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "кількість полів не збігається (символ {0}): в оригіналі {1}, "
                + "у відтвореному {2}; перше зайве або відсутнє поле — {3}",
                position,
                expected.Count,
                actual.Count,
                common + 1);
        }

        // Усі поля збіглися, а рядки — ні: лишається рівно завершальний ';'.
        return string.Format(
            CultureInfo.InvariantCulture,
            "завершальний '{0}' (символ {1}): в оригіналі {2}, у відтвореному {3}",
            FieldSeparator,
            position,
            expectedTrailing ? "є" : "немає",
            actualTrailing ? "є" : "немає");
    }

    /// <summary>Ділить рядок на поля, знімаючи завершальний роздільник.</summary>
    private static List<string> SplitFields(string row, out bool trailingSeparator)
    {
        if (row.Length == 0)
        {
            trailingSeparator = false;

            return [];
        }

        var fields = new List<string>(row.Split(FieldSeparator));
        trailingSeparator = row[^1] == FieldSeparator;

        if (trailingSeparator)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        return fields;
    }

    /// <summary>Позиція першого відмінного символу, рахуючи з одиниці.</summary>
    private static int FirstDifference(string left, string right)
    {
        var limit = Math.Min(left.Length, right.Length);
        var index = 0;

        while (index < limit && left[index] == right[index])
        {
            index++;
        }

        return index + 1;
    }
}
