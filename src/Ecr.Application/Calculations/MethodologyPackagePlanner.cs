// src/Ecr.Application/Calculations/MethodologyPackagePlanner.cs
using System.Globalization;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.Services.External;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>Формула версії так, як її порівнює імпорт.</summary>
/// <param name="Code">Код.</param>
/// <param name="Expression">Вираз.</param>
/// <param name="ArgumentsCsv">Оголошені аргументи як є.</param>
/// <param name="ResultType">Тип результату; пакет без позначки — <see cref="FormulaResultType.Number"/>.</param>
/// <param name="Scope">Область формули (L2-1); пакет без позначки — <see cref="MethodologyFormulaScope.Substance"/>.</param>
public sealed record ImportFormulaContent(
    string Code,
    string Expression,
    string? ArgumentsCsv,
    FormulaResultType ResultType = FormulaResultType.Number,
    MethodologyFormulaScope Scope = MethodologyFormulaScope.Substance);

/// <summary>Рядок константи версії так, як його порівнює імпорт.</summary>
/// <param name="Code">Код.</param>
/// <param name="Kind">Вид.</param>
/// <param name="Value">Число; <c>null</c> — текст або нерозібране число.</param>
/// <param name="TextValue">Текст або сирий рядок нерозібраного числа.</param>
/// <param name="UnitId">Одиниця числа.</param>
/// <param name="Category">Категорія; <c>null</c> — спільна.</param>
/// <param name="ValidFrom">Перший чинний день.</param>
/// <param name="ValidTo">Перший НЕчинний день.</param>
/// <param name="Source">Походження рядка.</param>
public sealed record ImportConstantContent(
    string Code,
    ConstantKind Kind,
    decimal? Value,
    string? TextValue,
    int? UnitId,
    string? Category,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Source);

/// <summary>Вміст версії методології, який імпорт створює або з яким звіряє наявну.</summary>
/// <param name="Formulas">Формули.</param>
/// <param name="Constants">Рядки констант.</param>
/// <param name="Imports">Коди методологій, чиї формули версія імпортує.</param>
/// <param name="CategoryRule">Вираз правила категорії константи (L-2); <c>null</c> — правила немає.</param>
public sealed record ImportVersionContent(
    IReadOnlyList<ImportFormulaContent> Formulas,
    IReadOnlyList<ImportConstantContent> Constants,
    IReadOnlyList<string> Imports,
    string? CategoryRule = null)
{
    /// <summary>Чи збігається вміст із іншим — без огляду на порядок.</summary>
    /// <param name="other">Інший вміст.</param>
    /// <returns><c>true</c> — ті самі формули, константи й імпорти.</returns>
    /// <remarks>
    /// ⚠ Число порівнюється без хвостових нулів: <c>decimal(38,18)</c> з бази повертає
    /// <c>1.500000000000000000</c>, а пакет — <c>1.5</c>; це те саме значення, і повторний
    /// імпорт мусить сказати «без змін», а не «конфлікт».
    /// </remarks>
    public bool SameAs(ImportVersionContent other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Keys(this).SequenceEqual(Keys(other), StringComparer.Ordinal);
    }

    private static List<string> Keys(ImportVersionContent content)
        => [
            .. content.Formulas
                .Select(f => string.Join(
                    '\u001f', "F", f.Code.ToUpperInvariant(), f.Expression, f.ArgumentsCsv ?? "\0", ((byte)f.ResultType).ToString(CultureInfo.InvariantCulture),
                    ((byte)f.Scope).ToString(CultureInfo.InvariantCulture)))
                .Order(StringComparer.Ordinal),
            .. content.Constants
                .Select(c => string.Join(
                    '\u001f',
                    "C",
                    c.Code.ToUpperInvariant(),
                    c.Kind.ToString(),
                    c.Value?.ToString("G29", CultureInfo.InvariantCulture) ?? "\0",
                    c.TextValue ?? "\0",
                    c.UnitId?.ToString(CultureInfo.InvariantCulture) ?? "\0",
                    c.Category ?? "\0",
                    c.ValidFrom?.ToString("O", CultureInfo.InvariantCulture) ?? "\0",
                    c.ValidTo?.ToString("O", CultureInfo.InvariantCulture) ?? "\0",
                    c.Source ?? "\0"))
                .Order(StringComparer.Ordinal),
            .. content.Imports.Select(i => "I\u001f" + i.ToUpperInvariant()).Order(StringComparer.Ordinal),
            .. content.CategoryRule is { } rule ? ["R\u001f" + rule] : Array.Empty<string>(),
        ];
}

