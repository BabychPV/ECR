// src/Ecr.Application/Registries/Nae/NaeTreeNodeApplier.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Nae;

/// <summary>What one create-only write of NAE nodes is recorded as in the structure audit.</summary>
/// <param name="Operation">Audit operation (<c>NaeAfImport</c>, <c>NaeExcelImport</c>, <c>NaeRootCreate</c>).</param>
/// <param name="ReasonKey">UI key of the change reason (<c>registryAudit.*</c>, parameter <c>count</c>).</param>
/// <param name="Summary">Counters stored as the new state of the audit record.</param>
public sealed record NaeWriteAudit(string Operation, string ReasonKey, object Summary);

/// <summary>
/// Creates planned NAE tree nodes level by level in ONE transaction (all or none): code, values, names, level, parent, ordinal
/// after the existing siblings, and one structure-audit record. Shared by the AF load, the Excel import and manual roots.
/// </summary>
public sealed class NaeTreeNodeApplier(RegistryEntryWriter writer, IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Writes the nodes.</summary>
    /// <param name="definition">The NAE tree registry.</param>
    /// <param name="levels">Its levels by code.</param>
    /// <param name="toCreate">Nodes to create, parents before children.</param>
    /// <param name="existingIdByKey">Plan keys of nodes that already exist, with their entry ids (parents of new nodes).</param>
    /// <param name="maxOrdinalByParent">Highest ordinal among the live children of each existing parent (0 = roots).</param>
    /// <param name="record">Audit record.</param>
    /// <param name="userId">Acting user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Entry ids of all nodes by plan key (existing and created).</returns>
    /// <exception cref="BusinessRuleException"><c>422 naeImportRejected</c>: the writer refused a node; nothing is written.</exception>
    public async Task<IReadOnlyDictionary<string, long>> WriteAsync(
        RegistryDef definition,
        IReadOnlyDictionary<string, RegistryLevelDef> levels,
        IReadOnlyList<NaeTreeNodePlan> toCreate,
        IReadOnlyDictionary<string, long> existingIdByKey,
        IReadOnlyDictionary<long, int> maxOrdinalByParent,
        NaeWriteAudit record,
        int userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(toCreate);
        ArgumentNullException.ThrowIfNull(existingIdByKey);
        ArgumentNullException.ThrowIfNull(maxOrdinalByParent);
        ArgumentNullException.ThrowIfNull(record);

        // ⛔ D-211: an External registry is written only by its AF sync.
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);

        var ids = new Dictionary<string, long>(existingIdByKey, StringComparer.Ordinal);

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                foreach (var level in NaeTreeSchema.LevelCodes)
                {
                    var batchNodes = toCreate.Where(n => n.LevelCode == level).ToList();
                    if (batchNodes.Count == 0)
                    {
                        continue;
                    }

                    var batch = new RegistryEntryWriteBatch(
                        definition.Id,
                        [.. batchNodes.Select(n => new RegistryEntryWrite(
                            n.Code, n.Fields.ToDictionary(f => f.Key, f => (object?)f.Value, StringComparer.Ordinal))
                        {
                            DisplayName = n.Display["en"],
                        })])
                    {
                        CreateOnly = true,
                    };
                    var result = await writer.WriteAsync(batch, token).ConfigureAwait(false);
                    if (result.Errors.Count > 0)
                    {
                        var first = result.Errors[0];
                        throw new BusinessRuleException(
                            "ECR-REG-0422",
                            $"NAE load rejected at {first.Key}: {first.MessageKey}.",
                            new Dictionary<string, object?>
                            {
                                ["messageKey"] = ImportNaeFromAfHandler.RejectedKey,
                                ["code"] = first.Key,
                                ["reason"] = first.MessageKey,
                            });
                    }

                    foreach (var row in result.Rows.Where(r => r.IsNew))
                    {
                        var node = batchNodes[row.Row - 1];
                        row.Entry.Rename(new LocalizedText(new Dictionary<string, string>(node.Display, StringComparer.OrdinalIgnoreCase)));
                        row.Entry.SetLevel(levels[node.LevelCode].Id);
                        var parentId = node.ParentKey is { } parentKey ? ids[parentKey] : 0L;
                        row.Entry.SetOrdinal(node.Ordinal + maxOrdinalByParent.GetValueOrDefault(parentId));
                        if (parentId != 0)
                        {
                            row.Entry.SetParent(parentId);
                        }

                        ids[node.Key] = row.Entry.Id;
                    }

                    await uow.SaveChangesAsync(token).ConfigureAwait(false);
                }

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "cfg.RegistryDef",
                        EntityId: definition.Id,
                        ChangeClass: ChangeClass.Guarded,
                        Operation: record.Operation,
                        OldJson: null,
                        NewJson: JsonSerializer.Serialize(record.Summary),
                        ChangeReason: JsonSerializer.Serialize(new
                        {
                            k = record.ReasonKey,
                            p = new { count = toCreate.Count.ToString(CultureInfo.InvariantCulture) },
                        }),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return ids;
    }
}
