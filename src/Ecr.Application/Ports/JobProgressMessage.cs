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
