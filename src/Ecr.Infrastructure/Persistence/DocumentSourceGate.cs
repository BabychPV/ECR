// src/Ecr.Infrastructure/Persistence/DocumentSourceGate.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Рядок зв'язки, потрібний завантажувачу й фактам здоров'я.</summary>
/// <param name="Id">Зв'язка.</param>
/// <param name="RegistryDefId">Довідник зв'язки.</param>
/// <param name="SourceState">Стан-ворота.</param>
/// <param name="ReadFromDocument">Біт «читати з документа».</param>
/// <param name="RecalculateDependents">Біт «ставити перерахунок залежних».</param>
public sealed record DocumentSourceBindingRow(
    int Id, int RegistryDefId, DocumentSourceState SourceState, bool ReadFromDocument, bool RecalculateDependents);

/// <summary>
/// B80 (N1, A2): ворота «лише з затвердженого місяця джерела» — спільне для завантажувача складу і фактів здоров'я.
/// </summary>
/// <remarks>
/// <para>
/// G1: місяць <c>MM</c> року <c>yyyy</c> документа-джерела проходить, лише коли аркуш таблиці (оригінал, без копії)
/// має стан <see cref="DocumentStatus.Approved"/> у періоді, що ВОЛОДІЄ місяцем: на місячному проєкті — <c>yyyyMM</c>
/// (маршрутизатор місячних колонок), на річному — якір року (усі 12 місяців), на квартальному — три місяці від початку
/// кварталу. Довільний період (<c>Custom</c>) покриває лише свій місяць — консервативно.
/// </para>
/// <para>
/// ⚠ Перемикання на <see cref="DocumentSourceState.ApprovedOnly"/> дозволене лише для місячного проєкту (R3.3), тож
/// річна гілка тут — для цілісності правила, не для робочого шляху.
/// </para>
/// </remarks>
public static class DocumentSourceGate
{
    /// <summary>Зв'язка за кодом шаблону-джерела і кодом довідника; <c>null</c> — зв'язки немає.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="templateCode">Код шаблону-джерела.</param>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<DocumentSourceBindingRow?> FindBindingAsync(
        EcrDbContext db, string templateCode, string registryCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return (
                from b in db.DocumentSourceBindings.AsNoTracking()
                join t in db.Templates.AsNoTracking() on b.SourceTemplateId equals t.Id
                join r in db.RegistryDefs.AsNoTracking() on b.RegistryDefId equals r.Id
                where t.Code == templateCode && r.Code == registryCode
                select new DocumentSourceBindingRow(
                    b.Id, b.RegistryDefId, b.SourceState, b.ReadFromDocument, b.RecalculateDependents))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Затверджені місяці документів-джерел: (документ, таблиця, рік, місяць), де аркуш таблиці затверджено в
    /// періоді-власнику місяця. ОДИН запит до <c>wf.ApprovalState</c>.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentIds">Обрані документи-джерела.</param>
    /// <param name="tableDefIds">Таблиці зв'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<HashSet<(long DocumentId, int TableDefId, int Year, int Month)>> ApprovedMonthsAsync(
        EcrDbContext db, IReadOnlyCollection<long> documentIds, IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(documentIds);
        ArgumentNullException.ThrowIfNull(tableDefIds);

        var approved = new HashSet<(long, int, int, int)>();
        if (documentIds.Count == 0 || tableDefIds.Count == 0)
        {
            return approved;
        }

        var docIds = documentIds.Distinct().ToList();
        var tableIds = tableDefIds.Distinct().ToList();
        var states = await (
                from s in db.ApprovalStates.AsNoTracking()
                where docIds.Contains(s.DocumentId) && s.SheetCopyId == null && s.Status == DocumentStatus.Approved
                join t in db.TableDefs.AsNoTracking() on s.SheetDefId equals t.SheetDefId
                where tableIds.Contains(t.Id)
                join d in db.Documents.AsNoTracking() on s.DocumentId equals d.Id
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { s.DocumentId, TableDefId = t.Id, s.PeriodKey, p.PeriodKind })
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var state in states)
        {
            var year = state.PeriodKey / 100;
            foreach (var month in OwnedMonths(state.PeriodKind, state.PeriodKey % 100))
            {
                approved.Add((state.DocumentId, state.TableDefId, year, month));
            }
        }

        return approved;
    }

    /// <summary>Місяці, якими володіє період із першим місяцем <paramref name="startMonth"/>.</summary>
    /// <param name="kind">Періодичність проєкту.</param>
    /// <param name="startMonth">Місяць ключа періоду (1–12).</param>
    public static IEnumerable<int> OwnedMonths(PeriodKind kind, int startMonth)
    {
        var length = kind switch
        {
            PeriodKind.Yearly => 12,
            PeriodKind.Quarterly => 3,
            _ => 1,
        };

        for (var month = startMonth; month < startMonth + length && month <= 12; month++)
        {
            yield return month;
        }
    }
}