/// <summary>Наявна в базі версія методології.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Version">Номер.</param>
/// <param name="IsDraft">Чернетка чи ні.</param>
/// <param name="Content">Вміст.</param>
public sealed record ExistingMethodologyVersion(int Id, string Version, bool IsDraft, ImportVersionContent Content);

/// <summary>Наявна в базі методологія.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код.</param>
/// <param name="Kind">Природа.</param>
/// <param name="Versions">Версії з вмістом.</param>
public sealed record ExistingMethodology(
    int Id, string Code, MethodologyKind Kind, IReadOnlyList<ExistingMethodologyVersion> Versions);

/// <summary>Версія, яку імпорт створить або залишить.</summary>
/// <param name="Version">Номер.</param>
/// <param name="Action"><c>create</c>, <c>unchanged</c>, <c>conflict</c>.</param>
/// <param name="ExistingVersionId">Наявна версія; <c>null</c> — нова.</param>
/// <param name="Content">Вміст із пакета.</param>
/// <param name="ConstantsFromLibrary">Скільки рядків констант узято з бібліотеки.</param>
public sealed record PlannedMethodologyVersion(
    string Version,
    string Action,
    int? ExistingVersionId,
    ImportVersionContent Content,
    int ConstantsFromLibrary);

/// <summary>Методологія, яку імпорт створить або доповнить.</summary>
/// <param name="Code">Код.</param>
/// <param name="Kind">Природа.</param>
/// <param name="ExistingId">Наявна методологія; <c>null</c> — нова.</param>
/// <param name="Versions">Версії.</param>
public sealed record PlannedMethodology(
    string Code, MethodologyKind Kind, int? ExistingId, IReadOnlyList<PlannedMethodologyVersion> Versions);

/// <summary>План імпорту пакета: що створити, що вже є, що заважає.</summary>
/// <param name="Methodologies">Методології в порядку пакета.</param>
/// <param name="Blockers">Блокери.</param>
/// <param name="Conflicts">Конфлікти з наявними версіями.</param>
/// <param name="Warnings">Попередження.</param>
public sealed record MethodologyImportPlan(
    IReadOnlyList<PlannedMethodology> Methodologies,
    IReadOnlyList<MethodologyImportIssueDto> Blockers,
    IReadOnlyList<MethodologyImportIssueDto> Conflicts,
    IReadOnlyList<MethodologyImportIssueDto> Warnings)
{
    /// <summary>Чи є що записати.</summary>
    public bool HasChanges => Methodologies.Any(m => m.Versions.Any(v => v.Action == MethodologyPackagePlanner.Create));

    /// <summary>Складає звіт плану.</summary>
    /// <param name="dryRun">Сухий прогін.</param>
    /// <param name="applied">Чи записано.</param>
    /// <param name="methodologyIds">Ідентифікатори методологій після запису.</param>
    /// <param name="versionIds">Ідентифікатори версій після запису.</param>
    /// <returns>Звіт.</returns>
    public MethodologyImportReportDto ToReport(
        bool dryRun,
        bool applied,
        IReadOnlyDictionary<string, int>? methodologyIds = null,
        IReadOnlyDictionary<(string, string), int>? versionIds = null)
    {
        var outcome = Blockers.Count > 0 ? "blocked"
            : Conflicts.Count > 0 ? "conflict"
            : HasChanges ? "created"
            : "unchanged";

        var created = Methodologies.SelectMany(m => m.Versions).Where(v => v.Action == MethodologyPackagePlanner.Create).ToList();

        return new MethodologyImportReportDto(
            dryRun,
            applied,
            outcome,
            [.. Methodologies.Select(m => new MethodologyImportMethodologyDto(
                m.Code,
                m.Kind,
                m.ExistingId is null ? MethodologyPackagePlanner.Create : "existing",
                m.ExistingId ?? (methodologyIds is not null && methodologyIds.TryGetValue(m.Code, out var mid) ? mid : null),
                [.. m.Versions.Select(v => new MethodologyImportVersionDto(
                    v.Version,
                    v.Action,
                    v.ExistingVersionId
                        ?? (versionIds is not null && versionIds.TryGetValue((m.Code, v.Version), out var vid) ? vid : null),
                    v.Content.Formulas.Count,
                    v.Content.Constants.Count,
                    v.ConstantsFromLibrary,
                    v.Content.Imports,
                    v.Content.CategoryRule is null
                        ? null
                        : v.Action switch { MethodologyPackagePlanner.Create => "added", "unchanged" => "unchanged", _ => "conflict" }))]))],
            Blockers,
            Conflicts,
            Warnings,
            new MethodologyImportTotalsDto(
                Methodologies.Count(m => m.ExistingId is null && m.Versions.Any(v => v.Action == MethodologyPackagePlanner.Create)),
                created.Count,
                Methodologies.SelectMany(m => m.Versions).Count(v => v.Action == "unchanged"),
                created.Sum(v => v.Content.Formulas.Count),
                created.Sum(v => v.Content.Constants.Count),
                created.Sum(v => v.Content.Imports.Count)));
    }
}

