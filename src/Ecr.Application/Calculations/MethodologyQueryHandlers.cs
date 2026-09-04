// src/Ecr.Application/Calculations/MethodologyQueryHandlers.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Calculations;

/// <summary>
/// Перелік методологій із версіями (ФВ-13.2).
/// </summary>
/// <remarks>
/// Три осі версійності не змішуються: версія визначення, вікно дії і версія
/// даних. Останньої тут немає взагалі — вона живе в довідниках.
/// </remarks>
public sealed class ListMethodologiesHandler(
    IMethodologyStore methodologies,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на читання методологій (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає перелік.</summary>
    /// <param name="methodologyIds">Методології, які цікавлять.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<MethodologyDto>> HandleAsync(
        IReadOnlyList<int> methodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);

        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var result = new List<MethodologyDto>(methodologyIds.Count);

        foreach (var id in methodologyIds)
        {
            var versions = await methodologies.GetPublishedVersionsAsync(id, ct).ConfigureAwait(false);
            if (versions.Count == 0)
            {
                continue;
            }

            result.Add(Map(id, versions));
        }

        return result;
    }

    /// <summary>Складає DTO методології з її версій.</summary>
    private static MethodologyDto Map(int methodologyId, IReadOnlyList<MethodologyVersion> versions)
    {
        var ordered = versions
            .Where(v => v.EffectiveFrom is not null)
            .OrderBy(v => v.EffectiveFrom)
            .ToList();

        var mapped = new List<MethodologyVersionDto>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            // ⚠ EffectiveTo ОБЧИСЛЮЄТЬСЯ, а не зберігається: версія чинна до
            // початку наступної (`calc`-частина `Q-027`). Друге поле в базі
            // дозволяло б задати і дірку між версіями, і накладку, і жодна
            // перевірка при публікації не встигла б за руками, що правлять їх
            // окремо. Клієнту межа потрібна — тож він її отримує обчисленою.
            var next = i + 1 < ordered.Count ? ordered[i + 1].EffectiveFrom : null;

            mapped.Add(new MethodologyVersionDto(
                ordered[i].Id,
                ordered[i].Version,
                ordered[i].Status,
                ordered[i].Level,
                ordered[i].EffectiveFrom!.Value,
                next?.AddDays(-1),
                ordered[i].NumericMode,
                ordered[i].CalendarMode,
                ordered[i].TraceLevel));
        }

        return new MethodologyDto(
            methodologyId,
            versions[0].Version,
            new Domain.ValueObjects.LocalizedText(new Dictionary<string, string>()),
            Group: null,
            mapped);
    }
}

/// <summary>
/// Прогін методології **без запису результату** (ФВ-13.5): подивитися, що
/// вийде, до публікації.
/// </summary>
public sealed class SimulateMethodologyHandler(
    ICalculationModule module,
    IMethodologyStore methodologies,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>
    /// Право на симуляцію — <b>перегляд</b>, не публікація.
    /// </summary>
    /// <remarks>
    /// Симуляція нічого не зберігає, тож вимагати від неї право публікації
    /// означало б зробити недоступною саме ту перевірку, яку роблять ПЕРЕД
    /// публікацією (ФВ-13.5).
    /// </remarks>
    public const string Permission = "Calculation.View";

    /// <summary>Проганяє версію на золотому наборі.</summary>
    /// <param name="methodologyVersionId">Версія, яку проганяємо.</param>
    /// <param name="periodKey">Період, на даних якого проганяємо.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<SimulationResultDto> HandleAsync(
        int methodologyVersionId, int periodKey, CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var methodology = await methodologies
            .FindByVersionAsync(methodologyVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-CALC-0404", $"Версії методології {methodologyVersionId} не існує.");

        var version = methodology.Versions.Single(v => v.Id == methodologyVersionId);
        var testCases = await methodologies.GetTestCasesAsync(methodologyVersionId, ct).ConfigureAwait(false);

        var outputs = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var diff = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var trace = new List<string>();

        // Порівнюємо з версією, чинною на дату періоду, а не з «останньою»:
        // саме її числа зараз у звітах (ФВ-9.3).
        var published = methodology.VersionOn(PeriodDate(periodKey));

        foreach (var testCase in testCases)
        {
            // ⚠ Трейс завжди Full незалежно від TraceLevel версії: сенс
            // симуляції саме в тому, щоб побачити кроки. Її результат нікуди
            // не пишеться, тож обсяг трейсу нічого не коштує.
            var input = testCase.Input with
            {
                Methodology = Descriptor(methodology, version, Domain.Enums.TraceLevel.Full),
            };

            var simulated = await module.ExecuteAsync(input, ct).ConfigureAwait(false);

            foreach (var value in simulated.Values)
            {
                outputs[value.OutputCode] = value.Value;
            }

            trace.AddRange(simulated.Trace.Select(s => $"{s.StepOrder}. {s.StepCode} = {s.Value}"));

            if (published is null || published.Id == version.Id)
            {
                continue;
            }

            var baseline = await module
                .ExecuteAsync(
                    testCase.Input with
                    {
                        Methodology = Descriptor(methodology, published, Domain.Enums.TraceLevel.Off),
                    },
                    ct)
                .ConfigureAwait(false);

            foreach (var value in simulated.Values)
            {
                var old = baseline.Values.FirstOrDefault(v => v.OutputCode == value.OutputCode);
                if (old is not null && old.Value != value.Value)
                {
                    diff[value.OutputCode] = value.Value - old.Value;
                }
            }
        }

        // ⛔ Жодного запису: ні в calc.CalculationResult, ні CalculationRun.
        // Інакше симуляція засмітила б історію прогонів, за якою відновлюють
        // числа, і питання «яким прогоном пораховано цей звіт» отримало б
        // відповіді, яких ніхто не запускав.
        return new SimulationResultDto(outputs, diff, trace);
    }

    private static MethodologyDescriptor Descriptor(
        Methodology methodology, MethodologyVersion version, Domain.Enums.TraceLevel trace)
        => new(
            methodology.Id,
            version.Id,
            methodology.Code,
            version.Version,
            version.Level,
            version.NumericMode,
            version.CalendarMode,
            trace);

    /// <summary>Останній день періоду — дата, на яку резолвиться версія.</summary>
    private static DateOnly PeriodDate(int periodKey)
    {
        // PeriodKey = Year*100 + Sequence (R-A6).
        var year = periodKey / 100;
        var sequence = Math.Clamp(periodKey % 100, 1, 12);

        return new DateOnly(year, sequence, DateTime.DaysInMonth(year, sequence));
    }
}
