// src/Ecr.Application/Calculations/Scripting/Migration/ImportClosureSource.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Expressions.Binding;

namespace Ecr.Application.Calculations.Scripting.Migration;

/// <summary>
/// Склад імпортів версії ТАК, як його бачить старий рушій: не «усе, що лежить у версії бібліотеки», а лише те, що
/// досяжне з формул версії-викликача (<see cref="MethodologyLibraryClosure"/>, той самий обхід, що й у
/// <c>GenericCalculationModule.PrepareAsync</c>).
/// </summary>
/// <remarks>
/// Старий рушій бере маркери необов'язковості (<c>OptionalArguments.Of</c>) лише з ПОТРІБНИХ формул бібліотек, а «відомі
/// категорії» (<c>KnownCategoriesOf</c>) — з констант версій бібліотек, у які веде хоч одне потрібне ім'я (транзитивно, і
/// через бібліотеки бібліотек). Бібліотека, на яку формули версії не посилаються, не дає ні того, ні того. Тому для
/// кожного прямого імпорту тут зібрано об'єднання по всьому його піддереву замикання.
/// </remarks>
public static class ImportClosureSource
{
    /// <summary>Будує імпорти версії для перетворювача й відбитка.</summary>
    /// <param name="store">Сховище методологій.</param>
    /// <param name="engine">Рушій формул: розбір виразів для обходу посилань.</param>
    /// <param name="methodologyId">Методологія викликача.</param>
    /// <param name="versionId">Версія викликача.</param>
    /// <param name="orderedFormulas">Формули версії-викликача.</param>
    /// <param name="onDate">Дата, на яку обрано версії бібліотек.</param>
    /// <param name="direct">Прямі імпорти версії (<see cref="IMethodologyStore.ResolveImportsAsync"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<SourceImport>> BuildAsync(
        IMethodologyStore store,
        IFormulaEngine engine,
        int methodologyId,
        int versionId,
        IReadOnlyList<MethodologyFormula> orderedFormulas,
        DateOnly onDate,
        IReadOnlyList<MethodologyLibrary> direct,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(orderedFormulas);
        ArgumentNullException.ThrowIfNull(direct);

        var closure = direct.Count == 0
            ? null
            : await MethodologyLibraryClosure
                .LoadAsync(store, engine, methodologyId, versionId, orderedFormulas, onDate, ct)
                .ConfigureAwait(false);
        var byVersion = closure?.Versions.ToDictionary(v => v.MethodologyVersionId) ?? [];

        var imports = new List<SourceImport>(direct.Count);
        foreach (var library in direct)
        {
            var categories = new SortedSet<string>(StringComparer.Ordinal);
            var optional = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            if (library.MethodologyVersionId is { } start)
            {
                var visited = new HashSet<int>();
                var pending = new Stack<int>();
                pending.Push(start);
                while (pending.TryPop(out var current))
                {
                    if (!visited.Add(current) || !byVersion.TryGetValue(current, out var node))
                    {
                        continue;
                    }

                    foreach (var constant in node.Constants.Values.SelectMany(c => c))
                    {
                        if (!string.IsNullOrEmpty(constant.Category))
                        {
                            categories.Add(constant.Category);
                        }
                    }

                    foreach (var formula in node.Formulas)
                    {
                        if (formula.ArgumentsCsv is { Length: > 0 } csv)
                        {
                            optional.UnionWith(ArgumentDeclarationChecker.OptionalNamesFromCsv(csv));
                        }
                    }

                    foreach (var link in node.Imports.Values.Where(l => !l.IsCycle))
                    {
                        pending.Push(link.MethodologyVersionId);
                    }
                }
            }

            imports.Add(new SourceImport(library.MethodologyCode, [.. library.FormulaCodes], [.. categories], [.. optional]));
        }

        return imports;
    }

    /// <summary>
    /// Імпорти, чий склад (категорії констант і маркери <c>Name?</c>) на іншу дату відрізняється від базового. Скрипт
    /// переносить цей склад у текст ОДИН раз (правило «відомих категорій», <c>row.X?</c>), а старий рушій бере його заново
    /// з версії бібліотеки кожного періоду, тож розбіжність означає, що точного переносу одним текстом немає.
    /// </summary>
    /// <param name="baseline">Склад на дату чинності версії-імпортера.</param>
    /// <param name="other">Склад на іншу дату життя версії-імпортера.</param>
    /// <returns>Коди бібліотек зі зміненим складом (порожньо — склад однаковий).</returns>
    public static IReadOnlyList<string> Divergences(IReadOnlyList<SourceImport> baseline, IReadOnlyList<SourceImport> other)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(other);

        static string Key(IReadOnlyList<string>? names, StringComparer comparer)
            => names is null ? "-" : string.Join('', names.Select(n => comparer == StringComparer.Ordinal ? n : n.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        var result = new List<string>();
        var codes = baseline.Select(i => i.LibraryCode).Concat(other.Select(i => i.LibraryCode))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var a = baseline.FirstOrDefault(i => string.Equals(i.LibraryCode, code, StringComparison.OrdinalIgnoreCase));
            var b = other.FirstOrDefault(i => string.Equals(i.LibraryCode, code, StringComparison.OrdinalIgnoreCase));
            if (a is null || b is null
                || Key(a.ConstantCategories, StringComparer.Ordinal) != Key(b.ConstantCategories, StringComparer.Ordinal)
                || Key(a.OptionalArguments, StringComparer.OrdinalIgnoreCase) != Key(b.OptionalArguments, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(code);
            }
        }

        return result;
    }
}