/// <summary>
/// Планувальник імпорту пакета методологій (крок V, FEATURE-HSE301-VIEW §11.6): пакет +
/// наявний стан бази → план і звіт. Нічого не пише.
/// </summary>
/// <remarks>
/// ⛔ Резолвінг той самий, що в аналізаторі інструмента: <c>!Формула</c> і <c>CST.Константа</c>
/// шукаються спершу у власній версії, потім у бібліотеці (<c>Library</c> пакета, усі її версії;
/// якщо бібліотеки в пакеті немає — наявна в базі). Не знайдено — блокер: ціль не вгадується.
/// <para>
/// ⚠ <c>!</c>-посилання в бібліотеку стає ІМПОРТОМ методології (<c>calc.MethodologyImport</c>),
/// а <c>CST.</c> у бібліотеку — КОПІЄЮ рядків константи у версію. Причина: публікація шукає
/// константи лише у власній версії (<c>CheckUnknownConstants</c>), і без копії кожна така
/// формула корпусу відхилялася б. Копія позначена <c>Source</c> і лічиться у звіті окремо.
/// </para>
/// <para>
/// ⚠ Кілька версій однієї формули AF в одній версії методології: у моделі продукту формула
/// одна на код, тож береться доступна з найпізнішим початком дії, решта — попередженням
/// <c>formulaVersionsCollapsed</c>, а не тихо.
/// </para>
/// </remarks>
public static class MethodologyPackagePlanner
{
    /// <summary>Назва формату.</summary>
    public const string FormatName = "ecr-methodology-package";

    /// <summary>Підтримувана версія формату.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Дія «створити».</summary>
    public const string Create = "create";

    /// <summary>Одиниця безрозмірного числа — підставляється, коли одиниці AF у каталозі немає.</summary>
    public const string DimensionlessUnit = "one";

