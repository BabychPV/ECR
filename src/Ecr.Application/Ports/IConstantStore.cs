namespace Ecr.Application.Ports;

using Ecr.Domain.Entities.Calculations;

/// <summary>
/// Читання констант методології зі сховища.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b>
/// Порт віддає <b>кандидатів</b>, а не готове значення: звуження за категорією
/// і речовиною та вибір темпорального інтервалу — це правила предметної
/// області (ФВ-16.5), і живуть вони в
/// <c>Ecr.Calculations.ConstantResolver</c>. Зокрема правило «кілька кандидатів
/// на одну дату — помилка конфігурації, а не привід узяти перший» неможливо
/// перевірити, якщо сховище вже вибрало один запис.
/// </remarks>
public interface IConstantStore
{
    /// <summary>
    /// Усі константи версії з цим кодом — разом із темпоральними варіантами
    /// та варіантами за категорією і речовиною.
    /// </summary>
    Task<IReadOnlyList<MethodologyConstant>> GetCandidatesAsync(
        int methodologyVersionId, string code, CancellationToken ct);
}
