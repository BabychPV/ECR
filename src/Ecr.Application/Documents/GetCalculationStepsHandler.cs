// src/Ecr.Application/Documents/GetCalculationStepsHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// ✎ D-565 (PERF-03): кроки розрахунку ОДНОГО рядка в актуальному прогоні — «який крок упав і з якими входами» для
/// проблеми рядка <c>StepError</c>.
/// </summary>
/// <remarks>
/// ⚠ Мінімальне читання для діагностики, а не екран «Чому це число?» (FEATURE-HSE301-VIEW §10.3): кроки рядка за адресою
/// (документ, період, екземпляр таблиці, ключ рядка) — та сама адреса, якою користуватиметься той екран.
/// <para>
/// ⛔ Чесність: кроків немає, бо прогін їх не записав (трасування методології було вимкнено), — це ФАКТ прогону у відповіді
/// (<see cref="CalculationStepsDto.NotRecordedMethodologyIds"/>), а не порожній перелік, що читався б як «помилок немає».
/// </para>
/// <para>
/// ⚠ Право — <c>Calculation.View</c> і видимість документа (невидимий — 404, B-08); таблиця, схована від читача, — 404,
/// як і неіснуюча. Читачу з обмеженнями по аркушах значення входів-аргументів не віддаються (лише коди): вхід може
/// бути коміркою схованої колонки, а адреси колонки в кроці немає, щоб це перевірити.
/// </para>
/// </remarks>
public sealed class GetCalculationStepsHandler(
    ICalculationTraceReader traces,
    ICalculationResultStore results,
    IRowStore rows,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання результатів розрахунку (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Стеля кроків одного рядка у відповіді.</summary>
    public const int MaxSteps = 200;

    /// <summary>Віддає кроки рядка в актуальному прогоні.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці рядка.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Кроки й факт прогону; без актуального прогону — <c>RunId = null</c> і порожньо.</returns>
    /// <exception cref="NotFoundException">Документ невидимий, екземпляр чужий чи схований.</exception>
    public async Task<CalculationStepsDto> HandleAsync(
        long documentId, int periodKey, long tableInstanceId, string rowKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

        var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        var instance = await rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var restricted = DocumentSheetVisibility.HasRestrictions(profile);
        if (instance.DocumentId != documentId)
        {
            throw TableNotFound(tableInstanceId);
        }

        if (restricted)
        {
            var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
            var key = new PeriodKey(periodKey);
            if (!(key.IsValid ? scope.InPeriod(key) : scope).CanReadTable(instance.TableDefId))
            {
                throw TableNotFound(tableInstanceId);
            }
        }

        var run = await results.ReadCurrentRunProfileAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (run is null)
        {
            return new CalculationStepsDto(null, null, null, [], Truncated: false);
        }

        var fact = ModuleProfile.ReadTraceSteps(run.ModulesProfileJson);
        var steps = await traces
            .ReadRowStepsAsync(run.RunId, periodKey, documentId, tableInstanceId, rowKey, MaxSteps, ct)
            .ConfigureAwait(false);

        return new CalculationStepsDto(
            run.RunId,
            fact is null ? null : [.. fact.Recorded.Order()],
            fact is null ? null : [.. fact.NotRecorded.Order()],
            [.. steps.Take(MaxSteps).Select(s => StepTraceJson.Describe(s, hideArgumentValues: restricted))],
            Truncated: steps.Count > MaxSteps);
    }

    private static NotFoundException TableNotFound(long tableInstanceId)
        => new(
            "ECR-DOC-0404",
            $"Таблиці {tableInstanceId} у документі немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["tableInstanceId"] = tableInstanceId.ToString(CultureInfo.InvariantCulture),
            });
}

