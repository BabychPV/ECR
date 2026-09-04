namespace Ecr.Calculations;

/// <summary>
/// Резолвить константи методології за категорією, речовиною і датою.
/// </summary>
/// <remarks>
/// Саме тут живуть **контекстні коефіцієнти** — щільність, теплотворність,
/// молярна маса. Вони залежать від речовини й умов і змінюються з часом, тому
/// не є конверсіями одиниць і в <c>uom.Conversion</c> потрапити не можуть
/// (ФВ-16.5). Це розмежування — головне, що не дає числам «попливти» глобально.
/// </remarks>
public sealed class ConstantResolver(Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Значення константи з одиницею.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="code">Код константи.</param>
    /// <param name="category">Категорія; <c>null</c> — без категорії.</param>
    /// <param name="substanceEntryId">Речовина; <c>null</c> — спільна константа.</param>
    /// <param name="onDate">Дата періоду для темпорального вибору.</param>
    public Task<(decimal Value, int UnitId)?> ResolveAsync(
        int methodologyVersionId, string code, string? category, int? substanceEntryId,
        DateOnly onDate, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: фільтр за версією і кодом; звузити за категорією і речовиною (точний збіг " +
            "виграє над загальним); з темпоральних вибрати той, чий інтервал ValidFrom..ValidTo " +
            "містить onDate. Кілька кандидатів на одну дату — помилка конфігурації, а не " +
            "привід узяти перший.");
}
