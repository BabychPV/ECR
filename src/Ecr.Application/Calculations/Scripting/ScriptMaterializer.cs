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
            : await ResolveSubstancesAsync(versionId, methodologyId, script.Substances, ct).ConfigureAwait(false);
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

        // ✎ D-568: поля власної таблиці, які читає скрипт (прохід читача — RecalculationJob.ReaderPeriodsOfOwnersAsync).
        await methodologies.ReplaceScriptReadFieldsAsync(versionId, ScriptReadFields.Of(script), ct).ConfigureAwait(false);
        await ReplaceRegistryUsesAsync(versionId, script, ct).ConfigureAwait(false);

        return new ScriptMaterialization(added, bound, dependencies);
    }

    /// <summary>
    /// Код речовини скрипта → запис довідника: спершу речовини, які версія ВЖЕ має (клон джерела чи попереднє збереження), лише
    /// решта — кандидатами <see cref="IMethodologyStore.FindSubstanceCandidatesAsync"/> через <see cref="ScriptSubstanceResolver"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ c11-10: той самий код буває в кількох довідниках речовин (LAND_HSE400_SUBSTANCE «C333» і LAND_AIR_SUBSTANCE «E333» мають
    /// SUBSTANCE_CODE 333), а колишній пошук брав запис із меншим Id. Збереження тексту клону мігрованої версії пересаджувало її
    /// речовини на чужий довідник: виходи писались би з іншими SubstanceEntryId, золотий набір (очікування за E333…) червонів
    /// «виходу не було», а звіти за речовиною лишались би порожніми. Неоднозначний код тут не береться: його вже назвала
    /// перевірка оточення (<c>ambiguousSubstance</c>), і матеріалізація з діагностиками не йде.
    /// </remarks>
    private async Task<Dictionary<string, long>> ResolveSubstancesAsync(
        int versionId, int methodologyId, IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        var current = await methodologies.GetSubstanceCodesAsync(versionId, ct).ConfigureAwait(false);
        var candidates = await methodologies.FindSubstanceCandidatesAsync(codes, methodologyId, ct).ConfigureAwait(false);
        return new Dictionary<string, long>(
            ScriptSubstanceResolver.Resolve(codes, current, candidates).Found, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Порівнює те, що дала б матеріалізація скрипта (виходи, речовини, імпорти), з ТИМ, що вже лежить у версії
    /// (<c>calc.MethodologyOutput</c>, <c>calc.MethodologySubstance</c>, <c>calc.MethodologyImport</c>). Нічого не пише.
    /// </summary>
    /// <remarks>
    /// Перемикання опублікованої версії на скрипт (MS, план 8.5) не має права змінити склад версії: інакше змінилися б
    /// колонки, які вона пише, чи речовини, за якими рахує. Виходи порівнюються за (код без регістру, одиниця,
    /// «по речовинах»); речовини й імпорти — за кодами без регістру.
    /// </remarks>
    /// <param name="versionId">Версія.</param>
    /// <param name="script">Скомпільований скрипт версії.</param>
    /// <param name="importsOnDate">Дата, на яку читаються оголошені імпорти (на склад не впливає).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="moduleLevel">
    /// Версія рівня <c>Module</c>: скрипт може мати виходів більше, ніж збережено (див. коментар у тілі); зайвими вважаються
    /// лише збережені виходи, яких скрипт не дає.
    /// </param>
    /// <returns>Проблеми: порожньо — склад збігається.</returns>
    public async Task<IReadOnlyList<PublishProblem>> DiffAgainstStoredAsync(
        int versionId, CompiledScript script, DateOnly importsOnDate, CancellationToken ct, bool moduleLevel = false)
    {
        ArgumentNullException.ThrowIfNull(script);

        var problems = new List<PublishProblem>();
        var catalog = await units.GetAsync(ct).ConfigureAwait(false);

        var actualSubstances = (await methodologies.GetSubstanceCodesAsync(versionId, ct).ConfigureAwait(false)).Values.ToList();

        // Версія без речовин: ознака «по речовинах» нічого не означає (виходу немає за чим множитися), а сховище
        // версій, створених через API/збирач, часто тримає її true за замовчуванням, тоді як скрипт без `foreach
        // substance` її не має. Без цього кожне таке перемикання відмовляло б через різницю, що не змінює жодного числа.
        var perSubstanceMatters = script.Substances.Count > 0 || actualSubstances.Count > 0;
        var expectedOutputs = script.Outputs
            .Select(o => OutputKey(o.Code, catalog.Units.TryGetValue(o.UnitCode, out var unit) ? unit.Id : -1, perSubstanceMatters && o.IsPerSubstance))
            .ToList();
        var actualOutputs = (await methodologies.GetOutputsAsync(versionId, ct).ConfigureAwait(false))
            .Select(o => OutputKey(o.Code, o.UnitId, perSubstanceMatters && o.IsPerSubstance))
            .ToList();
        // Рівень Module: скрипт оголошує вихід для КОЖНОЇ формули (по рядках F_… їх розкладає модуль), а збережена версія
        // тримає лише виходи, прив'язані до якорів таблиць, і виходи золотих тестів. Тож скрипт може мати більше виходів,
        // ніж збережено; а збережений вихід, якого скрипт не дає (чи з іншою одиницею), - справжня розбіжність.
        AddDifference(problems, "switch.problem.outputsDiffer", "outputs", versionId, expectedOutputs, actualOutputs, ignoreOnlyInScript: moduleLevel);

        AddDifference(problems, "switch.problem.substancesDiffer", "substances", versionId, script.Substances, actualSubstances);

        var actualImports = (await methodologies.ResolveImportsAsync(versionId, importsOnDate, ct).ConfigureAwait(false))
            .Select(i => i.MethodologyCode)
            .ToList();
        AddDifference(problems, "switch.problem.importsDiffer", "imports", versionId, script.Imports, actualImports);

        return problems;
    }

    /// <summary>
    /// Записує те, що перемикання на скрипт додає до версії: таблиці читання, залежності від методологій-джерел і
    /// ребра довідників. Виходи, речовини й імпорти вже збігаються (<see cref="DiffAgainstStoredAsync"/>) і не
    /// переписуються; прив'язки виходів не змінюються.
    /// </summary>
    /// <param name="versionId">Версія.</param>
    /// <param name="methodologyId">Методологія-власник.</param>
    /// <param name="script">Скомпільований скрипт.</param>
    /// <param name="environment">Результат перевірки оточення.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task WriteForSwitchAsync(
        int versionId, int methodologyId, CompiledScript script, ScriptEnvironment environment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(environment);

        await methodologies.ReplaceScriptReadTablesAsync(versionId, script.ReadTables, ct).ConfigureAwait(false);

        var importIds = new List<int>();
        foreach (var code in script.Imports)
        {
            if (await drafts.FindByCodeAsync(code, ct).ConfigureAwait(false) is { } library)
            {
                importIds.Add(library.Id);
            }
        }

        var dependencies = importIds
            .Concat(environment.ResultSourceMethodologyIds)
            .Where(id => id != methodologyId)
            .Distinct()
            .Order()
            .ToList();
        await methodologies.ReplaceDependenciesAsync(methodologyId, dependencies, ct).ConfigureAwait(false);
        await ReplaceRegistryUsesAsync(versionId, script, ct).ConfigureAwait(false);

        // ✎ D-568: поля власної таблиці, які читає скрипт.
        await methodologies.ReplaceScriptReadFieldsAsync(versionId, ScriptReadFields.Of(script), ct).ConfigureAwait(false);
    }

    private static string OutputKey(string code, int unitId, bool perSubstance)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{code.ToUpperInvariant()}[unit {unitId}{(perSubstance ? ", per substance" : string.Empty)}]");

    private static void AddDifference(
        List<PublishProblem> problems,
        string messageKey,
        string what,
        int versionId,
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> actual,
        bool ignoreOnlyInScript = false)
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        var actualSet = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);
        var fromScript = ignoreOnlyInScript
            ? []
            : expectedSet.Where(x => !actualSet.Contains(x)).Order(StringComparer.Ordinal).ToList();
        var stored = actualSet.Where(x => !expectedSet.Contains(x)).Order(StringComparer.Ordinal).ToList();
        if (fromScript.Count == 0 && stored.Count == 0)
        {
            return;
        }

        problems.Add(Calculations.PublishProblem.Of(
            messageKey,
            $"Version {versionId.ToString(CultureInfo.InvariantCulture)}: the {what} derived from the script differ from the stored ones "
            + $"(only in the script: {Join(fromScript)}; only stored: {Join(stored)}).",
            ("version", versionId.ToString(CultureInfo.InvariantCulture)),
            ("onlyInScript", Join(fromScript)),
            ("onlyStored", Join(stored))));
    }

    private static string Join(List<string> items) => items.Count == 0 ? "-" : string.Join(", ", items);

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
