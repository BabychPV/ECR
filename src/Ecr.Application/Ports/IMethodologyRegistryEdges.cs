// src/Ecr.Application/Ports/IMethodologyRegistryEdges.cs
using Ecr.Domain.Enums;

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

    /// <summary>
    /// Читання РЕЗУЛЬТАТІВ однієї методології іншою (<c>calc.MethodologyDependency</c>): скрипт («читання результатів
    /// іншої методології», імпорт бібліотеки) і формули (посилання на вихід, бібліотека) пишуть те саме ребро.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<MethodologyResultReadRow>> ListResultReadsAsync(CancellationToken ct);
}

/// <summary>Методологія-читач залежить від результатів методології-джерела (ребро рівня методологій, не версій).</summary>
/// <param name="ReaderMethodologyId">Хто читає — рахується пізніше.</param>
/// <param name="ReaderCode">Код читача.</param>
/// <param name="SourceMethodologyId">Чиї результати (чи бібліотеку) читає.</param>
/// <param name="SourceCode">Код джерела.</param>
public sealed record MethodologyResultReadRow(
    int ReaderMethodologyId, string ReaderCode, int SourceMethodologyId, string SourceCode);

/// <summary>Формула версії методології читає довідник.</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="MethodologyVersionId">Опублікована версія.</param>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="FormulaCode">Код формули версії.</param>
/// <param name="Expression">Вираз формули (для обсягу за періодом); <c>null</c> — формули з таким кодом немає.</param>
public sealed record MethodologyRegistryReadRow(
    int MethodologyId, string MethodologyCode, int MethodologyVersionId, int RegistryDefId, string FormulaCode, string? Expression)
{
    /// <summary>Текст скрипта версії (<c>ScriptText</c>); <c>null</c> — версія формульна. Обсяг за періодом скрипта — з нього.</summary>
    public string? ScriptText { get; init; }

    /// <summary>Числовий режим версії (для компіляції скрипта).</summary>
    public NumericMode NumericMode { get; init; }

    /// <summary>Методологія — бібліотека (для компіляції скрипта).</summary>
    public bool IsLibrary { get; init; }

    /// <summary>Версія рівня <c>Module</c> (для компіляції скрипта).</summary>
    public bool IsModuleLevel { get; init; }
}

/// <summary>Прив'язка виходу методології до колонки шаблону.</summary>
/// <param name="CalculationBindingId">Прив'язка.</param>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="TableDefId">Таблиця шаблону.</param>
/// <param name="ColumnDefId">Колонка шаблону.</param>
/// <param name="OutputCode">Вихід методології.</param>
public sealed record MethodologyBindingRow(
    int CalculationBindingId, int MethodologyId, string MethodologyCode, int TableDefId, int ColumnDefId, string OutputCode);
