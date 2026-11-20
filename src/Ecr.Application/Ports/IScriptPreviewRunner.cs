// src/Ecr.Application/Ports/IScriptPreviewRunner.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Прогін скрипта на документі «насухо» — для перегляду в редакторі (MS, план 5.4): що вибрано, що пораховано,
/// нічого не пишеться.
/// </summary>
/// <remarks>
/// ⚠ Реалізація живе в проєкті рушія (<c>Ecr.Calculations</c>); авторська сторона лише вирішує, чи можна запитувати
/// (право, доступ до документа, компіляція й перевірки) і віддає рушієві вже перевірений текст.
/// </remarks>
public interface IScriptPreviewRunner
{
    /// <summary>Проганяє текст скрипта на документі й періоді, нічого не записуючи.</summary>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Таблиці з рядками й значеннями.</returns>
    public Task<ScriptPreviewResult> RunAsync(ScriptPreviewRequest request, CancellationToken ct);
}

/// <summary>Запит до <see cref="IScriptPreviewRunner"/>.</summary>
/// <param name="MethodologyVersionId">Версія, чий режими й імпорти діють.</param>
/// <param name="Text">Текст скрипта (уже перевірений: без діагностик).</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="TableInstanceId">Лише цей екземпляр таблиці; <c>null</c> — усі екземпляри з речення <c>rows</c>.</param>
/// <param name="MaxRows">Скільки рядків повернути з кожної таблиці (1…200).</param>
public sealed record ScriptPreviewRequest(
    int MethodologyVersionId, string Text, long DocumentId, int PeriodKey, long? TableInstanceId, int MaxRows);

/// <summary>Відповідь перегляду: таблиці.</summary>
/// <param name="Tables">Екземпляри таблиць, на яких скрипт вибирає рядки.</param>
public sealed record ScriptPreviewResult(IReadOnlyList<ScriptPreviewTable> Tables);

/// <summary>Екземпляр таблиці в перегляді.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="TotalRows">Усього рядків екземпляра.</param>
/// <param name="SelectedRows">Скільки рядків вибрано до розрахунку.</param>
/// <param name="InactiveRows">Скільки рядків неактивні (<c>active when</c> хибне).</param>
/// <param name="Rows">Рядки (не більше <c>MaxRows</c>).</param>
public sealed record ScriptPreviewTable(
    long TableInstanceId,
    string TableCode,
    int TotalRows,
    int SelectedRows,
    int InactiveRows,
    IReadOnlyList<ScriptPreviewRow> Rows);

/// <summary>Рядок таблиці в перегляді.</summary>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="RowNumber">Номер рядка в таблиці.</param>
/// <param name="State"><c>selected</c>, <c>inactive</c>, <c>notSelected</c> або <c>rejected</c>.</param>
/// <param name="Issue"><c>missingInputs</c>, <c>stepError</c> або <c>rejected</c>; <c>null</c> — без проблем.</param>
/// <param name="Missing">Обов'язкові поля, яких бракує (для <c>missingInputs</c>).</param>
/// <param name="Values">Значення величин; для <c>notSelected</c>/<c>inactive</c> — порожньо.</param>
/// <param name="Inputs">
/// Входи вибраного рядка — поля, які читає скрипт (разом із місячними як розв'язані імена аргументів): з них клієнт
/// складає золотий тест (<c>arguments[{argumentCode, value, valueString, unitId}]</c>). Для решти рядків — порожньо.
/// </param>
public sealed record ScriptPreviewRow(
    string RowKey,
    int RowNumber,
    string State,
    string? Issue,
    IReadOnlyList<string>? Missing,
    IReadOnlyList<ScriptPreviewValue> Values,
    IReadOnlyList<ScriptPreviewInput>? Inputs = null)
{
    /// <summary><c>true</c> — величини рядка урізано стелею відповіді перегляду (PSS-21: 50 000 на запит); <c>values</c> неповний.</summary>
    public bool ValuesTruncated { get; init; }
}

/// <summary>Один вхід рядка в перегляді.</summary>
/// <param name="Name">Ім'я аргументу (код колонки).</param>
/// <param name="Value">Числове значення; <c>null</c> — немає.</param>
/// <param name="Text">Текстове значення; <c>null</c> — немає.</param>
/// <param name="UnitId">Одиниця значення; <c>null</c> — безрозмірне.</param>
public sealed record ScriptPreviewInput(string Name, decimal? Value, string? Text, int? UnitId);

/// <summary>Одне значення рядка в перегляді.</summary>
/// <param name="Name">Ім'я величини (<c>var</c>/<c>output</c>).</param>
/// <param name="Substance">Код речовини для значень усередині <c>foreach substance</c>; інакше <c>null</c>.</param>
/// <param name="Kind"><c>var</c>, <c>visible</c> або <c>output</c>.</param>
/// <param name="Text">Значення текстом.</param>
/// <param name="Error">Код помилки значення (<c>#DIV/0</c> тощо); <c>null</c> — значення без помилки.</param>
public sealed record ScriptPreviewValue(string Name, string? Substance, string Kind, string Text, string? Error);
