// src/Ecr.Application/Calculations/Scripting/ScriptMaterializer.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>Що саме матеріалізувати понад виходи, речовини, імпорти й таблиці читання.</summary>
/// <param name="Publish">
/// Публікація: додати відсутні прив'язки виходів до колонок, записати залежності від методологій-джерел
/// (<c>calc.MethodologyDependency</c>) і ребра довідників (<c>cfg.RegistryUse</c>). Збереження чернетки — <c>false</c>.
/// </param>
public sealed record ScriptMaterializeOptions(bool Publish);

/// <summary>Підсумок матеріалізації.</summary>
/// <param name="AddedBindings">Скільки прив'язок виходів до колонок додано (лише при публікації).</param>
/// <param name="BoundOutputCodes">Виходи, що мають хоч одну активну прив'язку (після додавання).</param>
/// <param name="DependencyMethodologyIds">Методології, від яких залежить версія (імпорти й джерела результатів).</param>
public sealed record ScriptMaterialization(
    int AddedBindings,
    IReadOnlyList<string> BoundOutputCodes,
    IReadOnlyList<int> DependencyMethodologyIds);

/// <summary>
/// Матеріалізація скрипта в «дитячі» таблиці версії (MS, план 5.1, 13.1, 13.9): виходи, речовини, імпорти, таблиці
/// читання; при публікації — ще й прив'язки виходів до колонок (лише додавання), залежності й ребра довідників.
/// </summary>
/// <remarks>
/// ⚠ Усі зміни — у трекері змін: зберігає викликач (<see cref="IUnitOfWork"/>) тією ж транзакцією, що й текст.
/// Передумова: скрипт скомпільовано й перевірено без діагностик (<see cref="ScriptEnvironmentChecker"/>).
/// </remarks>
public sealed class ScriptMaterializer(
    IMethodologyStore methodologies,
    IMethodologyDraftStore drafts,
    IUnitCatalog units,
    ICalculationBindingStore bindings,
    IRegistryStore? registries = null,
    IRegistryUseStore? registryUses = null)
{
    /// <summary>Ім'я «формули» в ребрах <c>cfg.RegistryUse</c>: скрипт — одна одиниця читання довідників.</summary>
    public const string RegistryUseSource = "script";

    /// <summary>Матеріалізує скрипт версії.</summary>
    /// <param name="versionId">Версія методології.</param>
    /// <param name="methodologyId">Методологія-власник.</param>
    /// <param name="script">Скомпільований скрипт.</param>
    /// <param name="environment">Результат перевірки оточення (живі таблиці, джерела результатів).</param>
    /// <param name="options">Обсяг матеріалізації.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Підсумок.</returns>
    public async Task<ScriptMaterialization> MaterializeAsync(
        int versionId,
        int methodologyId,
        CompiledScript script,
        ScriptEnvironment environment,
        ScriptMaterializeOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        var catalog = await units.GetAsync(ct).ConfigureAwait(false);

        var outputs = script.Outputs
            .Select(o => new ScriptOutputRow(
                o.Code,
                catalog.Units.TryGetValue(o.UnitCode, out var unit)
                    ? unit.Id
                    : throw new InvalidOperationException($"Одиниці «{o.UnitCode}» немає: скрипт має бути перевірений до матеріалізації."),
                o.Ordinal,
                o.IsPerSubstance))
            .ToList();
        await methodologies.ReplaceScriptOutputsAsync(versionId, outputs, ct).ConfigureAwait(false);

        var found = script.Substances.Count == 0
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(
                await methodologies.FindSubstanceEntryIdsAsync(script.Substances, ct).ConfigureAwait(false),
                StringComparer.OrdinalIgnoreCase);
        await methodologies
            .ReplaceScriptSubstancesAsync(
                versionId, [.. script.Substances.Where(found.ContainsKey).Select(c => found[c])], ct)
            .ConfigureAwait(false);

        var importIds = new List<int>();
        foreach (var code in script.Imports)
        {
            if (await drafts.FindByCodeAsync(code, ct).ConfigureAwait(false) is { } library)
            {
                importIds.Add(library.Id);
            }
        }

        await methodologies.ReplaceScriptImportsAsync(versionId, importIds, ct).ConfigureAwait(false);
        await methodologies.ReplaceScriptReadTablesAsync(versionId, script.ReadTables, ct).ConfigureAwait(false);

        var dependencies = importIds
            .Concat(environment.ResultSourceMethodologyIds)
            .Where(id => id != methodologyId)
            .Distinct()
            .Order()
            .ToList();

        if (!options.Publish)
        {
            return new ScriptMaterialization(0, [], dependencies);
        }

        var (added, bound) = await BindOutputsAsync(methodologyId, script, environment, ct).ConfigureAwait(false);
        await methodologies.ReplaceDependenciesAsync(methodologyId, dependencies, ct).ConfigureAwait(false);
        await ReplaceRegistryUsesAsync(versionId, script, ct).ConfigureAwait(false);

        return new ScriptMaterialization(added, bound, dependencies);
    }

    /// <summary>
    /// Додає відсутні активні прив'язки <c>(колонка, методологія, вихід)</c> для колонок <c>Calculated</c> живих
    /// таблиць: код колонки дорівнює коду виходу або (місячна колонка) <c>&lt;вихід&gt;_M&lt;mm&gt;</c>.
    /// Наявні прив'язки не змінюються й не видаляються.
    /// </summary>
    private async Task<(int Added, IReadOnlyList<string> Bound)> BindOutputsAsync(
        int methodologyId, CompiledScript script, ScriptEnvironment environment, CancellationToken ct)
    {
        var existing = await bindings.ListAsync(methodologyId, ct).ConfigureAwait(false);
        var bound = new HashSet<string>(
            existing.Where(b => b.IsActive).Select(b => b.OutputCode), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var table in environment.RowTables)
        {
            foreach (var output in script.Outputs)
            {
                var columns = table.Columns.Where(c =>
                    c.DataType == CellDataType.Calculated
                    && (string.Equals(c.Code, output.Code, StringComparison.OrdinalIgnoreCase)
                        || (c.IsMonthColumn
                            && c.MonthNumber is { } month
                            && string.Equals(
                                c.Code,
                                output.Code + "_M" + month.ToString("00", CultureInfo.InvariantCulture),
                                StringComparison.OrdinalIgnoreCase))));

                foreach (var column in columns)
                {
                    if (existing.Any(b => b.ColumnDefId == column.Id
                                          && b.MethodologyId == methodologyId
                                          && string.Equals(b.OutputCode, output.Code, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    bindings.Add(new CalculationBinding(table.TableDefId, column.Id, methodologyId, output.Code, "{}"));
                    bound.Add(output.Code);
                    added++;
                }
            }
        }

        return (added, [.. script.Outputs.Where(o => bound.Contains(o.Code)).Select(o => o.Code)]);
    }

    private async Task ReplaceRegistryUsesAsync(int versionId, CompiledScript script, CancellationToken ct)
    {
        if (registryUses is null || registries is null)
        {
            return;
        }

        var uses = new List<RegistryUse>();
        foreach (var code in script.RegistryCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false) is { } definition)
            {
                uses.Add(RegistryUse.ForMethodologyFormula(versionId, RegistryUseSource, definition.Id, fieldPath: null));
            }
        }

        await registryUses.ReplaceMethodologyUsesAsync(versionId, uses, ct).ConfigureAwait(false);
    }
}
