// src/Ecr.Application/Ports/JobProgressMessage.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecr.Application.Ports;

/// <summary>
/// Структурований конверт повідомлення прогресу задачі: ключ каталогу рядків
/// (+ опційні параметри підстановки, + опційне ВКЛАДЕНЕ повідомлення) замість
/// готового тексту однією мовою (аудит lane6, <c>Q-326</c>).
/// </summary>
/// <remarks>
/// ⛔ До цієї картки КОЖЕН тип фонової задачі писав у <c>itg.JobProgress.Message</c>
/// готовий український рядок напряму: користувач з англійським чи казахським
/// інтерфейсом бачив необ'єднаний український текст на <c>/admin/jobs</c>
/// (аудит lane6, <c>Q-325</c> — свідомо відкладено звідти саме на цю картку).
/// Схему <c>itg.JobProgress.Message</c> (<c>nvarchar(400)</c>) міняти не
/// можна — конверт лягає в ТОЙ САМИЙ стовпець замість готового тексту.
/// <para>
/// ⚠ Прямий патерн <c>HealthCatalogText</c> (<c>Q-304</c>: резолв одразу, у
/// скоупі HTTP-запиту) тут не підходить, і <c>Q-314</c> (розділ Judgment)
/// прямо виключив цю знахідку зі свого узагальнення саме з цієї причини:
/// ЗАПИС відбувається у фоновому Quartz-потоці без <c>HttpContext</c> і без
/// мови ЧИТАЧА (читач може бути іншою людиною чи сесією, пізніше). Тому запис
/// кодує НАМІР (ключ + параметри), а резолв відбувається пізніше, при
/// ЧИТАННІ (<c>GetJobStatusHandler</c>, <c>JobProgressMessageResolver</c>),
/// де мова читача вже відома.
/// </para>
/// <para>
/// ⚠ <see cref="Inner"/> існує заради композиції кількох шарів повідомлення:
/// <c>RecalculationJob</c>'s префікс документа («Документ N (i з M): …») і
/// <c>CalculationOrchestrator.PhaseProgress</c>'s префікс фази («Методології:
/// …») обидва загортають ЧУЖЕ, уже сформоване повідомлення. Резолвер
/// підставляє резольвене значення <see cref="Inner"/> як параметр
/// <c>{message}</c> шаблону цього рівня, рекурсивно — довільна глибина без
/// нового коду на кожен новий шар композиції.
/// </para>
/// </remarks>
/// <param name="Key">Ключ каталогу рядків, префікс <c>jobs.</c>.</param>
/// <param name="Params">Підстановки <c>{name}</c>; <c>null</c> — без змінних частин.</param>
/// <param name="Inner">
/// Вкладене повідомлення нижчого шару композиції; резолвиться першим (рекурсивно)
/// і підставляється як параметр шаблону <c>{message}</c>.
/// </param>
public sealed record JobProgressMessageEnvelope(
    string Key,
    IReadOnlyDictionary<string, string>? Params = null,
    JobProgressMessageEnvelope? Inner = null);

/// <summary>
/// Кодує/декодує <see cref="JobProgressMessageEnvelope"/> у стовпець
/// <c>itg.JobProgress.Message</c>.
/// </summary>
public static class JobProgressMessageCodec
{
    /// <summary>
    /// Межа стовпця <c>itg.JobProgress.Message</c> — <c>nvarchar(400)</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Число, а не посилання на <c>HasMaxLength</c> із конфігурації EF: та
    /// лежить в <c>Ecr.Infrastructure</c>, на яку цей шар не посилається за
    /// побудовою. Розбіжність двох чисел стереже окремий тест на моделі.
    /// </remarks>
    public const int MaxEncodedLength = 400;

    /// <summary>Позначка того, що текст обрізано.</summary>
    private const string TruncationMark = "…";

    /// <summary>
    /// Короткі імена властивостей (<c>k</c>/<c>p</c>/<c>i</c>) — навмисно, не
    /// заради стилю: конверт мусить вміщатися в <c>nvarchar(400)</c>
    /// (<c>Q-326</c>) навіть у найдовшому випадку композиції (кілька
    /// параметрів + вкладене повідомлення).
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Кодує конверт у компактний JSON.</summary>
    public static string Encode(JobProgressMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return JsonSerializer.Serialize(ToDto(envelope), Options);
    }

    /// <summary>
    /// Кодує конверт, укорочуючи ОДИН вільний параметр рівно настільки, щоб
    /// результат гарантовано вліз у <see cref="MaxEncodedLength"/>.
    /// </summary>
    /// <param name="envelope">Конверт.</param>
    /// <param name="freeTextParam">
    /// Ім'я параметра з текстом ДОВІЛЬНОЇ довжини (<c>error</c> у
    /// <c>jobs.retryScheduled</c>): решта параметрів — числа й коди, і різати
    /// їх означало б зіпсувати саме те, що читабельне.
    /// </param>
    /// <remarks>
    /// ⛔ Рахується довжина ПІСЛЯ кодування, і саме тут була пастка. У JSON
    /// кирилиця виходить екранованою — шість символів на літеру, — тож
    /// повідомлення з 70 українських слів має сирі ~300 символів (менше за
    /// межу!) і закодовані ~1900 (утричі більше). Обрізання за сирою довжиною
    /// не спрацювало б узагалі: воно нічого не обрізало б.
    ///
    /// ⚠ Двійковий пошук по кількості лишених символів: межа монотонна
    /// (довший текст — довший конверт), тож ціна — логарифм серіалізацій
    /// замість лінійного підбору.
    /// </remarks>
    public static string EncodeWithinLimit(JobProgressMessageEnvelope envelope, string freeTextParam)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrEmpty(freeTextParam);

