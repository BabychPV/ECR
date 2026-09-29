using System.Globalization;
using Ecr.Application.Ports;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Один рядок довгої форми запиту подій: подія + один її атрибут (HSE301 §4.7.2).
/// </summary>
/// <remarks>
/// Час тут уже UTC — його нормалізує той, хто читає результат запиту
/// (<see cref="PiSqlClientDataSource"/>): там відомо, з якого джерела рядок, і
/// відмова може це назвати. Сторожову дату кінця й текст значення розбирає
/// <see cref="SourceEventFolder"/>.
/// </remarks>
/// <param name="EventId">Ідентифікатор події в джерелі.</param>
/// <param name="EventName">Назва події.</param>
/// <param name="Template">Шаблон події; <c>null</c> — колонки немає, береться шаблон запиту.</param>
/// <param name="StartUtc">Початок, UTC.</param>
/// <param name="EndUtc">Кінець, UTC, як його повернуло джерело (зі сторожовою датою).</param>
/// <param name="ModifiedUtc">Остання зміна, UTC.</param>
/// <param name="PrimaryElement">Первинний елемент.</param>
/// <param name="ParentId">Батьківська подія.</param>
/// <param name="AttrScope"><c>E</c> — атрибут події, <c>P</c> — первинного елемента.</param>
/// <param name="AttrName">Ім'я атрибута; <c>null</c> — подія без атрибутів.</param>
/// <param name="AttrValue">Сире значення атрибута.</param>
/// <param name="AttrUom">UOM атрибута.</param>
public sealed record SourceEventRow(
    string EventId,
    string? EventName,
    string? Template,
    DateTime StartUtc,
    DateTime? EndUtc,
    DateTime? ModifiedUtc,
    string? PrimaryElement,
    string? ParentId,
    string? AttrScope,
    string? AttrName,
    object? AttrValue,
    string? AttrUom);

/// <summary>
/// Згортає довгу форму «рядок на атрибут» у події: групування за
/// <see cref="SourceEventRow.EventId"/>, у порядку першої появи (HSE301 F4e).
/// </summary>
/// <remarks>
/// ⛔ Ключ групування — <b>ідентифікатор</b>, не назва: два Event Frame з
/// однаковою назвою — дві різні події, і злиття їх в одну подвоїло б атрибути
/// однієї й загубило іншу.
/// <para>
/// ⚠ Стеля рахує <b>події</b>, не рядки: рядків на подію стільки, скільки
/// атрибутів. Подія понад стелю не додається, а <see cref="Truncated"/> стає
/// <c>true</c> — тобто позначка ставиться лише тоді, коли подій справді більше,
/// а не коли їх рівно стеля. Звідси контракт тексту запиту: рядки однієї події
/// йдуть поспіль (<c>ORDER BY StartTime, EventId</c>), інакше атрибути ранішої
/// події, що прийшли після обрізу, губляться.
/// </para>
/// </remarks>
public sealed class SourceEventFolder
{
    /// <summary>
    /// Кінець, починаючи з якого подія вважається незакритою: PI AF пише
    /// відкритому Event Frame сторожову дату <c>9999-12-31</c>, а не <c>NULL</c>.
    /// </summary>
    public static readonly DateTime OpenEndSentinel = new(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string template;
    private readonly int maxEvents;
    private readonly HashSet<(string Name, SourceEventAttributeScope Scope)>? wanted;
    private readonly Dictionary<string, Draft> byId = new(StringComparer.Ordinal);
    private readonly List<Draft> order = [];

    /// <summary>Порожній згортальник.</summary>
    /// <param name="template">Шаблон запиту — для рядків без колонки <c>Template</c>.</param>
    /// <param name="maxEvents">Стеля подій.</param>
    /// <param name="attributes">Які атрибути лишити; <c>null</c> чи порожньо — усі.</param>
    public SourceEventFolder(
        string template, int maxEvents, IReadOnlyCollection<SourceEventAttributeRef>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);

        this.template = template;
        this.maxEvents = maxEvents;

        if (attributes is { Count: > 0 })
        {
            wanted = new HashSet<(string, SourceEventAttributeScope)>(
                attributes.Select(a => (a.Name, a.Scope)), NameAndScope.Instance);
        }
    }

    /// <summary>Стелю досягнуто, а подій було більше.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Додає рядок.</summary>
    /// <param name="row">Рядок довгої форми.</param>
    /// <returns><c>false</c> — стелю вичерпано, читати далі немає сенсу.</returns>
    public bool Add(SourceEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Truncated)
        {
            return false;
        }

        if (!byId.TryGetValue(row.EventId, out var draft))
        {
            if (order.Count >= maxEvents)
            {
                Truncated = true;
                return false;
            }

            draft = new Draft(row, string.IsNullOrWhiteSpace(row.Template) ? template : row.Template);
            byId.Add(row.EventId, draft);
            order.Add(draft);
        }

        if (string.IsNullOrWhiteSpace(row.AttrName))
        {
            // Подія без атрибутів — рядок з AttrName = NULL (§4.7.2).
            return true;
        }

        var scope = Scope(row.AttrScope);
        if (wanted is not null && !wanted.Contains((row.AttrName, scope)))
        {
            return true;
        }

