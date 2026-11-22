// src/Ecr.Infrastructure/Persistence/MethodologyReadFieldsBackfill.cs
using Ecr.Application.Calculations.Scripting;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ D-568: одноразовий дозапис <c>calc.MethodologyReadField</c> для версій-скриптів, опублікованих до D-568 (у них переліку
/// немає): скрипт компілюється, і поля власної таблиці записуються тим самим правилом, що й при публікації
/// (<see cref="ScriptReadFields.Of"/>).
/// </summary>
/// <remarks>
/// <para>
/// Виконує застосунок на старті після сіду (як <see cref="MethodologyArgumentsBackfill"/>). Ідемпотентний і самообмежений:
/// береться лише опублікована/застаріла версія з рушієм <c>Script</c> без жодного рядка; після першого проходу таких немає.
/// </para>
/// <para>
/// ⛔ Безпечний бік: скрипт, що не компілюється, отримує рядок <see cref="MethodologyReadField.AllFields"/> («читає будь-що»);
/// версія, до якої дозапис не дійшов (збій старту, стеля), лишається без рядків — прохід читача вважає її читачем усього,
/// доки її не дозапишуть чи не перевидадуть. Пропущеного читача немає в жодному разі.
/// </para>
/// </remarks>
/// <param name="db">Контекст бази.</param>
/// <param name="logger">Журнал старту; <c>null</c> — без журналу.</param>
public sealed partial class MethodologyReadFieldsBackfill(EcrDbContext db, ILogger? logger = null)
{
    /// <summary>Стеля версій за один старт (скриптів у базі — десятки).</summary>
    private const int MaxVersions = 5_000;

    /// <summary>Лише тести: обмежити дозапис цими версіями (спільна тестова база).</summary>
    internal IReadOnlyCollection<int>? OnlyVersions { get; init; }

    /// <summary>Виконує дозапис.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки версій дозаписано.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var query = db.MethodologyVersions.AsNoTracking();
        if (OnlyVersions is { } only)
        {
            query = query.Where(v => only.Contains(v.Id));
        }

        var versions = await query
            .Where(v => v.Engine == MethodologyEngine.Script
                        && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated)
                        && !db.MethodologyReadFields.Any(f => f.MethodologyVersionId == v.Id))
            .OrderBy(v => v.Id)
            .Select(v => new
            {
                v.Id,
                v.ScriptText,
                v.NumericMode,
                Library = db.Methodologies.Any(m => m.Id == v.MethodologyId && m.Kind == MethodologyKind.Library),
            })
            .Take(MaxVersions)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return 0;
        }

        var unknown = 0;
        foreach (var version in versions)
        {
            var compiled = Ecr.Scripting.ScriptCompiler
                .Compile(version.ScriptText ?? string.Empty, new Ecr.Scripting.ScriptCompileOptions(version.NumericMode, version.Library))
                .Script;
            db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, MethodologyReadField.RecordedMarker, otherMonths: false));
            if (compiled is null)
            {
                unknown++;
                db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, MethodologyReadField.AllFields, otherMonths: true));
                continue;
            }

            foreach (var field in ScriptReadFields.Of(compiled)
                         .Where(f => f.FieldCode.Length is > 0 and <= MethodologyReadField.MaxFieldCodeLength))
            {
                db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, field.FieldCode, field.OtherMonths));
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (logger is not null)
        {
            LogBackfilled(logger, versions.Count, unknown);
        }

        return versions.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "D-568: дозаписано поля читання скриптів для {Versions} опублікованих версій; не скомпілювались (читачі всього) {Unknown}.")]
    private static partial void LogBackfilled(ILogger logger, int versions, int unknown);
}