        var encoded = Encode(envelope);

        if (encoded.Length <= MaxEncodedLength
            || envelope.Params is null
            || !envelope.Params.TryGetValue(freeTextParam, out var text)
            || text.Length == 0)
        {
            return encoded;
        }

        // Найменший можливий варіант — сама позначка обрізання. Він же
        // відповідь, якщо не вміщається навіть вона: краще завідомо коротке
        // повідомлення, ніж виняток замість результату задачі.
        var best = Encode(WithParam(envelope, freeTextParam, TruncationMark));

        var low = 0;
        var high = text.Length - 1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = Encode(WithParam(envelope, freeTextParam, TakeFirst(text, middle)));

            if (candidate.Length <= MaxEncodedLength)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// Укорочує вільний текст до <paramref name="maxLength"/> символів,
    /// лишаючи впізнаваний ПОЧАТОК і позначку обрізання.
    /// </summary>
    /// <param name="text">Текст.</param>
    /// <param name="maxLength">Межа в символах; результат ніколи не довший.</param>
    /// <remarks>
    /// ⚠ Для стовпців, куди текст лягає БЕЗ кодування
    /// (<c>itg.JobProgress.Error</c>, <c>nvarchar(2000)</c>) — там сира
    /// довжина і є тією, яку перевіряє SQL Server.
    /// </remarks>
    public static string Shorten(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        return text.Length <= maxLength
            ? text
            : TakeFirst(text, Math.Max(0, maxLength - TruncationMark.Length));
    }

    /// <summary>Перші <paramref name="keep"/> символів плюс позначка обрізання.</summary>
    /// <remarks>
    /// ⚠ Сурогатна пара, розрізана навпіл, дає недійсний рядок — на такій межі
    /// беремо на символ менше. Кирилиці це не стосується, а от аварійне
    /// повідомлення з емодзі чи рідкісним ієрогліфом цілком можливе.
    /// </remarks>
    private static string TakeFirst(string text, int keep)
    {
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), TruncationMark);
    }

    /// <summary>Той самий конверт із підміненим значенням одного параметра.</summary>
    private static JobProgressMessageEnvelope WithParam(
        JobProgressMessageEnvelope envelope, string name, string value)
    {
        var parameters = new Dictionary<string, string>(envelope.Params!, StringComparer.Ordinal)
        {
            [name] = value,
        };

        return envelope with { Params = parameters };
    }

    /// <summary>
    /// Пробує розібрати рядок як конверт.
    /// </summary>
    /// <param name="raw">Сирий вміст стовпця <c>Message</c>; <c>null</c> — немає повідомлення.</param>
    /// <param name="envelope">Розібраний конверт; недійсний, коли метод повертає <c>false</c>.</param>
    /// <returns>
    /// <c>false</c> — рядок НЕ в цьому форматі: стара пряма форма запису ДО
    /// <c>Q-326</c> (готовий український текст), чи ІНШІ дані в тому самому
    /// стовпці, що не є текстом узагалі (<c>ExcelExportJob</c> пише сюди
    /// <c>exportId</c> на 100 % — ключ, за яким клієнт забирає файл, а не
    /// повідомлення для людини). Обидва випадки мають повертатися З ЦЬОГО
    /// методу як «не конверт», а не кидати виняток чи ламати читання.
    /// </returns>
    public static bool TryDecode(string? raw, out JobProgressMessageEnvelope envelope)
    {
        envelope = null!;

        // ⚠ Швидкий вихід на порожній рядок і на будь-що, що не починається
        // з `{`: `JsonSerializer.Deserialize` на випадковому GUID (типовий
        // вміст ДО цієї картки і `exportId`-повідомлення ПІСЛЯ неї) кинув би
        // виняток на кожному такому виклику — дешевше відсікти його тут.
        if (string.IsNullOrEmpty(raw) || raw[0] != '{')
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(raw, Options);

            if (dto is null || string.IsNullOrEmpty(dto.K))
            {
                return false;
            }

            envelope = FromDto(dto);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Dto ToDto(JobProgressMessageEnvelope envelope)
        => new(envelope.Key, envelope.Params, envelope.Inner is null ? null : ToDto(envelope.Inner));

    private static JobProgressMessageEnvelope FromDto(Dto dto)
        => new(dto.K, dto.P, dto.I is null ? null : FromDto(dto.I));

    /// <summary>Форма JSON на дроті — короткі імена заради бюджету <c>nvarchar(400)</c>.</summary>
    private sealed record Dto(
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("p")] IReadOnlyDictionary<string, string>? P = null,
        [property: JsonPropertyName("i")] Dto? I = null);
}

/// <summary>
/// Зручні виклики для задач, що пишуть структурований прогрес (<c>Q-326</c>),
/// не збираючи <see cref="JobProgressMessageEnvelope"/> вручну на кожному
/// сайті виклику.
/// </summary>
public static class JobProgressExtensions
{
    /// <summary>Пише прогрес зі структурованим ключем каталогу без параметрів.</summary>
    public static Task ReportKeyAsync(
        this IJobProgress progress, int percent, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return progress.ReportAsync(
            percent, JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(key)), ct);
    }

    /// <summary>Пише прогрес зі структурованим ключем каталогу і параметрами підстановки.</summary>
    public static Task ReportKeyAsync(
        this IJobProgress progress,
        int percent,
        string key,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return progress.ReportAsync(
            percent, JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(key, parameters)), ct);
    }
}
