using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations.Scripting.Parity;

/// <summary>
/// Порівнює старий розрахунок версії (правила + <see cref="GenericCalculationModule"/>) із розрахунком її скрипта
/// (<see cref="ScriptRowSelector"/> + <see cref="ScriptCalculationModule"/>) на ОДНИХ вхідних даних (MS, план §8.4).
/// </summary>
/// <remarks>
/// ⛔ Нічого не пише: ні результатів, ні проблем рядків, ні версії. Модулі повертають значення, запис робить
/// <c>CalculationOutputWriter</c> — його тут немає.
/// <para>
/// ⚠ Для версії, що ще на <c>Engine = Formulas</c>, матеріалізовані виходи, речовини й імпорти — ті самі таблиці, що
/// читає старий шлях, тож скрипт-шлях просто читає їх (дескриптор — <c>Engine = Script</c>, текст — із параметра).
/// Один і той самий <see cref="CalculationInput"/> іде в обидва модулі.
/// </para>
/// <para>
/// Порівняння — <see cref="ParityDiff"/>: точне, без допусків. Виняток шляху (не відхилення рядка категорії) — це теж
/// розбіжність (<see cref="ParityAspect.Error"/>), а не мовчазний пропуск.
/// </para>
/// </remarks>
public sealed class ParityComparer(
    IMethodologyStore methodologies,
    MethodologyResolver resolver,
    ScriptRowSelector selector,
    CalculationInputBuilder inputBuilder,
    IEnumerable<ICalculationModule> modules)
{
    /// <summary>Порівнює один екземпляр таблиці документа за один період.</summary>
    /// <param name="methodologyVersionId">Версія методології (рушій <c>Formulas</c>, правила й формули матеріалізовані).</param>
    /// <param name="scriptText">Текст скрипта, який перевіряється (тіньовий чи чернетка).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ParityReport> CompareAsync(
        int methodologyVersionId,
        string scriptText,
        long documentId,
        PeriodKey periodKey,
        long tableInstanceId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptText);

        var methodology = await methodologies.FindByVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Версії {methodologyVersionId} немає.", nameof(methodologyVersionId));
        var version = methodology.Versions.Single(v => v.Id == methodologyVersionId);
        var oldDescriptor = new MethodologyDescriptor(
            methodology.Id, version.Id, methodology.Code, version.Version, version.Level, version.NumericMode,
            version.CalendarMode, version.TraceLevel, MethodologyEngine.Formulas);
        var scriptDescriptor = oldDescriptor with { Engine = MethodologyEngine.Script };
        var target = new ParityTarget(
            methodology.Code, version.Version, version.Id, documentId, periodKey.Value, tableInstanceId);

        var oldModule = modules.FirstOrDefault(m => m.CanHandle(oldDescriptor))
            ?? throw new InvalidOperationException($"Немає модуля для старого шляху версії {methodology.Code} {version.Version}.");
        var scriptModule = modules.OfType<ScriptCalculationModule>().FirstOrDefault()
            ?? throw new InvalidOperationException("Модуль скрипта не зареєстровано.");

        // Один знімок довідників на порівняння, спільний для обох шляхів.
        var registries = new RegistrySnapshotCache(registryAsOfUtc: null);

        // ── скрипт: підготовка (компіляція, бібліотеки); збій — розбіжність екземпляра, а не пропуск ──
        CalculationBindingContext scriptContext;
        try
        {
            scriptContext = await scriptModule
                .PrepareAsync(scriptDescriptor, documentId, periodKey, registries, new ScriptPrepareOptions(scriptText, AllowShadowLibraries: true), ct)
                .ConfigureAwait(false);
        }
        catch (DomainException error)
        {
            return new ParityReport(
                [new ParityMismatch(
                    target.MethodologyCode, target.Version, target.VersionId, documentId, periodKey.Value, tableInstanceId,
                    null, ParityAspect.Error, null, Describe(error))],
                0,
                0);
        }

        // ── вибір рядків ──
        var oldOutcome = await resolver.MatchRowsDetailedAsync(methodologyVersionId, tableInstanceId, ct).ConfigureAwait(false);
        var runtime = scriptContext.ScriptRuntime as ScriptRuntime;
        var scriptOutcome = await selector
            .MatchAsync(scriptContext.Script!, tableInstanceId, (registry, entry) => runtime?.EntryIdText(registry, entry), ct)
            .ConfigureAwait(false);

        IReadOnlyList<string> union =
            [.. oldOutcome.Matches.Select(m => m.RowKey)
                .Union(scriptOutcome.Matches.Select(m => m.RowKey), StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

        // ── входи: один пакет, одне читання, однаковий CalculationInput для обох ──
        var inputs = await inputBuilder
            .BuildAsync(tableInstanceId, union, periodKey, oldDescriptor, ct, oldOutcome.Read ?? scriptOutcome.Read)
            .ConfigureAwait(false);
        var byRow = inputs.ToDictionary(i => i.SourceRowKey ?? string.Empty, StringComparer.Ordinal);

        var oldContext = await oldModule.PrepareAsync(oldDescriptor, documentId, periodKey, registries, ct).ConfigureAwait(false);

        var oldRows = await ExecuteRowsAsync(
            oldOutcome.Matches.Select(m => m.RowKey), byRow, oldModule, oldContext, ct).ConfigureAwait(false);
        var scriptRows = await ExecuteRowsAsync(
            scriptOutcome.Matches.Select(m => m.RowKey), byRow, scriptModule, scriptContext, ct).ConfigureAwait(false);

        return ParityDiff.Compare(
            target,
            new ParitySide(
                [.. oldOutcome.Matches.Select(m => m.RowKey)], [.. oldOutcome.Excluded], oldOutcome.AllRowKeys, oldRows),
            new ParitySide(
                [.. scriptOutcome.Matches.Select(m => m.RowKey)], [.. scriptOutcome.Excluded], scriptOutcome.AllRowKeys, scriptRows));
    }

    /// <summary>
    /// Виконує модуль на відібраних рядках і зводить кожен у <see cref="ParityRowOutcome"/>: значення, відсутні аргументи,
    /// наявність <c>StepError</c>; відхилення рядка правилом категорії — <see cref="ParityRowOutcome.Rejected"/>; будь-який
    /// інший <see cref="DomainException"/> — <see cref="ParityRowOutcome.Error"/> з кодом і ключем, а не мовчазний пропуск.
    /// Рядок, для якого немає входу, результату не дає (порівняння покаже це як розбіжність, а не як збіг).
    /// </summary>
    /// <param name="rowKeys">Рядки, відібрані шляхом.</param>
    /// <param name="inputs">Входи за ключем рядка (спільні для обох шляхів).</param>
    /// <param name="module">Модуль шляху.</param>
    /// <param name="context">Контекст прив'язки модуля.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<Dictionary<string, ParityRowOutcome>> ExecuteRowsAsync(
        IEnumerable<string> rowKeys,
        Dictionary<string, CalculationInput> inputs,
        ICalculationModule module,
        CalculationBindingContext context,
        CancellationToken ct)
    {
        var result = new Dictionary<string, ParityRowOutcome>(StringComparer.Ordinal);
        foreach (var rowKey in rowKeys)
        {
            if (!inputs.TryGetValue(rowKey, out var input))
            {
                continue;
            }

            try
            {
                var output = await module.ExecuteAsync(context, input, ct).ConfigureAwait(false);
                result[rowKey] = new ParityRowOutcome(
                    rowKey,
                    [.. output.Values.Select(v => new ParityValue(v.OutputCode, v.SubstanceEntryId, v.Kind, v.Value, v.UnitId))],
                    output.MissingArguments is { Count: > 0 } missing ? missing : null,
                    output.StepError is not null);
            }
            catch (DomainException error) when (GenericCalculationModule.IsCategoryRuleRowFailure(error))
            {
                result[rowKey] = new ParityRowOutcome(rowKey, [], Rejected: true);
            }
            catch (DomainException error)
            {
                result[rowKey] = new ParityRowOutcome(rowKey, [], Error: Describe(error));
            }
        }

        return result;
    }

    private static string Describe(DomainException error)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{error.ErrorCode}: {(error.Details is { } details && details.TryGetValue("messageKey", out var key) ? key : error.Message)}");
}
