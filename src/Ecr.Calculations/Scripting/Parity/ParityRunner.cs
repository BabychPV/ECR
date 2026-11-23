using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations.Scripting.Parity;

/// <summary>Версія з тіньовим скриптом на рушії <c>Formulas</c>: що перевіряє <see cref="ParityRunner"/>.</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="VersionId">Версія.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="ScriptText">Тіньовий текст.</param>
/// <param name="IsLibrary">Методологія-бібліотека: рядків і прив'язок не має, її доводять імпортери.</param>
public sealed record ParityVersion(
    int MethodologyId, string MethodologyCode, int VersionId, string Version, string ScriptText, bool IsLibrary = false);

/// <summary>Екземпляр таблиці документа, до якої прив'язана методологія.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="TableInstanceId">Екземпляр таблиці (оригінал, не копія аркуша).</param>
public sealed record ParityInstance(long DocumentId, int PeriodKey, long TableInstanceId);

/// <summary>
/// Порт переліків для <see cref="ParityRunner"/>: версії з тіньовим текстом і екземпляри таблиць, до яких прив'язана
/// методологія. Реалізація читає базу (інструмент перенесення), ця збірка EF не знає.
/// </summary>
public interface IParityTargetSource
{
    /// <summary>
    /// Опубліковані версії (рівні <c>Configuration</c> і <c>Module</c>) з рушієм <c>Formulas</c> і непорожнім тіньовим текстом.
    /// </summary>
    /// <param name="methodologyCode">Лише ця методологія; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ParityVersion>> ListShadowedVersionsAsync(string? methodologyCode, CancellationToken ct);

    /// <summary>
    /// Екземпляри таблиць (оригінали), до яких активно прив'язана методологія, — за всіма документами й періодами.
    /// </summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="documentId">Лише цей документ; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ParityInstance>> ListInstancesAsync(int methodologyId, long? documentId, CancellationToken ct);

    /// <summary>
    /// Опубліковані версії рушія <c>Formulas</c> (виведені з обігу рушій при виборі версії прив'язки не обирає), яких <see cref="ListShadowedVersionsAsync"/> не
    /// повертає, тобто паритет їх не покриває: немає тіньового тексту, немає дати чинності.
    /// Запис — «код версія: причина». За замовчуванням порожньо.
    /// </summary>
    /// <param name="methodologyCode">Лише ця методологія; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<string>> ListUncoveredVersionsAsync(string? methodologyCode, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Прогін паритету по всій копії бази (MS, план §8.4): для кожної версії з тіньовим скриптом на <c>Formulas</c> — усі
/// документи, періоди й екземпляри таблиць, де ЦЯ версія чинна на дату кінця періоду, а методологія прив'язана до таблиці.
/// </summary>
/// <remarks>
/// Версія на період резолвиться тим самим кодом, що й у прогоні (<see cref="MethodologyResolver.ResolveBindingVersionAsync"/>):
/// перевіряти версію на періоді, де її не рахують, було б порівнянням, яке нікому не потрібне.
/// </remarks>
public sealed class ParityRunner(
    IParityTargetSource targets,
    MethodologyResolver resolver,
    IPeriodStore periods,
    ParityComparer comparer)
{
    /// <summary>Виконує прогін.</summary>
    /// <param name="methodologyCode">Лише ця методологія; <c>null</c> — усі.</param>
    /// <param name="documentId">Лише цей документ; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ParityRunReport> RunAsync(string? methodologyCode, long? documentId, CancellationToken ct)
    {
        var mismatches = new List<ParityMismatch>();
        var unproven = new List<string>();
        var perVersion = new List<ParityVersionResult>();
        var compared = 0;
        var rows = 0;
        var values = 0;

        var versions = await targets.ListShadowedVersionsAsync(methodologyCode, ct).ConfigureAwait(false);
        foreach (var version in versions)
        {
            var resolved = new Dictionary<DateOnly, int?>();
            var comparedForVersion = 0;
            var rowsForVersion = 0;
            var valuesForVersion = 0;
            var mismatchesForVersion = 0;
            var instances = await targets.ListInstancesAsync(version.MethodologyId, documentId, ct).ConfigureAwait(false);
            foreach (var instance in instances)
            {
                ct.ThrowIfCancellationRequested();

                var periodKey = new PeriodKey(instance.PeriodKey);
                var bounds = await periods.FindPeriodBoundsAsync(instance.DocumentId, instance.PeriodKey, ct).ConfigureAwait(false);
                if (bounds is null)
                {
                    continue;
                }

                if (!resolved.TryGetValue(bounds.PeriodEnd, out var resolvedVersionId))
                {
                    var resolution = await resolver
                        .ResolveBindingVersionAsync(version.MethodologyId, bounds.PeriodEnd, ct)
                        .ConfigureAwait(false);
                    resolvedVersionId = resolution.Descriptor?.MethodologyVersionId;
                    resolved[bounds.PeriodEnd] = resolvedVersionId;
                }

                if (resolvedVersionId != version.VersionId)
                {
                    continue;
                }

                var report = await comparer
                    .CompareAsync(version.VersionId, version.ScriptText, instance.DocumentId, periodKey, instance.TableInstanceId, ct)
                    .ConfigureAwait(false);
                compared++;
                comparedForVersion++;
                rows += report.RowsCompared;
                values += report.ValuesCompared;
                rowsForVersion += report.RowsCompared;
                valuesForVersion += report.ValuesCompared;
                mismatchesForVersion += report.Mismatches.Count;
                mismatches.AddRange(report.Mismatches);
            }

            var result = new ParityVersionResult(
                version.MethodologyCode, version.VersionId, version.Version, comparedForVersion, rowsForVersion,
                valuesForVersion, mismatchesForVersion, version.IsLibrary,
                Convert.ToHexStringLower(Ecr.Scripting.ScriptCompiler.Hash(version.ScriptText)));
            perVersion.Add(result);

            // ✎ D-571: версія без жодного порівняного ЗНАЧЕННЯ (екземпляри є, але правила не відібрали рядків) — теж не
            // доведена; розбіжності називає перелік розбіжностей, тут лише «нічого не доведено».
            if (result is { IsProven: false, IsLibrary: false, MismatchCount: 0 })
            {
                unproven.Add($"{version.MethodologyCode} {version.Version}: {result.UnprovenReason}");
            }
        }

        unproven.AddRange(await targets.ListUncoveredVersionsAsync(methodologyCode, ct).ConfigureAwait(false));

        return new ParityRunReport(compared, rows, values, mismatches, unproven, perVersion);
    }
}
