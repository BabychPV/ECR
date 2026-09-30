// src/Ecr.Application/Ports/IRowWindowFetchJob.cs
using System.Globalization;

namespace Ecr.Application.Ports;

/// <summary>
/// Маркер задачі «підтягнути значення PI за вікном рядка» (HSE301 A1, FEATURE-HSE301-VIEW §4.4).
/// </summary>
/// <remarks>
/// ⚠ Потрібен із тієї самої причини, що й решта маркерів: планувальник приймає ТИП, а прикладний
/// шар не бачить реалізацій з інфраструктури. Ціль злиття — <see cref="RowWindowFetchTarget.Of"/>:
/// задача на екземпляр таблиці рахує ВСІ рядки, вікно яких змінилося чи ще не дочитане, тож
/// злиття двох постановок (сплеск правок) нічого не губить — і саме тому payload не несе ключів
/// рядків: злиття лишило б від другої постановки тільки ціль.
/// </remarks>
public interface IRowWindowFetchJob : IBackgroundJob;

/// <summary>Завдання на підтягування вікон рядків одного екземпляра таблиці.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період екземпляра (він же ключ партиції).</param>
public sealed record RowWindowFetchRequest(long TableInstanceId, int PeriodKey)
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги: типізоване або JSON.</summary>
    /// <param name="payload">Завдання.</param>
    /// <returns>Завдання з ненульовим екземпляром.</returns>
    public static RowWindowFetchRequest Parse(object? payload)
    {
        if (payload is RowWindowFetchRequest typed)
        {
            return Validate(typed);
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);
        var request = System.Text.Json.JsonSerializer.Deserialize<RowWindowFetchRequest>(json, Options)
                      ?? throw new InvalidOperationException(
                          "Завдання підтягування вікон рядків не розбирається: невідома форма payload.");

        return Validate(request);
    }

    // ⛔ Нульовий екземпляр — не «підтягнути все»: задача без адресата мовчки не робила б нічого.
    private static RowWindowFetchRequest Validate(RowWindowFetchRequest request)
        => request.TableInstanceId > 0
            ? request
            : throw new InvalidOperationException(
                "Завдання підтягування вікон рядків не називає екземпляра таблиці: підтягувати нічого.");
}

/// <summary>Ціль злиття завдань підтягування вікон рядків.</summary>
public static class RowWindowFetchTarget
{
    /// <summary>Ціль «екземпляр таблиці».</summary>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <returns>Ключ цілі.</returns>
    public static string Of(long tableInstanceId)
        => string.Create(CultureInfo.InvariantCulture, $"row-windows-i{tableInstanceId}");
}

/// <summary>Що змінила правка в екземплярі таблиці — для порту <see cref="IRowWindowTrigger"/>.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період екземпляра.</param>
/// <param name="TableDefId">Таблиця шаблону.</param>
/// <param name="ChangedColumnDefIds">Колонки, у яких записано чи стерто значення.</param>
public sealed record RowWindowChange(
    long TableInstanceId, int PeriodKey, int TableDefId, IReadOnlyCollection<int> ChangedColumnDefIds);

/// <summary>
/// Хук запису комірок: правка Початку/Кінця/селектора вікна ставить підтягування з PI
/// (HSE301 §4.4, «одна точка виклику в <c>PatchCellsHandler</c>»).
/// </summary>
/// <remarks>
/// ⛔ Реалізація не додає звернень до бази на шляху «зачеплено колонку, що не є вікном»:
/// це гарячий шлях запису, і сторож <c>PatchCellsQueryCountTests</c> лічить кожне.
/// Постановка — <c>EnqueueCoalescedAsync</c>, не <c>EnqueueExclusiveAsync</c>: сплеск правок
/// не має переривати виконуване підтягування.
/// </remarks>
public interface IRowWindowTrigger
{
    /// <summary>Повідомляє про змінені колонки екземпляра.</summary>
    /// <param name="change">Що змінено.</param>
    /// <param name="ct">Скасування.</param>
    public Task RowsChangedAsync(RowWindowChange change, CancellationToken ct);
}

/// <summary>
/// Які колонки таблиці керують вікном рядка (Початок, Кінець, селектор): відповідь на питання
/// «чи варто ставити підтягування» без звернення до бази на кожен запис.
/// </summary>
public interface IRowWindowColumnIndex
{
    /// <summary>Колонки вікна активних прив'язок таблиці; порожньо — прив'язок немає.</summary>
    /// <param name="tableDefId">Таблиця шаблону.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlySet<int>> WindowColumnsAsync(int tableDefId, CancellationToken ct);

    /// <summary>Скидає знімок: прив'язки змінено.</summary>
    public void Invalidate();
}
