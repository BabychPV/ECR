// src/Ecr.Application/Ports/IRegistryImpactRecalculationJob.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ecr.Application.Ports;

/// <summary>
/// Маркер батьківської задачі «перерахувати документи, зачеплені правкою довідника» (RT-25,
/// FEATURE-REGISTRY-TABLES §5.10): один <c>jobId</c> на набір документів.
/// </summary>
/// <remarks>
/// ⚠ Потрібен із тієї самої причини, що й решта маркерів: планувальник приймає ТИП, а прикладний шар
/// не бачить реалізацій з інфраструктури. Задача сама НІЧОГО не рахує — розкладає набір на
/// <see cref="ICalculationTrigger"/> (окремий <see cref="IRecalculationJob"/> на документ і період,
/// злиття без витіснення), тож «готово» означає «поставлено в чергу», а не «перераховано».
/// </remarks>
public interface IRegistryImpactRecalculationJob : IBackgroundJob;

/// <summary>Завдання на перерахунок зачеплених правкою довідника документів.</summary>
/// <param name="RegistryDefId">Довідник, правку якого обробляємо.</param>
/// <param name="DocumentIds">
/// Документи, яких стосується запуск; вже розв'язаний і перевірений за правами перелік.
/// </param>
/// <param name="Reason">Причина від людини — для журналу задач.</param>
/// <param name="Year">
/// HSE400 3b: змінився документ-джерело даних довідника — рік періодів, у яких шукати залежні документи; тоді
/// <paramref name="DocumentIds"/> порожній — набір визначає задача під час виконання.
/// </param>
/// <param name="BindingId">
/// B179 L2: зв'язка <c>cfg.DocumentSourceBinding</c>, документ-джерело якої змінився (<c>SourceDocumentChanged</c>):
/// споживачі — з графа залежностей (<see cref="IDependencyGraphProvider"/>), а не з результатів. <c>null</c> — старі
/// форми (перелік людини; FLERT — довідник × рік).
/// </param>
/// <param name="Month">B179 L2: місяць зміни джерела (1…12); <c>null</c> — увесь рік <paramref name="Year"/>.</param>
/// <param name="Hop">
/// B179 L2: ланка ланцюжка джерел (0 — зміна людиною; споживач, що сам є джерелом, ставить наступну з +1); стеля —
/// <see cref="MaxHops"/>.
/// </param>
/// <param name="IncludeApproved">
/// B80 N5 (D-554, рішення людини 19.11): явне перекриття адміністратора — залежні перераховуються і ЗАТВЕРДЖЕНІ
/// (право <c>Calculation.RecalculateApprovedDependents</c> перевіряє ендпоінт). Окрема ціль черги
/// (<see cref="RegistryImpactRecalculationTarget.ForSourceChange"/>): не зливається з постановкою без нього.
/// </param>
/// <param name="OverrideUserId">Хто ввімкнув перекриття.</param>
/// <param name="OverrideReason">Причина перекриття (обов'язкова, в журнал записів аркушів).</param>
public sealed record RegistryImpactRecalculationRequest(
    int RegistryDefId,
    IReadOnlyList<long> DocumentIds,
    string Reason,
    int? Year = null,
    int? BindingId = null,
    int? Month = null,
    int Hop = 0,
    bool IncludeApproved = false,
    int? OverrideUserId = null,
    string? OverrideReason = null)
{
    /// <summary>B179 L2: стеля ланцюжка «джерело → споживач-джерело → …» (як <c>CrossPeriodFanOut.MaxHops</c>).</summary>
    public const int MaxHops = 8;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Чи це постановка «залежні від документа-джерела за рік» (HSE400 3b), а не перелік людини.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSourceChange => Year is > 0;

    /// <summary>B179 L2: змінився документ-джерело зв'язки <see cref="BindingId"/> (споживачі — з графа).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSourceDocumentChange => IsSourceChange && BindingId is > 0;

    /// <summary>Розбирає завдання черги: типізоване або JSON.</summary>
    /// <param name="payload">Завдання.</param>
    /// <returns>Завдання з ненульовим довідником і непорожнім переліком.</returns>
    public static RegistryImpactRecalculationRequest Parse(object? payload)
    {
        if (payload is RegistryImpactRecalculationRequest typed)
        {
            return Validate(typed);
        }

        var json = payload as string ?? JsonSerializer.Serialize(payload);
        var request = JsonSerializer.Deserialize<RegistryImpactRecalculationRequest>(json, Options)
                      ?? throw new InvalidOperationException(
                          "Завдання перерахунку зачеплених не розбирається: невідома форма payload.");

        return Validate(request);
    }

    // ⛔ Порожній перелік — не «усі зачеплені»: задача без адресатів мовчки не робила б нічого. Виняток — зміна
    // документа-джерела (HSE400 3b): там адресатів визначає рік, а не перелік.
    private static RegistryImpactRecalculationRequest Validate(RegistryImpactRecalculationRequest request)
        => request.RegistryDefId > 0 && (request.DocumentIds is { Count: > 0 } || request.IsSourceChange)
            ? request
            : throw new InvalidOperationException(
                "Завдання перерахунку зачеплених не називає довідника чи документів: рахувати нічого.");
}