        // Повтор того самого атрибута — перший виграє: порядок рядків належить
        // запиту, і «останній» тут нічим не правдивіший за «перший».
        if (draft.Seen.Add((row.AttrName, scope)))
        {
            var (numeric, text) = Value(row.AttrValue);
            draft.Attributes.Add(new SourceEventAttribute(row.AttrName, scope, numeric, text, row.AttrUom));
        }

        return true;
    }

    /// <summary>Згорнуті події.</summary>
    /// <remarks>
    /// ⚠ Ім'я <c>ToResult</c>, не <c>Result</c>: сторож блокувальних викликів
    /// (<c>LayerRulesTests</c>, правило 5) читає текст і бачить у <c>.Result(</c> <c>Task.Result</c>.
    /// </remarks>
    public SourceEventResult ToResult()
        => new([.. order.Select(d => d.Build())], Truncated, null);

    /// <summary>Згортає всі рядки (чиста функція).</summary>
    /// <param name="rows">Рядки довгої форми.</param>
    /// <param name="template">Шаблон запиту.</param>
    /// <param name="maxEvents">Стеля подій.</param>
    /// <param name="attributes">Які атрибути лишити; <c>null</c> чи порожньо — усі.</param>
    public static SourceEventResult Fold(
        IEnumerable<SourceEventRow> rows,
        string template,
        int maxEvents,
        IReadOnlyCollection<SourceEventAttributeRef>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var folder = new SourceEventFolder(template, maxEvents, attributes);
        foreach (var row in rows)
        {
            if (!folder.Add(row))
            {
                break;
            }
        }

        return folder.ToResult();
    }

    /// <summary>Кінець події: <c>NULL</c> чи сторожова дата — подія ще триває.</summary>
    /// <param name="end">Кінець із джерела.</param>
    public static DateTime? OpenEnd(DateTime? end)
        => end is { } value && value >= OpenEndSentinel ? null : end;

    /// <summary>Область атрибута: <c>P</c> — первинний елемент, решта — подія.</summary>
    /// <remarks>
    /// ⚠ Прийнятий дефолт (координатор, 2026-09-29): <c>P</c> або <c>PrimaryElement</c>
    /// (без регістру, з обрізкою пробілів) — <see cref="SourceEventAttributeScope.PrimaryElement"/>;
    /// усе інше — <c>E</c>, <c>NULL</c>, порожнє і <b>будь-яке невідоме значення</b> —
    /// <see cref="SourceEventAttributeScope.Event"/>, без відмови. Контракт тексту
    /// запиту дає лише <c>E</c>/<c>P</c> (§4.7.2), тож невідоме значення — дефект
    /// тексту, і атрибут потрапить в область події.
    /// </remarks>
    /// <param name="raw">Значення колонки <c>AttrScope</c>.</param>
    public static SourceEventAttributeScope Scope(string? raw)
        => raw?.Trim() is { } text
           && (string.Equals(text, "P", StringComparison.OrdinalIgnoreCase)
               || string.Equals(text, nameof(SourceEventAttributeScope.PrimaryElement), StringComparison.OrdinalIgnoreCase))
            ? SourceEventAttributeScope.PrimaryElement
            : SourceEventAttributeScope.Event;

    /// <summary>Значення атрибута: число або текст, ніколи обидва.</summary>
    /// <remarks>
    /// ⚠ Уявлення AS-IS віддає атрибут текстом (<c>CAST(Value AS String)</c>).
    /// Текст розбирається в <c>decimal</c> лише <b>інваріантною</b> культурою з
    /// <see cref="NumberStyles.Float"/> — так само, як <c>CellValueReader</c>
    /// (D-30): <c>"1.5"</c> — число, а <c>"1,5"</c> лишається текстом, бо кома
    /// в <see cref="NumberStyles.Number"/> читалася б як тисячі (15), а вгадування
    /// культури з самого рядка немає. Нечислове (цифровий стан, перелік) — текст.
    /// Типізоване значення — тим самим правилом, що точки.
    /// </remarks>
    /// <param name="raw">Сире значення.</param>
    public static (decimal? Numeric, string? Text) Value(object? raw)
        => raw switch
        {
            string text when string.IsNullOrWhiteSpace(text) => (null, null),
            string text when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                => (number, null),
            string text => (null, text),
            _ => PiSqlClientDataSource.Value(raw),
        };

    /// <summary>Подія, що збирається.</summary>
    private sealed class Draft(SourceEventRow first, string templateName)
    {
        public HashSet<(string Name, SourceEventAttributeScope Scope)> Seen { get; } = new(NameAndScope.Instance);

        public List<SourceEventAttribute> Attributes { get; } = [];

        public SourceEvent Build()
            => new(
                first.EventId,
                templateName,
                first.EventName,
                first.StartUtc,
                OpenEnd(first.EndUtc),
                first.ModifiedUtc,
                first.PrimaryElement,
                first.ParentId,
                [.. Attributes]);
    }

    /// <summary>Ім'я атрибута без регістру, область — точно.</summary>
    private sealed class NameAndScope : IEqualityComparer<(string Name, SourceEventAttributeScope Scope)>
    {
        public static readonly NameAndScope Instance = new();

        public bool Equals((string Name, SourceEventAttributeScope Scope) x, (string Name, SourceEventAttributeScope Scope) y)
            => x.Scope == y.Scope && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, SourceEventAttributeScope Scope) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name), obj.Scope);
    }
}
