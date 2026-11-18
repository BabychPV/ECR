// src/Ecr.Application/Ports/IScriptParityRunner.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Паритет старого й скриптового шляхів версії на документі й періоді (MS, план 8.4): що рахує рушій формул і що —
/// скрипт; нічого не пишеться.
/// </summary>
/// <remarks>
/// ⚠ Реалізація живе в проєкті рушія (<c>Ecr.Calculations</c>, <c>ParityComparer</c>); авторська сторона вирішує
/// лише, чи можна запитувати (право, доступ до документа, версія й текст).
/// </remarks>
public interface IScriptParityRunner
{
    /// <summary>Порівнює шляхи на всіх екземплярах таблиць документа за період, до яких прив'язана методологія.</summary>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Лічильники й розбіжності.</returns>
    public Task<ScriptParityResult> RunAsync(ScriptParityRequest request, CancellationToken ct);
}

/// <summary>Запит до <see cref="IScriptParityRunner"/>.</summary>
/// <param name="MethodologyVersionId">Версія зі збереженим (тіньовим) текстом.</param>
/// <param name="Text">Текст скрипта, що перевіряється.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record ScriptParityRequest(int MethodologyVersionId, string Text, long DocumentId, int PeriodKey);

/// <summary>Підсумок паритету.</summary>
/// <param name="Targets">Скільки екземплярів таблиць порівняно.</param>
/// <param name="RowsCompared">Скільки рядків порівняно за значеннями.</param>
/// <param name="ValuesCompared">Скільки значень порівняно.</param>
/// <param name="Mismatches">Усі розбіжності.</param>
public sealed record ScriptParityResult(
    int Targets, int RowsCompared, int ValuesCompared, IReadOnlyList<ScriptParityMismatch> Mismatches);

/// <summary>Одна розбіжність паритету.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowKey">Ключ рядка; <c>null</c> — розбіжність на рівні екземпляра.</param>
/// <param name="Aspect">Що розійшлося (<c>rows.selected</c>, <c>value.number</c>, …).</param>
/// <param name="Old">Значення старого шляху.</param>
/// <param name="New">Значення скрипта.</param>
public sealed record ScriptParityMismatch(long TableInstanceId, string? RowKey, string Aspect, string? Old, string? New);
