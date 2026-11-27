using Ecr.Application.Ports;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>Підсумок розв'язання кодів речовин.</summary>
/// <param name="Found">Код → Id запису.</param>
/// <param name="Ambiguous">Код → кандидати, між якими вибрати не можна (кілька довідників, жоден чи кілька — методології).</param>
public sealed record SubstanceResolution(
    IReadOnlyDictionary<string, long> Found,
    IReadOnlyDictionary<string, IReadOnlyList<SubstanceCandidate>> Ambiguous);

/// <summary>
/// ✎ c11-10: код речовини скрипта → запис довідника без мовчазного «меншого Id». Порядок: (1) речовини, які версія вже має
/// (клон джерела, попереднє збереження); (2) єдиний кандидат; (3) єдиний кандидат довідника, який уже вживає методологія;
/// інакше код НЕОДНОЗНАЧНИЙ — перевірка оточення пише діагностику <c>ambiguousSubstance</c>, матеріалізація його не бере.
/// </summary>
public static class ScriptSubstanceResolver
{
    /// <summary>Розв'язує коди.</summary>
    /// <param name="codes">Коди речовин скрипта.</param>
    /// <param name="versionSubstances">Речовини версії: Id запису → код (<see cref="IMethodologyStore.GetSubstanceCodesAsync"/>).</param>
    /// <param name="candidates">Кандидати (<see cref="IMethodologyStore.FindSubstanceCandidatesAsync"/>).</param>
    /// <returns>Розв'язані й неоднозначні коди; невідомих немає ні там, ні там.</returns>
    public static SubstanceResolution Resolve(
        IReadOnlyCollection<string> codes,
        IReadOnlyDictionary<long, string> versionSubstances,
        IReadOnlyList<SubstanceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(versionSubstances);
        ArgumentNullException.ThrowIfNull(candidates);

        var own = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entryId, code) in versionSubstances.OrderBy(s => s.Key))
        {
            if (!string.IsNullOrWhiteSpace(code))
            {
                own.TryAdd(code.Trim(), entryId);
            }
        }

        var found = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new Dictionary<string, IReadOnlyList<SubstanceCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (own.TryGetValue(code, out var kept))
            {
                found[code] = kept;
                continue;
            }

            var all = candidates
                .Where(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(c => c.EntryId)
                .OrderBy(c => c.EntryId)
                .ToList();
            if (all.Count == 0)
            {
                continue;
            }

            var preferred = all.Count == 1 ? all : [.. all.Where(c => c.UsedByMethodology)];
            if (preferred.Count == 1)
            {
                found[code] = preferred[0].EntryId;
            }
            else
            {
                ambiguous[code] = all;
            }
        }

        return new SubstanceResolution(found, ambiguous);
    }
}
