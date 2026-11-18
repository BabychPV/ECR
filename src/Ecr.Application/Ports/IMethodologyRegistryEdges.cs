// src/Ecr.Application/Ports/IMethodologyRegistryEdges.cs
namespace Ecr.Application.Ports;

/// <summary>
/// B179 L1: ЄДИНИЙ читач таблиць методологій для графа залежностей (<c>calc.Methodology</c>,
/// <c>calc.MethodologyVersion</c>, <c>calc.MethodologyFormula</c>, <c>cfg.RegistryUse</c> вид 1,
/// <c>cfg.CalculationBinding</c>) — лише читання.
/// </summary>
/// <remarks>
/// ⛔ Модель методологій переосмислюється окремо (рішення людини 18.11): цей адаптер — одна точка, яку та робота
/// замінить, не чіпаючи графа, тригерів і екрана. Обхід адаптера стереже <c>DependencyGraphMethodologyReadGuardTests</c>.
/// </remarks>
public interface IMethodologyRegistryEdges
{
    /// <summary>Ревізія даних методологій для ключа кешу графа (публікація версії, зміна прив'язки).</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<string> RevisionAsync(CancellationToken ct);

    /// <summary>Читання довідників формулами ОПУБЛІКОВАНИХ версій методологій (<c>cfg.RegistryUse</c> вид 1).</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<MethodologyRegistryReadRow>> ListReadsAsync(CancellationToken ct);

    /// <summary>Активні прив'язки методологій до колонок шаблонів (<c>cfg.CalculationBinding</c>).</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<MethodologyBindingRow>> ListBindingsAsync(CancellationToken ct);
}

/// <summary>Формула версії методології читає довідник.</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="MethodologyVersionId">Опублікована версія.</param>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="FormulaCode">Код формули версії.</param>
/// <param name="Expression">Вираз формули (для обсягу за періодом); <c>null</c> — формули з таким кодом немає.</param>
public sealed record MethodologyRegistryReadRow(
    int MethodologyId, string MethodologyCode, int MethodologyVersionId, int RegistryDefId, string FormulaCode, string? Expression);

/// <summary>Прив'язка виходу методології до колонки шаблону.</summary>
/// <param name="CalculationBindingId">Прив'язка.</param>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="TableDefId">Таблиця шаблону.</param>
/// <param name="ColumnDefId">Колонка шаблону.</param>
/// <param name="OutputCode">Вихід методології.</param>
public sealed record MethodologyBindingRow(
    int CalculationBindingId, int MethodologyId, string MethodologyCode, int TableDefId, int ColumnDefId, string OutputCode);
