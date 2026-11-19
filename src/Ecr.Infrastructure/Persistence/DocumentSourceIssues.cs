// src/Ecr.Infrastructure/Persistence/DocumentSourceIssues.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B80 (N4, R3.2): проблеми джерела документа-споживача зв'язки <c>ApprovedOnly</c> — спільне тіло панелі проблем
/// розрахунку (<see cref="DocumentSourceMonthGate.ListSourceIssuesAsync"/>) і правила узгодженості
/// <c>STALE_APPROVED_DEPENDENT</c> (<c>StaleApprovedDependentCheck</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ті самі правила, що ворота подання N2b (<see cref="DocumentSourceMonthGate.FindUnapprovedSourceMonthAsync"/>): зв'язки
/// споживача в стані <c>ApprovedOnly</c> (<see cref="DocumentSourceMonthGate.ConsumerBindingsAsync"/>), читання з
/// документа (явний ключ перекриває біт), обраний документ року (<see cref="DocumentSourceBindingStore"/>), затверджені
/// місяці (<see cref="DocumentSourceGate.ApprovedMonthTimesAsync"/>, лише оригінал аркуша; кілька таблиць — місяць
/// затверджено, якщо затверджено будь-яку, момент — найпізніший).
/// </para>
/// <para>
/// ⚠ Затвердження місяця СПОЖИВАЧА — на рівні документ × період: період затверджено, якщо стани оригіналів УСІХ його
/// аркушів (<c>SheetCopyId = null</c>) є і всі <c>Approved</c>; момент — НАЙРАНІШИЙ <c>ApprovedAt</c> серед них
/// (консервативно: аркуш, затверджений раніше за повторне затвердження джерела, уже застарів). Копії аркушів не
/// враховуються. Місяці періоду — за періодичністю проєкту споживача (<see cref="DocumentSourceGate.OwnedMonths"/>).
/// </para>
/// <para>
/// ⛔ За <c>Any</c> — один дрібний запит (чи є хоч одна зв'язка <c>ApprovedOnly</c>) і нічого більше.
/// </para>
/// </remarks>
/// <param name="db">Контекст.</param>
/// <param name="readOverride">
/// Перекриття біта зв'язки <c>ReadFromDocument</c> (<see cref="Ecr.Application.Ports.IDocumentSourceFlagOverrides"/>;
/// у DI завжди <c>null</c> — ключ конфігурації виведено, P-0118-31/33); <c>null</c> — біт зв'язки.
/// </param>
internal sealed class DocumentSourceIssues(EcrDbContext db, bool? readOverride)
{
    /// <summary>Причина <c>SourceChangedAfterApproval</c>: місяць джерела зараз не затверджено.</summary>
    public const string ReasonNotApproved = "notApproved";

    /// <summary>Причина <c>SourceChangedAfterApproval</c>: місяць джерела затверджено пізніше за споживача.</summary>
    public const string ReasonReapproved = "reapproved";

    private readonly Dictionary<(int BindingId, int Year), SourceYear?> years = [];
    private readonly Dictionary<long, (string ProjectCode, string DocumentKey)> sources = [];

