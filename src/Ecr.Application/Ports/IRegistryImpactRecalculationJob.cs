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
public sealed record RegistryImpactRecalculationRequest(
    int RegistryDefId,
    IReadOnlyList<long> DocumentIds,
    string Reason,
    int? Year = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Чи це постановка «залежні від документа-джерела за рік» (HSE400 3b), а не перелік людини.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSourceChange => Year is > 0;

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
}
