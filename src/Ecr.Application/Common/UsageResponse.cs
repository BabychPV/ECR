// src/Ecr.Application/Common/UsageResponse.cs
namespace Ecr.Application.Common;

/// <summary>
/// «Де використовується» — єдина форма відповіді <c>GET /…/{id}/usage</c>
/// (директива №15, BE-15).
/// </summary>
/// <param name="Total">Скільки посилань усього — не лише показаних.</param>
/// <param name="Items">Перші <see cref="PageSize"/> посилань.</param>
/// <remarks>
/// ⚠ <paramref name="Total"/> і довжина <paramref name="Items"/> — різні числа
/// навмисно: діалог видалення показує кілька перших і чесно каже, скільки їх
/// насправді. Перелік без загальної кількості читався б як «оце й усе».
/// </remarks>
public sealed record UsageResponse(int Total, IReadOnlyList<UsageItemDto> Items)
{
    /// <summary>Скільки посилань віддається в <see cref="Items"/>.</summary>
    public const int PageSize = 20;
}

/// <summary>Одне місце, що посилається на ресурс.</summary>
/// <param name="Kind">Рід залежного об'єкта (<c>templateColumn</c>, <c>methodologyConstant</c>…).</param>
/// <param name="Id">Ідентифікатор залежного об'єкта — рядком, бо ключі різних таблиць різного типу.</param>
/// <param name="Label">Те, чим об'єкт упізнає людина: код.</param>
/// <param name="Route">Маршрут клієнта до об'єкта; <c>null</c> — окремого екрана немає.</param>
public sealed record UsageItemDto(string Kind, string Id, string Label, string? Route);