    /// <summary>Модулі з власною процедурою CLR (<see cref="MethodologyKind.Bespoke"/>).</summary>
    private static readonly HashSet<string> BespokeModules =
        new(["HSE400", "Flert", "Thermaloxidizer"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Складає план.</summary>
    /// <param name="package">Пакет.</param>
    /// <param name="existing">Наявні методології за кодом (без огляду на регістр).</param>
    /// <param name="units">Каталог одиниць.</param>
    /// <param name="timeZone">Пояс майданчика: дати AF (UTC) переводяться в його дні.</param>
    /// <param name="formulaEngine">
    /// Парсер діалекту Methodology для перевірки виразів правил категорії (L-2); <c>null</c> — перевіряється
    /// лише непорожність і довжина (сухий прогін без рушія).
    /// </param>
    /// <returns>План.</returns>
    public static MethodologyImportPlan Plan(
        MethodologyPackageDto package,
        IReadOnlyDictionary<string, ExistingMethodology> existing,
        UnitCatalogSnapshot units,
        TimeZoneInfo timeZone,
        IFormulaEngine? formulaEngine = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(timeZone);

        var blockers = new List<MethodologyImportIssueDto>();
        var conflicts = new List<MethodologyImportIssueDto>();
        var warnings = new List<MethodologyImportIssueDto>();

        if (!string.Equals(package.Format, FormatName, StringComparison.Ordinal) || package.Version != SupportedVersion)
        {
            blockers.Add(new("format", null, null, null,
                $"Очікувано {FormatName} v{SupportedVersion}, отримано {package.Format} v{package.Version}."));

            return new MethodologyImportPlan([], blockers, conflicts, warnings);
        }

        foreach (var blocker in package.Blockers ?? [])
        {
            blockers.Add(new("packageBlocker", null, null, null, blocker));
        }

        var library = string.IsNullOrWhiteSpace(package.Library) ? "Common" : package.Library.Trim();
        var methodologies = package.Methodologies ?? [];

        // Коди — ДО всього: недопустимий код не створиться, і резолвінг по ньому не має сенсу.
        foreach (var m in methodologies)
        {
            RequireCode(m.Name, "methodology", m.Name, null, null, blockers);
            foreach (var v in m.Versions ?? [])
            {
                if (string.IsNullOrWhiteSpace(v.Version))
                {
                    blockers.Add(new("invalidVersion", m.Name, v.Version, null, "Порожній номер версії."));
                }
                else if (v.Version.Length > CreateMethodologyVersionHandler.MaxVersionLength)
                {
                    // ⚠ Межа колонки Version: без блокера сухий прогін казав `created`, а запис — 500 (аудит L7-09).
                    blockers.Add(new("invalidVersion", m.Name, v.Version, null,
                        $"Номер версії довший за {CreateMethodologyVersionHandler.MaxVersionLength} символів."));
                }

                foreach (var f in v.Formulas ?? [])
                {
                    RequireCode(f.Name, "formula", m.Name, v.Version, f.Name, blockers);
                    if ((f.Text?.Length ?? 0) > MethodologyFormula.MaxExpressionLength)
                    {
                        blockers.Add(new("formulaTooLong", m.Name, v.Version, f.Name,
                            $"Вираз довший за {MethodologyFormula.MaxExpressionLength} символів."));
                    }

                    if (!TryParseScope(f.Scope, out _))
                    {
                        blockers.Add(new("invalidFormulaScope", m.Name, v.Version, f.Name,
                            $"Область формули «{f.Scope}» невідома: допустимі Substance та Row."));
                    }
                }

                foreach (var c in v.Constants ?? [])
                {
                    RequireCode(c.Name, "constant", m.Name, v.Version, c.Name, blockers);
                }

                CheckCategoryRule(v.CategoryRule, m.Name, v.Version, formulaEngine, blockers);
            }
        }

        foreach (var duplicate in methodologies.GroupBy(m => m.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            blockers.Add(new("duplicateMethodology", duplicate.Key, null, null, "Методологія двічі в пакеті."));
        }

        if (blockers.Count > 0)
        {
            return new MethodologyImportPlan([], blockers, conflicts, warnings);
        }

        var libraryInPackage = methodologies.FirstOrDefault(m => string.Equals(m.Name, library, StringComparison.OrdinalIgnoreCase));
        existing.TryGetValue(library, out var libraryInDb);

        // Формули бібліотеки: ім'я → чи є. Константи бібліотеки: ім'я → рядки найпізнішої версії, що її має.
        var libraryFormulas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraryConstants = new Dictionary<string, (string Version, Func<IReadOnlyList<ImportConstantContent>> Rows)>(StringComparer.OrdinalIgnoreCase);

        if (libraryInPackage is not null)
        {
            // ⚠ «Найпізніша» — за числовими компонентами номера (V10 > V9, 1.10 > 1.9), тим самим
            // правилом, що й для бази нижче (аудит L7-12): порядкове сортування ставило V10 перед V9.
            foreach (var v in (libraryInPackage.Versions ?? []).OrderBy(v => v.Version, VersionNumberComparer.Instance))
            {
                libraryFormulas.UnionWith((v.Formulas ?? []).Select(f => f.Name.Trim()));
                foreach (var c in v.Constants ?? [])
                {
                    var version = v;
                    var constant = c;
                    libraryConstants[c.Name.Trim()] = (v.Version, () => ConstantRows(
                        libraryInPackage.Name, version.Version, constant, referenced: true, units, timeZone, null));
                }
            }
        }
        else if (libraryInDb is not null)
        {
            // ⚠ Чернетки лишаються: імпорт створює лише чернетки, і бібліотека з попереднього пакета
            // інакше була б невидимою. Порядок — той самий, що для пакета (аудит L7-12), а не Id.
            foreach (var v in libraryInDb.Versions.OrderBy(v => v.Version, VersionNumberComparer.Instance).ThenBy(v => v.Id))
            {
                libraryFormulas.UnionWith(v.Content.Formulas.Select(f => f.Code));
                foreach (var group in v.Content.Constants.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase))
                {
                    var rows = group.ToList();
                    libraryConstants[group.Key] = (v.Version, () => rows);
                }
            }
        }

        var libraryCode = libraryInPackage?.Name ?? libraryInDb?.Code;
        var planned = new List<PlannedMethodology>();

        foreach (var m in methodologies)
        {
            existing.TryGetValue(m.Name, out var current);
            var isLibrary = string.Equals(m.Name, libraryCode, StringComparison.OrdinalIgnoreCase);
            var kind = current?.Kind
                ?? (isLibrary ? MethodologyKind.Library
                    : BespokeModules.Contains(m.Name) ? MethodologyKind.Bespoke
                    : MethodologyKind.DataDriven);

            var versions = new List<PlannedMethodologyVersion>();

            foreach (var v in m.Versions ?? [])
            {
                var formulas = Collapse(m.Name, v, warnings);
                var ownFormulas = new HashSet<string>(formulas.Select(f => f.Name.Trim()), StringComparer.OrdinalIgnoreCase);
                var ownConstants = new HashSet<string>((v.Constants ?? []).Select(c => c.Name.Trim()), StringComparer.OrdinalIgnoreCase);

                var imports = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var referencedConstants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fromLibrary = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in formulas)
                {
                    foreach (var token in Ecr.Expressions.Binding.ArgumentDeclarationChecker.ParseDeclaration(f.Arguments))
                    {
                        if (token.StartsWith('!'))
                        {
                            var name = token[1..].Trim();
                            if (ownFormulas.Contains(name))
                            {
                                continue;
                            }

                            if (!isLibrary && libraryCode is not null && libraryFormulas.Contains(name))
                            {
                                imports.Add(libraryCode);
                                continue;
                            }

                            blockers.Add(new("unresolvedFormula", m.Name, v.Version, f.Name, token));
                        }
                        else if (token.StartsWith("CST.", StringComparison.OrdinalIgnoreCase))
                        {
                            var name = token[4..].Trim();
                            referencedConstants.Add(name);
                            if (ownConstants.Contains(name))
                            {
                                continue;
                            }

                            if (libraryConstants.ContainsKey(name))
                            {
                                fromLibrary.Add(name);
                                continue;
                            }

                            blockers.Add(new("unresolvedConstant", m.Name, v.Version, f.Name, token));
                        }
                    }

                    if (string.IsNullOrWhiteSpace(f.Text))
                    {
                        blockers.Add(new("emptyFormula", m.Name, v.Version, f.Name, "Формула без виразу."));
                    }
                }

                var constants = new List<ImportConstantContent>();
                foreach (var c in v.Constants ?? [])
                {
                    constants.AddRange(ConstantRows(
                        m.Name, v.Version, c, referencedConstants.Contains(c.Name.Trim()), units, timeZone, (blockers, warnings)));
                }

                var copied = 0;
                foreach (var name in fromLibrary)
                {
                    var (libraryVersion, rows) = libraryConstants[name];
                    var source = $"AF {libraryCode}/{libraryVersion}";
                    foreach (var row in rows())
                    {
                        // Текст бібліотеки, на який посилається формула, — завжди Text, не мітка.
                        var kindHere = row.Kind == ConstantKind.CategoryLabel ? ConstantKind.Text : row.Kind;
                        constants.Add(row with { Kind = kindHere, Source = source });
                        copied++;
                    }
                }

                if (copied > 0)
                {
                    warnings.Add(new("constantsFromLibrary", m.Name, v.Version, null,
                        $"{fromLibrary.Count} констант ({copied} рядків) скопійовано з {libraryCode}: {string.Join(", ", fromLibrary)}."));
                }

                var content = new ImportVersionContent(
                    [.. formulas.Select(f => new ImportFormulaContent(
                        f.Name.Trim(), f.Text ?? string.Empty, f.Arguments,
                        string.Equals(f.ResultType?.Trim(), "Text", StringComparison.OrdinalIgnoreCase)
                            ? FormulaResultType.Text
                            : FormulaResultType.Number,
                        TryParseScope(f.Scope, out var scope) ? scope : MethodologyFormulaScope.Substance))],
                    constants,
                    [.. imports],
                    v.CategoryRule?.Expression?.Trim());

                var match = current?.Versions.FirstOrDefault(x => string.Equals(x.Version, v.Version, StringComparison.Ordinal));
                var action = Create;
                if (match is not null)
                {
                    if (match.Content.SameAs(content))
                    {
                        action = "unchanged";
                    }
                    else
                    {
                        action = "conflict";
                        conflicts.Add(new(
                            match.IsDraft ? "draftDiffers" : "publishedDiffers",
                            m.Name,
                            v.Version,
                            null,
                            match.IsDraft
                                ? "Чернетка з цим номером уже є і має інший вміст; імпорт її не переписує."
                                : "Опублікована версія з цим номером має інший вміст; вона незмінна — дайте версії новий номер."));
                    }
                }

                versions.Add(new PlannedMethodologyVersion(v.Version, action, match?.Id, content, copied));
            }

            foreach (var duplicate in (m.Versions ?? []).GroupBy(v => v.Version, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                blockers.Add(new("duplicateVersion", m.Name, duplicate.Key, null, "Версія двічі в пакеті."));
            }

            planned.Add(new PlannedMethodology(m.Name.Trim(), kind, current?.Id, versions));
        }

        if (!units.Units.ContainsKey(DimensionlessUnit)
            && planned.Any(p => p.Versions.Any(v => v.Content.Constants.Any(c => c.Kind == ConstantKind.Numeric && c.UnitId is null))))
        {
            blockers.Add(new("unitCatalog", null, null, null, $"У каталозі немає одиниці «{DimensionlessUnit}»."));
        }

        return new MethodologyImportPlan(planned, blockers, conflicts, warnings);
    }

    /// <summary>Область формули з рядка пакета (L2-1): порожнє - <c>Substance</c>, імена - без регістру.</summary>
    /// <param name="text">Рядок пакета; <c>null</c> або порожній - область не задано.</param>
    /// <param name="scope">Область.</param>
    /// <returns><c>false</c> - значення невідоме.</returns>
    /// <remarks>
    /// ⚠ Лише ІМ'Я переліку: <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> приймає й число
    /// («1», «77»), а пакет — формат обміну, де число замість імені є помилкою експортера.
    /// </remarks>
    private static bool TryParseScope(string? text, out MethodologyFormulaScope scope)
    {
        scope = MethodologyFormulaScope.Substance;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        return char.IsLetter(trimmed[0])
            && Enum.TryParse(trimmed, ignoreCase: true, out scope)
            && Enum.IsDefined(scope);
    }

    /// <summary>Вузол <c>categoryRule</c> версії пакета:непорожній, не задовгий, розбирається, не число (L-2).</summary>
    /// <param name="rule">Вузол пакета; <c>null</c> — правила в пакеті немає.</param>
    /// <param name="methodology">Методологія для рядка звіту.</param>
    /// <param name="version">Версія для рядка звіту.</param>
    /// <param name="formulaEngine">Парсер діалекту Methodology; <c>null</c> — розбір пропускається.</param>
    /// <param name="blockers">Куди складати блокери.</param>
    /// <remarks>
    /// ⚠ Вузол є, а виразу немає — блокер, а не мовчазне «правила немає»: інакше пакет, у якому правило
    /// забули заповнити, створював би версію, що публікується з <c>constantAmbiguous</c> на кожному рядку.
    /// </remarks>
    private static void CheckCategoryRule(
        MethodologyPackageCategoryRuleDto? rule,
        string methodology,
        string version,
        IFormulaEngine? formulaEngine,
        List<MethodologyImportIssueDto> blockers)
    {
        if (rule is null)
        {
            return;
        }

        var expression = rule.Expression?.Trim();
        if (string.IsNullOrEmpty(expression))
        {
            blockers.Add(new("categoryRuleEmpty", methodology, version, "categoryRule", "Порожній вираз правила категорії."));
            return;
        }

        if (expression.Length > MethodologyFormula.MaxExpressionLength)
        {
            blockers.Add(new("categoryRuleTooLong", methodology, version, "categoryRule",
                $"Вираз довший за {MethodologyFormula.MaxExpressionLength} символів."));
            return;
        }

        if (formulaEngine is null)
        {
            return;
        }

        var parsed = formulaEngine.Parse(expression, ExpressionDialect.Methodology);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            blockers.Add(new("categoryRuleInvalid", methodology, version, "categoryRule",
                parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : "Вираз не розбирається."));
        }
        else if (parsed.Expression.ResultType is Ecr.Expressions.Ast.ExpressionValueType.Number
                 or Ecr.Expressions.Ast.ExpressionValueType.Boolean or Ecr.Expressions.Ast.ExpressionValueType.Date)
        {
            blockers.Add(new("categoryRuleNotText", methodology, version, "categoryRule",
                "Правило категорії повертає не текст: ключ категорії — текст."));
        }
    }

    /// <summary>Одна формула на код: доступна з найпізнішим початком дії.</summary>
    private static List<MethodologyPackageFormulaDto> Collapse(
        string methodology, MethodologyPackageVersionDto version, List<MethodologyImportIssueDto> warnings)
    {
        var result = new List<MethodologyPackageFormulaDto>();
        foreach (var group in (version.Formulas ?? []).GroupBy(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(f => f.IsAvailable)
                .ThenByDescending(f => ParseInstant(f.StartDate) ?? DateTimeOffset.MinValue)
                .ThenByDescending(f => f.Version, StringComparer.Ordinal)
                .ToList();

            result.Add(ordered[0]);

            if (ordered.Count > 1)
            {
                warnings.Add(new("formulaVersionsCollapsed", methodology, version.Version, group.Key,
                    $"Узято версію формули {ordered[0].Version}; відкинуто: {string.Join(", ", ordered.Skip(1).Select(f => f.Version))}."));
            }
        }

        return result;
    }

    /// <summary>Рядки константи: по одному на значення.</summary>
    private static List<ImportConstantContent> ConstantRows(
        string methodology,
        string version,
        MethodologyPackageConstantDto constant,
        bool referenced,
        UnitCatalogSnapshot units,
        TimeZoneInfo timeZone,
        (List<MethodologyImportIssueDto> Blockers, List<MethodologyImportIssueDto> Warnings)? issues)
    {
        var code = constant.Name.Trim();
        var unitText = constant.Unit?.Trim() ?? string.Empty;
        var unitId = ResolveUnit(unitText, units);
        var rows = new List<ImportConstantContent>();
        var unitWarned = false;

        foreach (var value in constant.Values ?? [])
        {
            var raw = value.Value;
            var isNumber = MethodologyConstant.TryParseNumeric(raw, out var parsed);
            var numeric = isNumber || unitText.Length > 0 || string.IsNullOrWhiteSpace(raw);

            ConstantKind kind;
            if (numeric)
            {
                kind = ConstantKind.Numeric;
                if (unitId is null && !unitWarned)
                {
                    // Раз на константу, а не на кожне її значення.
                    unitWarned = true;
                    issues?.Warnings.Add(new(
                        unitText.Length == 0 ? "unitDefaulted" : "unitUnknown",
                        methodology, version, code,
                        $"Одиниця «{unitText}» → «{DimensionlessUnit}»."));
                }

                if (!isNumber)
                {
                    issues?.Warnings.Add(new("constantNotNumber", methodology, version, code,
                        $"Значення «{raw}» не число: збережено як є, публікація його назве."));
                }
            }
            else
            {
                kind = MethodologyConstant.ClassifyText(referenced);
            }

            var from = Boundary(value.StartDate, timeZone, upper: false, methodology, version, code, issues);
            var to = Boundary(value.EndDate, timeZone, upper: true, methodology, version, code, issues);
            var category = string.IsNullOrWhiteSpace(value.Category) ? null : value.Category.Trim();

            rows.Add(new ImportConstantContent(
                code,
                kind,
                kind == ConstantKind.Numeric && isNumber ? parsed : null,
                kind == ConstantKind.Numeric && isNumber ? null : raw ?? string.Empty,
                kind == ConstantKind.Numeric ? unitId ?? DimensionlessId(units) : null,
                category,
                from,
                to,
                $"AF {methodology}/{version}"));
        }

        return rows;
    }

    private static int? DimensionlessId(UnitCatalogSnapshot units)
        => units.Units.TryGetValue(DimensionlessUnit, out var one) ? one.Id : null;

    /// <summary>Одиниця AF → одиниця каталогу за кодом; <c>/</c> читається як <c>_per_</c>.</summary>
    private static int? ResolveUnit(string unit, UnitCatalogSnapshot units)
    {
        if (unit.Length == 0)
        {
            return null;
        }

        if (units.Units.TryGetValue(unit, out var exact))
        {
            return exact.Id;
        }

        var normalized = unit.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("/", "_per_", StringComparison.Ordinal);

        if (units.Units.TryGetValue(normalized, out var per))
        {
            return per.Id;
        }

        // Регістр і нерозривні пробіли в AF не різняться за змістом (`KG`, `kg`), а `Sm3` ≠ `Nm3` лишаються різними кодами.
        var folded = normalized.Replace('\u00A0', ' ').Replace(" ", string.Empty, StringComparison.Ordinal);
        var matches = units.Units.Where(kv => string.Equals(kv.Key, folded, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value.Id).Distinct().ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Межа чинності: UTC AF → день майданчика → півінтервал (<see cref="LegacyValidityImport"/>).</summary>
    private static DateOnly? Boundary(
        string? text,
        TimeZoneInfo timeZone,
        bool upper,
        string methodology,
        string version,
        string code,
        (List<MethodologyImportIssueDto> Blockers, List<MethodologyImportIssueDto> Warnings)? issues)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (ParseInstant(text) is not { } instant)
        {
            issues?.Blockers.Add(new("invalidDate", methodology, version, code, text));
            return null;
        }

        // ⚠ Сентинел 9999-… лишається сентинелом і в UTC, і після переводу в пояс
        // (9999-02-19T19:00Z → 9999-02-20 00:00): рік перевіряє перенос.
        var local = instant.Year >= LegacyValidityImport.SentinelYear
            ? instant.UtcDateTime
            : TimeZoneInfo.ConvertTime(instant, timeZone).DateTime;

        var mapped = upper ? LegacyValidityImport.MapUpperBound(local) : LegacyValidityImport.MapLowerBound(local);

        if (mapped.NeedsReport)
        {
            issues?.Warnings.Add(new("boundaryShifted", methodology, version, code,
                $"Межу {text} зсунуто на {mapped.Shift} до {mapped.Boundary:yyyy-MM-dd}."));
        }

        return mapped.Boundary;
    }

    private static DateTimeOffset? ParseInstant(string? text)
        => DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var instant)
            ? instant
            : null;

    private static void RequireCode(
        string? value, string what, string? methodology, string? version, string? subject, List<MethodologyImportIssueDto> blockers)
    {
        if (!EcrCode.TryCreate(value?.Trim(), out _))
        {
            blockers.Add(new("invalidCode", methodology, version, subject,
                $"Код {what} «{value}»: дозволені латинські літери, цифри й підкреслення, перший символ — літера, до 64."));
        }
    }
}

/// <summary>
/// Порівняння номерів версій методології за числовими компонентами: цифрові відрізки — як
/// числа, решта — без урахування регістру (аудит L7-12).
/// </summary>
/// <remarks>
/// ⛔ Порядкове порівняння рядків ставить <c>V10</c> перед <c>V9</c> і <c>1.10.0.0</c> перед
/// <c>1.9.0.0</c>. За рівних компонентів (<c>V01</c> і <c>V1</c>) — порядково, аби відповідь
/// була сталою.
/// </remarks>
internal sealed class VersionNumberComparer : IComparer<string?>
{
    /// <summary>Єдиний екземпляр.</summary>
    public static VersionNumberComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            var xDigit = char.IsAsciiDigit(x[i]);
            var yDigit = char.IsAsciiDigit(y[j]);
            if (xDigit && yDigit)
            {
                var xEnd = DigitsEnd(x, i);
                var yEnd = DigitsEnd(y, j);

                var xNumber = x.AsSpan(i, xEnd - i).TrimStart('0');
                var yNumber = y.AsSpan(j, yEnd - j).TrimStart('0');
                var byNumber = xNumber.Length != yNumber.Length
                    ? xNumber.Length.CompareTo(yNumber.Length)
                    : xNumber.CompareTo(yNumber, StringComparison.Ordinal);
                if (byNumber != 0)
                {
                    return byNumber;
                }

                i = xEnd;
                j = yEnd;
                continue;
            }

            var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0)
            {
                return xDigit != yDigit ? (xDigit ? -1 : 1) : byChar;
            }

            i++;
            j++;
        }

        var byRest = (x.Length - i).CompareTo(y.Length - j);
        return byRest != 0 ? byRest : string.CompareOrdinal(x, y);
    }

    private static int DigitsEnd(string value, int start)
    {
        var end = start;
        while (end < value.Length && char.IsAsciiDigit(value[end]))
        {
            end++;
        }

        return end;
    }
}
