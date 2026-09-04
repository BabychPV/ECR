namespace Ecr.Application.Common;

/// <summary>
/// Сторінка результатів. Ендпоінтів, що повертають «усе», не існує —
/// перевіряється архітектурним тестом.
/// </summary>
/// <param name="Items">Елементи сторінки.</param>
/// <param name="NextCursor">Курсор наступної сторінки; <c>null</c> — кінець.</param>
/// <param name="TotalCount">Загальна кількість; <c>null</c>, якщо підрахунок дорогий.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int? TotalCount);