/// <summary>Ціль злиття завдань перерахунку зачеплених.</summary>
public static class RegistryImpactRecalculationTarget
{
    /// <summary>Ціль «довідник × набір документів».</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="documentIds">Документи запуску.</param>
    /// <returns>Ключ цілі.</returns>
    /// <remarks>
    /// ⚠ У ключі — відбиток набору, а не самий довідник: злиття лишає payload ОСТАННЬОЇ постановки,
    /// і два запуски над різними підмножинами одного довідника втратили б перший. Однакові набори
    /// зливаються.
    /// </remarks>
    public static string Of(int registryDefId, IEnumerable<long> documentIds)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        var canonical = string.Join(',', documentIds.Distinct().Order().Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];

        return string.Create(CultureInfo.InvariantCulture, $"registry-impact-r{registryDefId}-{hash}");
    }

    /// <summary>Ціль «довідник × рік» для зміни документа-джерела (HSE400 3b).</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="year">Рік періодів.</param>
    /// <returns>Ключ цілі.</returns>
    /// <remarks>
    /// ⚠ Набору документів у ключі немає: набір визначає задача під час ВИКОНАННЯ, тож payload однієї цілі однаковий,
    /// і повторні перерахунки джерела, поки задача чекає, дають одну задачу.
    /// </remarks>
    public static string ForYear(int registryDefId, int year)
        => string.Create(CultureInfo.InvariantCulture, $"registry-impact-r{registryDefId}-y{year}");

    /// <summary>B179 L2: ціль «зв'язка × рік × місяць» для зміни документа-джерела.</summary>
    /// <param name="bindingId">Зв'язка.</param>
    /// <param name="year">Рік.</param>
    /// <param name="month">Місяць; <c>null</c> — увесь рік.</param>
    /// <param name="includeApproved">
    /// B80 N5: перекриття адміністратора («й затверджені») — суфікс <c>-approved</c>: задача з перекриттям не зливається з
    /// задачею без нього (злиття лишає payload ОДНІЄЇ постановки).
    /// </param>
    /// <returns>Ключ цілі.</returns>
    /// <remarks>
    /// ⛔ Місяць — у КЛЮЧІ, а не в payload: злиття лишає payload однієї постановки, тож сплеск правок одного місяця дає
    /// одну задачу, а правки різних місяців — окремі (≤ 13 цілей на рік). Ні документа, ні ланки в ключі немає.
    /// </remarks>
    public static string ForSourceChange(int bindingId, int year, int? month, bool includeApproved = false)
    {
        var key = month is { } m
            ? string.Create(CultureInfo.InvariantCulture, $"source-change-b{bindingId}-y{year}-m{m}")
            : string.Create(CultureInfo.InvariantCulture, $"source-change-b{bindingId}-y{year}");
        return includeApproved ? key + "-approved" : key;
    }
}
