// src/Ecr.Infrastructure/Persistence/SourceImpactAssessor.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B80 N5 (D-554): реалізація <see cref="ISourceImpactAssessor"/> — вплив затвердження чи повернення із затвердженого
/// аркуша документа-джерела на залежні документи зв'язок <c>ApprovedOnly</c>.
/// </summary>
/// <remarks>
/// <para>
/// Зв'язка діє, коли шаблон документа — її джерело, стан <c>ApprovedOnly</c>, читання з документа ввімкнено, аркуш містить
/// таблиці зв'язки, а документ — той, який завантажувач обрав би за рік (<see cref="DocumentSourceBindingStore"/>).
/// Споживачі — шаблони, яких називає граф (<see cref="IDependencyGraphProvider"/>), як у воротах подання й панелі проблем.
/// </para>
/// <para>
/// Залежний період — документ × період, що має стани оригіналів аркушів за рік джерела й володіє хоч одним зачепленим
/// місяцем за періодичністю СВОГО проєкту (<see cref="DocumentSourceGate.OwnedMonths"/>); затверджений — усі стани
/// затверджено. Період без жодного стану (ще не відкривався) не перелічується: затвердити нічого, перерахунок його не
/// шкодить.
/// </para>
/// <para>
/// Відбиток — SHA-256 від дії, адреси джерела, політики й відсортованого переліку залежних (зв'язка, документ, період,
/// затверджений): змінилось що-небудь із цього — підтвердження застаріло.
/// </para>
/// </remarks>
/// <param name="db">Контекст.</param>
/// <param name="graph">Граф залежностей документів: споживачі зв'язки.</param>
internal sealed class SourceImpactAssessor(EcrDbContext db, IDependencyGraphProvider graph) : ISourceImpactAssessor
{
    /// <inheritdoc />
    public async Task<SourceImpact> AssessAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, CancellationToken ct)
    {
        var year = periodKey / 100;
        var source = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                from pv in db.TemplateVersions.AsNoTracking()
                where pv.Id == (d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, periodKey) : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, periodKey))
                select new { TemplateId = d.TemplateId ?? pv.TemplateId, p.PeriodKind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (source is null)
        {
            return Build(change, documentId, sheetDefId, periodKey, DocumentSourcePolicy.None, [], []);
        }

        var months = DocumentSourceGate.OwnedMonths(source.PeriodKind, periodKey % 100).Select(m => (year * 100) + m).ToList();
        var bindings = await db.DocumentSourceBindings.AsNoTracking()
            .Where(b => b.SourceTemplateId == source.TemplateId
                        // Правка комірки: залежні бачать джерело одразу лише за `any`; Approve/Reopen — за `approvedOnly`.
                        && b.SourceState == (change == SourceApprovalChange.Edited ? DocumentSourceState.Any : DocumentSourceState.ApprovedOnly)
                        && b.ReadFromDocument
                        && b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping)
            .OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.Code, b.Policy })
            .ToListAsync(ct).ConfigureAwait(false);
        if (bindings.Count == 0)
        {
            return Build(change, documentId, sheetDefId, periodKey, DocumentSourcePolicy.None, months, []);
        }

        var snapshot = await graph.GetAsync(ct).ConfigureAwait(false);
        var templateCodes = snapshot.Nodes
            .Where(n => n.Ref.Kind == DependencyNodeKind.Template)
            .ToDictionary(n => n.Ref.Id, n => n.Code);
        var store = new DocumentSourceBindingStore(db, graph);
        var consumers = new List<SourceImpactConsumer>();
        var policies = new List<(DocumentSourcePolicy Policy, bool HasApproved)>();
        foreach (var binding in bindings)
        {
            var tableIds = await store.BoundTableIdsAsync(binding.Id, ct).ConfigureAwait(false);
            if (tableIds.Count == 0
                || !await db.TableDefs.AsNoTracking().AnyAsync(t => tableIds.Contains(t.Id) && t.SheetDefId == sheetDefId, ct).ConfigureAwait(false))
            {
                continue;
            }

            // Споживачі читають лише документ, який обрав би завантажувач; інший документ того ж шаблону їх не зачіпає.
            var chosen = await store.ChosenSourceDocumentAsync(tableIds, year, ct).ConfigureAwait(false);
            if (chosen?.DocumentId != documentId)
            {
                continue;
            }

            var consumerTemplates = snapshot.TemplateDependencies
                .Where(d => d.BindingId == binding.Id)
                .Select(d => d.ConsumerTemplateId)
                .Distinct()
                .ToList();
            var found = await ConsumersAsync(binding.Id, binding.Code, consumerTemplates, templateCodes, year, months, ct)
                .ConfigureAwait(false);
            consumers.AddRange(found);
            policies.Add((binding.Policy, found.Exists(c => c.Approved)));
        }

        // Політика — найсуворіша серед зв'язок із затвердженими залежними; без таких — найсуворіша серед тих, що діють.
        var policy = policies.Any(p => p.HasApproved)
            ? policies.Where(p => p.HasApproved).Max(p => p.Policy)
            : policies.Count == 0 ? DocumentSourcePolicy.None : policies.Max(p => p.Policy);
        return Build(change, documentId, sheetDefId, periodKey, policy, months, consumers);
    }

    private async Task<List<SourceImpactConsumer>> ConsumersAsync(
        int bindingId,
        string bindingCode,
        List<int> consumerTemplates,
        Dictionary<int, string> templateCodes,
        int year,
        List<int> months,
        CancellationToken ct)
    {
        if (consumerTemplates.Count == 0)
        {
            return [];
        }

        var yearStart = year * 100;
        var yearEnd = (year + 1) * 100;
        var states = await (
                from s in db.ApprovalStates.AsNoTracking()
                where s.SheetCopyId == null && s.PeriodKey >= yearStart && s.PeriodKey < yearEnd
                join d in db.Documents.AsNoTracking() on s.DocumentId equals d.Id
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                from pv in db.TemplateVersions.AsNoTracking()
                where pv.Id == (d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, s.PeriodKey) : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, s.PeriodKey))
                let templateId = d.TemplateId ?? pv.TemplateId
                where consumerTemplates.Contains(templateId)
                select new
                {
                    s.DocumentId,
                    s.PeriodKey,
                    s.Status,
                    TemplateId = templateId,
                    p.PeriodKind,
                    ProjectCode = p.Code,
                    d.BusinessKey,
                })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<SourceImpactConsumer>();
        foreach (var group in states.GroupBy(s => (s.DocumentId, s.PeriodKey)).OrderBy(g => g.Key.DocumentId).ThenBy(g => g.Key.PeriodKey))
        {
            var first = group.First();
            var owned = DocumentSourceGate.OwnedMonths(first.PeriodKind, first.PeriodKey % 100).Select(m => (year * 100) + m);
            if (!owned.Any(months.Contains))
            {
                continue;
            }

            result.Add(new SourceImpactConsumer(
                bindingId,
                bindingCode,
                templateCodes.TryGetValue(first.TemplateId, out var code) ? code : first.TemplateId.ToString(CultureInfo.InvariantCulture),
                first.DocumentId,
                first.ProjectCode,
                first.BusinessKey,
                first.PeriodKey,
                group.All(s => s.Status == DocumentStatus.Approved)));
        }

        return result;
    }

    private static SourceImpact Build(
        SourceApprovalChange change,
        long documentId,
        int sheetDefId,
        int periodKey,
        DocumentSourcePolicy policy,
        IReadOnlyList<int> months,
        IReadOnlyList<SourceImpactConsumer> consumers)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{change}|{documentId}|{sheetDefId}|{periodKey}|{policy}");
        foreach (var c in consumers.OrderBy(c => c.BindingId).ThenBy(c => c.DocumentId).ThenBy(c => c.PeriodKey))
        {
            text.Append(CultureInfo.InvariantCulture, $"|{c.BindingId}:{c.DocumentId}:{c.PeriodKey}:{(c.Approved ? 1 : 0)}");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()[..32];
        return new SourceImpact(change, documentId, sheetDefId, periodKey, policy, months, consumers, hash);
    }
}
