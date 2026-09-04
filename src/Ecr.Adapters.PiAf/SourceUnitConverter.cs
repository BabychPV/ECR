namespace Ecr.Adapters.PiAf;

/// <summary>
/// Конверсія одиниць на межі інтеграції.
/// </summary>
/// <remarks>
/// Атрибути PI AF мають власний UOM, і це **найчастіше джерело мовчазних
/// розбіжностей у числах**. Тому мапінг зберігає <c>SourceUnitId</c> і
/// <c>TargetUnitId</c> явно, а несподівана зміна одиниці в джерелі **зупиняє
/// збір**, а не конвертує «як здається» (ФВ-16.9).
/// </remarks>
public sealed class SourceUnitConverter(Ecr.Domain.Services.UnitConverter converter)
{
    /// <summary>Конвертує значення на межі.</summary>
    /// <param name="value">Значення в одиниці джерела.</param>
    /// <param name="declaredSourceUnitId">Одиниця, оголошена в мапінгу.</param>
    /// <param name="actualSourceUnitCode">Одиниця, яку фактично повернуло джерело.</param>
    /// <param name="targetUnitId">Цільова одиниця.</param>
    /// <exception cref="Ecr.Application.Errors.BusinessRuleException">
    /// Фактична одиниця не збігається з оголошеною — <c>ECR-INT-0422</c>.
    /// </exception>
    public decimal Convert(decimal value, int declaredSourceUnitId, string? actualSourceUnitCode, int targetUnitId)
        => throw new NotImplementedException(
            "TODO: якщо actualSourceUnitCode заданий і не збігається з declaredSourceUnitId — " +
            "кинути BusinessRuleException('ECR-INT-0422') і ЗУПИНИТИ збір. " +
            "Мовчазна конверсія «як здається» тут гірша за зупинку: вона дає правдоподібні " +
            "числа, помилку в яких знайдуть через місяць на звірці.");
}
