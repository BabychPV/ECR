// src/Ecr.Domain/Entities/Calculations/MethodologyFormula.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Формула методології. <see cref="EvaluationOrder"/> — **обчислюваний**, а не
/// введений: порядок топологічний і рахується при публікації (ФВ-9.4).
/// </summary>
/// <remarks>
/// Дозволити людині задати порядок руками означало б, що додана формула тихо
/// зміщує решту, а помилка виявиться числом у звіті, не помилкою публікації.
/// </remarks>
public sealed class MethodologyFormula : Entity<int>
{
    private MethodologyFormula() { }

    public MethodologyFormula(int methodologyVersionId, EcrCode code, string expression)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        Expression = expression;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Expression { get; private set; } = null!;

    /// <summary>Оголошений список аргументів; токен поза ним — помилка публікації.</summary>
    public string? ArgumentsJson { get; private set; }

    /// <summary>Топологічний порядок. Заповнюється при `Publish`, не користувачем.</summary>
    public int EvaluationOrder { get; private set; }

    public int? OutputUnitId { get; private set; }

    /// <summary>Проставляє порядок, отриманий із графа залежностей.</summary>
    /// <param name="order">Позиція в топологічному порядку.</param>
    /// <remarks>
    /// ⚠ Викликається **лише** з процедури публікації після топологічного
    /// сортування. Дозволити людині задати порядок руками означало б, що додана
    /// формула тихо зміщує решту, а помилка виявиться числом у звіті, не
    /// помилкою публікації (ФВ-9.4).
    /// </remarks>
    public void SetEvaluationOrder(int order)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        EvaluationOrder = order;
    }

    /// <summary>Оголошує одиницю результату формули (ФВ-16.6).</summary>
    /// <param name="unitId">Одиниця з <c>uom.Unit</c>.</param>
    public void SetOutputUnit(int unitId) => OutputUnitId = unitId;

    /// <summary>Задає оголошений список аргументів.</summary>
    /// <param name="argumentsJson">JSON-масив імен; токен поза ним — помилка публікації.</param>
    public void SetArguments(string? argumentsJson) => ArgumentsJson = argumentsJson;
}