    /// <summary>Чи є хоч одна зв'язка складу HSE400 у стані <c>ApprovedOnly</c> — дрібна перевірка, за <c>Any</c> єдина.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<bool> AnyApprovedOnlyAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DocumentSourceBindings.AsNoTracking()
            .AnyAsync(
                b => b.SourceState == DocumentSourceState.ApprovedOnly
                     && b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping,
                ct);
    }

    /// <summary>Проблеми джерела документа за періоди.</summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKeys">Періоди документа.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>За зростанням періоду й місяця; без повторів (кілька зв'язок одного місяця — один рядок).</returns>
    public async Task<List<SourceIssueFinding>> ListAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);

        // Явне «не читати з документа» — Land читає довідник, джерело не потрібне: жодного запиту.
        if (readOverride == false || periodKeys.Count == 0
            || !await AnyApprovedOnlyAsync(db, ct).ConfigureAwait(false))
        {
            return [];
        }

        var keys = periodKeys.Distinct().Order().ToList();
        var states = await db.ApprovalStates.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.SheetCopyId == null && keys.Contains(s.PeriodKey))
            .Select(s => new { s.Id, s.PeriodKey, s.Status, s.ApprovedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var findings = new List<SourceIssueFinding>();
        foreach (var periodKey in keys)
        {
            var bindings = await DocumentSourceMonthGate
                .ConsumerBindingsAsync(db, documentId, periodKey, ct).ConfigureAwait(false);
            if (bindings.Count == 0)
            {
                continue;
            }

            var own = states.Where(s => s.PeriodKey == periodKey).ToList();
            var approved = own.Count > 0 && own.TrueForAll(s => s.Status == DocumentStatus.Approved);
            var approvedAt = approved ? own.Min(s => s.ApprovedAt) : null;
            var stateId = own.Count == 0 ? (long?)null : own.Min(s => s.Id);
            var year = periodKey / 100;

            foreach (var binding in bindings)
            {
                if (!(readOverride ?? binding.ReadFromDocument))
                {
                    continue;
                }

                // A5: документа-джерела за рік немає — рядки довідника, як доти; проблеми немає.
                var source = await SourceYearAsync(binding.Id, year, ct).ConfigureAwait(false);
                if (source is null)
                {
                    continue;
                }

                foreach (var month in DocumentSourceGate.OwnedMonths(binding.PeriodKind, periodKey % 100))
                {
                    var sourceApproved = source.Approved.TryGetValue(month, out var sourceApprovedAt);
                    var (kind, reason) = Classify(approved, approvedAt, sourceApproved, sourceApprovedAt);
                    if (kind is null)
                    {
                        continue;
                    }

                    var facts = await SourceFactsAsync(source.DocumentId, ct).ConfigureAwait(false);
                    findings.Add(new SourceIssueFinding(
                        new SourceIssueRow(
                            kind, periodKey, month, (year * 100) + month, source.DocumentId, facts.ProjectCode,
                            facts.DocumentKey, sourceApproved ? sourceApprovedAt : null, approvedAt),
                        reason,
                        stateId));
                }
            }
        }

        return [.. findings.DistinctBy(f => (f.Row.Kind, f.Row.PeriodKey, f.Row.MonthNumber))];
    }

    /// <summary>Вид проблеми місяця за станом споживача і джерела; <c>null</c> — проблеми немає.</summary>
    private static (string? Kind, string? Reason) Classify(
        bool approved, DateTime? approvedAt, bool sourceApproved, DateTime? sourceApprovedAt)
    {
        if (!approved)
        {
            return sourceApproved ? (null, null) : (ISourceIssueReader.SourceMonthNotApproved, null);
        }

        if (!sourceApproved)
        {
            return (ISourceIssueReader.SourceChangedAfterApproval, ReasonNotApproved);
        }

        return sourceApprovedAt > approvedAt
            ? (ISourceIssueReader.SourceChangedAfterApproval, ReasonReapproved)
            : (null, null);
    }

    /// <summary>Обраний документ-джерело року зв'язки і його затверджені місяці з моментом; кеш на зв'язку × рік.</summary>
    private async Task<SourceYear?> SourceYearAsync(int bindingId, int year, CancellationToken ct)
    {
        if (years.TryGetValue((bindingId, year), out var cached))
        {
            return cached;
        }

        var store = new DocumentSourceBindingStore(db);
        var tableIds = await store.BoundTableIdsAsync(bindingId, ct).ConfigureAwait(false);
        var chosen = await store.ChosenSourceDocumentAsync(tableIds, year, ct).ConfigureAwait(false);
        SourceYear? result = null;
        if (chosen is not null)
        {
            var times = await DocumentSourceGate
                .ApprovedMonthTimesAsync(db, [chosen.DocumentId], tableIds, ct).ConfigureAwait(false);
            var approved = new Dictionary<int, DateTime?>();
            foreach (var ((_, _, sourceYear, month), at) in times)
            {
                if (sourceYear != year)
                {
                    continue;
                }

                approved[month] = approved.TryGetValue(month, out var seen) && (at is null || (seen is { } s && s >= at))
                    ? seen
                    : at;
            }

            result = new SourceYear(chosen.DocumentId, approved);
        }

        years[(bindingId, year)] = result;
        return result;
    }

    /// <summary>Код проєкту й бізнес-ключ документа-джерела; кеш на документ.</summary>
    private async Task<(string ProjectCode, string DocumentKey)> SourceFactsAsync(long documentId, CancellationToken ct)
    {
        if (sources.TryGetValue(documentId, out var cached))
        {
            return cached;
        }

        var facts = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { ProjectCode = p.Code, d.BusinessKey })
            .FirstAsync(ct).ConfigureAwait(false);

        var result = (facts.ProjectCode, facts.BusinessKey);
        sources[documentId] = result;
        return result;
    }

    private sealed record SourceYear(long DocumentId, Dictionary<int, DateTime?> Approved);
}

/// <summary>Проблема джерела з тим, що потрібно правилу узгодженості.</summary>
/// <param name="Row">Рядок для панелі.</param>
/// <param name="Reason">
/// Для <c>SourceChangedAfterApproval</c> — <see cref="DocumentSourceIssues.ReasonNotApproved"/> чи
/// <see cref="DocumentSourceIssues.ReasonReapproved"/>; інакше <c>null</c>.
/// </param>
/// <param name="StateId">Найменший id стану оригіналу аркуша споживача за період (сутність знахідки); <c>null</c> — станів немає.</param>
internal sealed record SourceIssueFinding(SourceIssueRow Row, string? Reason, long? StateId);