/// <summary>✎ D-565: кроки рядка в актуальному прогоні.</summary>
/// <param name="RunId">Актуальний прогін; <c>null</c> — документ за період ще не рахували.</param>
/// <param name="RecordedMethodologyIds">Методології, чиї кроки прогін записав; <c>null</c> — прогін до D-565 (факту немає).</param>
/// <param name="NotRecordedMethodologyIds">Методології, чиї кроки прогін НЕ записав (трасування вимкнено); <c>null</c> — як вище.</param>
/// <param name="Steps">Кроки рядка за порядком.</param>
/// <param name="Truncated">Кроків більше за стелю — показано перші.</param>
public sealed record CalculationStepsDto(
    long? RunId,
    IReadOnlyList<int>? RecordedMethodologyIds,
    IReadOnlyList<int>? NotRecordedMethodologyIds,
    IReadOnlyList<CalculationStepDto> Steps,
    bool Truncated);

/// <summary>✎ D-565: один крок рядка.</summary>
/// <param name="Id">Ідентифікатор кроку.</param>
/// <param name="StepOrder">Порядок.</param>
/// <param name="StepCode">Код кроку (формула чи вихід).</param>
/// <param name="Expression">Вираз.</param>
/// <param name="Value">Значення текстом (інваріантна культура); <c>null</c> — числа немає.</param>
/// <param name="Error">Код помилки кроку (<c>#VALUE</c>…); <c>null</c> — крок порахувався.</param>
/// <param name="Failed">Крок упав: помилка, що не є «немає значення» (<c>#NULL</c>).</param>
/// <param name="Masked">Причина маскування в нуль; <c>null</c> — не маскувалось.</param>
/// <param name="SubstanceEntryId">Речовина кроку.</param>
/// <param name="Inputs">Входи кроку, як їх бачила формула.</param>
public sealed record CalculationStepDto(
    long Id,
    int StepOrder,
    string StepCode,
    string? Expression,
    string? Value,
    string? Error,
    bool Failed,
    string? Masked,
    long? SubstanceEntryId,
    IReadOnlyList<CalculationStepInputDto> Inputs);

/// <summary>✎ D-565: вхід кроку.</summary>
/// <param name="Kind">Вид: <c>arg</c>, <c>const</c>, <c>formula</c>, <c>period</c>.</param>
/// <param name="Code">Код аргументу, константи чи формули; для періоду — властивість.</param>
/// <param name="Value">Значення текстом; <c>null</c> — порожньо або приховано від читача.</param>
/// <param name="Unit">Код одиниці.</param>
public sealed record CalculationStepInputDto(string Kind, string Code, string? Value, string? Unit);

/// <summary>Розбір <c>TraceJson</c> кроку (v1 або голий код помилки старих кроків) у DTO.</summary>
internal static class StepTraceJson
{
    /// <summary>Крок для читача.</summary>
    /// <param name="step">Записаний крок.</param>
    /// <param name="hideArgumentValues">Не віддавати значення аргументів-комірок (читач з обмеженнями).</param>
    public static CalculationStepDto Describe(CalculationStepRecord step, bool hideArgumentValues)
    {
        string? error = null;
        var inputs = new List<CalculationStepInputDto>();

        if (step.TraceJson is { Length: > 0 } json)
        {
            if (json.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    error = Text(root, "error");
                    if (root.TryGetProperty("inputs", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var input in list.EnumerateArray())
                        {
                            var kind = Text(input, "kind") ?? "?";
                            var code = Text(input, "code") ?? Text(input, "prop") ?? "?";
                            var value = hideArgumentValues && kind == "arg" ? null : Text(input, "value");
                            inputs.Add(new CalculationStepInputDto(kind, code, value, Text(input, "unit")));
                        }
                    }
                }
                catch (JsonException)
                {
                    error = json;
                }
            }
            else
            {
                // Кроки до HSE301 A3b: у TraceJson — лише код помилки.
                error = json;
            }
        }

        return new CalculationStepDto(
            step.Id,
            step.StepOrder,
            step.StepCode,
            step.Expression,
            step.Value?.ToString(CultureInfo.InvariantCulture),
            error,
            Failed: error is not null && !string.Equals(error, "#NULL", StringComparison.Ordinal),
            step.Masked == Domain.Enums.MaskedZeroReason.None ? null : step.Masked.ToString(),
            step.SubstanceEntryId,
            inputs);
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
}
