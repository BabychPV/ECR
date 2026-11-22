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

    /// <summary>Лише тести: викликається перед компіляцією кожної версії (кинути — збій компіляції чи рефлексії).</summary>
    internal Action<int>? BeforeCompileProbe { get; init; }

    /// <summary>Лише тести: викликається перед збереженням (вставити ті самі рядки — гонка двох екземплярів).</summary>
    internal Func<CancellationToken, Task>? BeforeSaveProbe { get; init; }

    /// <summary>Виконує дозапис. Не кидає (крім скасування): збій — попередження в журнал, старт іде далі.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки версій дозаписано (0 — і при збої).</returns>
    /// <remarks>
    /// ⛔ Огляд b9, п. 2: контекст — спільний scoped контекст старту. Збій збереження (гонка двох екземплярів на
    /// <c>UQ_MethodologyReadField</c>, обрив з'єднання) лишав додані рядки відстежуваними — наступний <c>SaveChanges</c> старту
    /// повторив би їх і впав. Тож при збої додані цим дозаписом рядки знімаються з відстеження. Версія без запису лишається
    /// «читачем усього» (безпечний бік) і дозапишеться наступним стартом.
    /// </remarks>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var entry in db.ChangeTracker.Entries<MethodologyReadField>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            if (logger is not null)
            {
                LogFailed(logger, ex.GetType().Name, ex.Message);
            }

            return 0;
        }
    }

    private async Task<int> RunCoreAsync(CancellationToken ct)
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
            // ✎ Огляд b9, п. 2: збій компіляції чи обходу дерева (виняток, не лише «не скомпілювалось») — та сама відповідь
            // «читає будь-що» для ЦІЄЇ версії, решта дозаписується як завжди.
            IReadOnlyList<ScriptReadField>? fields;
            try
            {
                BeforeCompileProbe?.Invoke(version.Id);
                var compiled = Ecr.Scripting.ScriptCompiler
                    .Compile(version.ScriptText ?? string.Empty, new Ecr.Scripting.ScriptCompileOptions(version.NumericMode, version.Library))
                    .Script;
                fields = compiled is null ? null : ScriptReadFields.Of(compiled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fields = null;
            }

            db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, MethodologyReadField.RecordedMarker, otherMonths: false));
            if (fields is null)
            {
                unknown++;
                db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, MethodologyReadField.AllFields, otherMonths: true));
                continue;
            }

            foreach (var field in fields
                         .Where(f => f.FieldCode.Length is > 0 and <= MethodologyReadField.MaxFieldCodeLength))
            {
                db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, field.FieldCode, field.OtherMonths));
            }
        }

        if (BeforeSaveProbe is not null)
        {
            await BeforeSaveProbe(ct).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "D-568: дозапис полів читання скриптів не вдався ({Kind}: {Error}) — версії без запису лишаються читачами всього, наступний старт повторить.")]
    private static partial void LogFailed(ILogger logger, string kind, string error);
}
