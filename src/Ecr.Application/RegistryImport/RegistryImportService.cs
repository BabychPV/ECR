using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Errors;

namespace Ecr.Application.RegistryImport;

/// <summary>Stored parents of one owner a replace touches.</summary>
/// <param name="OwnerKey">Owner.</param>
/// <param name="Label">Human label (permit number).</param>
/// <param name="Parents">Parent rows.</param>
/// <param name="Children">Child rows.</param>
public sealed record OwnerReplace(string OwnerKey, string? Label, int Parents, int Children);

/// <summary>What the apply will replace; shown BEFORE it (legacy semantics with an explicit confirmation).</summary>
/// <param name="ThisOwner">The owner's own rows in the bucket (all of them are replaced).</param>
/// <param name="OtherOwners">Rows of OTHER owners for the same keys (superseded), per owner.</param>
/// <param name="ConfirmationRequired">Other owners are touched: the apply needs their keys in <see cref="ImportRequest.ConfirmedOtherOwners"/>.</param>
public sealed record ReplaceScope(OwnerReplace ThisOwner, IReadOnlyList<OwnerReplace> OtherOwners, bool ConfirmationRequired);

/// <summary>Counts of the diff between the file and the stored rows of the owner.</summary>
/// <param name="NewParents">In the file only.</param>
/// <param name="ChangedParents">In both, different.</param>
/// <param name="UnchangedParents">In both, equal.</param>
/// <param name="RemovedParents">Stored for the owner in the bucket, not in the file (deleted by the replace).</param>
/// <param name="SkippedParents">Left out by a human decision.</param>
/// <param name="Children">Child rows that will be inserted.</param>
public sealed record ImportDiffCounts(
    int NewParents, int ChangedParents, int UnchangedParents, int RemovedParents, int SkippedParents, int Children);

/// <summary>A referenced code the target registry does not have.</summary>
/// <param name="Kind">Reference kind.</param>
/// <param name="Code">Normalised code.</param>
/// <param name="Row">First Excel row.</param>
/// <param name="GroupLabel">Group-row label above (the site), for "create under".</param>
/// <param name="Resolved">The decision taken, if any.</param>
public sealed record MissingReference(string Kind, string Code, int Row, string? GroupLabel, MissingAction? Resolved);

/// <summary>What a preview shows.</summary>
public sealed record ImportPreview(
    ImportHeaderReport? Header,
    ImportStats Stats,
    IReadOnlyList<ImportGroupRow> Groups,
    IReadOnlyList<ImportIssue> Issues,
    int DroppedIssues,
    ImportDiffCounts Diff,
    IReadOnlyList<MissingReference> Missing,
    ReplaceScope Replace,
    bool CanApply,
    string Fingerprint);

/// <summary>Caller's choices for a preview or a commit.</summary>
/// <param name="Scope">Owner and bucket.</param>
/// <param name="Resolutions">Decisions about missing references.</param>
/// <param name="ConfirmedOtherOwners">Owners (keys) whose rows the human agreed to replace.</param>
/// <param name="ExpectedFingerprint">Fingerprint the preview showed; a commit that would apply something else is refused.</param>
/// <param name="FileSha256">Hash of the uploaded file for the audit trail.</param>
/// <param name="ProfileCode">Profile used, for the audit trail.</param>
public sealed record ImportRequest(
    ImportScope Scope,
    IReadOnlyList<MissingResolution>? Resolutions = null,
    IReadOnlyList<string>? ConfirmedOtherOwners = null,
    string? ExpectedFingerprint = null,
    string? FileSha256 = null,
    string? ProfileCode = null);

/// <summary>Result of a commit.</summary>
/// <param name="Plan">The plan that was applied.</param>
/// <param name="Diff">Counts.</param>
public sealed record ImportCommitResult(ImportApplyPlan Plan, ImportDiffCounts Diff);

/// <summary>
/// Preview and commit over a parsed sheet. The commit rebuilds the SAME plan with the SAME code as the preview
/// (preview == commit), refuses anything with an error (all or nothing) and hands ONE plan to the target.
/// </summary>
public sealed class RegistryImportService(IRegistryImportTarget target, IEnumerable<IReferenceResolver> resolvers)
{
    private readonly Dictionary<string, IReferenceResolver> _resolvers =
        resolvers.ToDictionary(r => r.Kind, StringComparer.Ordinal);

    /// <summary>Builds the preview. Nothing is written.</summary>
    public async Task<ImportPreview> PreviewAsync(
        IImportGrid grid, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        var built = await BuildAsync(grid, spec, request, ct).ConfigureAwait(false);
        return built.Preview;
    }

    /// <summary>Applies the import, or refuses it as a whole.</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-IMP-0422</c>: <c>regRejected</c>, <c>regPlanChanged</c>, <c>regReplaceNotConfirmed</c>.</exception>
    public async Task<ImportCommitResult> CommitAsync(
        IImportGrid grid, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        var built = await BuildAsync(grid, spec, request, ct).ConfigureAwait(false);
        var preview = built.Preview;

        if (!preview.CanApply)
        {
            var errors = preview.Issues.Where(i => i.Severity == ImportSeverity.Error).ToList();
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"The import has {errors.Count} error(s): nothing was written.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = RegistryImportKeys.Rejected,
                    ["errorCount"] = errors.Count.ToString(CultureInfo.InvariantCulture),
                    ["issues"] = errors.Take(20).ToList(),
                });
        }

        if (request.ExpectedFingerprint is { } expected
            && !string.Equals(expected, preview.Fingerprint, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "The data changed since the preview: build the preview again.",
                new Dictionary<string, object?> { ["messageKey"] = RegistryImportKeys.PlanChanged });
        }

        var confirmed = (request.ConfirmedOtherOwners ?? []).ToHashSet(StringComparer.Ordinal);
        var unconfirmed = preview.Replace.OtherOwners.Where(o => !confirmed.Contains(o.OwnerKey)).ToList();

        if (unconfirmed.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "Rows of other owners would be replaced and were not confirmed.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = RegistryImportKeys.ReplaceNotConfirmed,
                    ["owners"] = string.Join(", ", unconfirmed.Select(o => o.Label ?? o.OwnerKey)),
                });
        }

        await target.ApplyAsync(built.Plan, ct).ConfigureAwait(false);
        return new ImportCommitResult(built.Plan, preview.Diff);
    }

    private sealed record Built(ImportPreview Preview, ImportApplyPlan Plan);

    private async Task<Built> BuildAsync(
        IImportGrid grid, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parsed = RegistryImportParser.Parse(grid, spec);
        var issues = new List<ImportIssue>(parsed.Issues);
        var resolutions = (request.Resolutions ?? []).ToDictionary(r => (r.Kind, r.Code));

        // ---- references ----
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var creations = new List<ReferenceCreation>();
        var missing = new List<MissingReference>();

        foreach (var kind in spec.Fields.Where(f => f.Reference is not null).Select(f => f.Reference!).Distinct(StringComparer.Ordinal))
        {
            var parentField = spec.Fields.FirstOrDefault(f => f.Reference == kind && f.Level == ImportLevel.Parent);
            var childField = spec.Fields.FirstOrDefault(f => f.Reference == kind && f.Level == ImportLevel.Child);

            var occurrences = new Dictionary<string, (int Row, ImportedParent Parent)>(StringComparer.Ordinal);
            foreach (var parent in parsed.Parents)
            {
                if (parentField is not null && parent.Values.GetValueOrDefault(parentField.Name) is string pc)
                {
                    occurrences.TryAdd(pc, (parent.Row, parent));
                }

                if (childField is not null)
                {
                    foreach (var child in parent.Children)
                    {
                        if (child.Values.GetValueOrDefault(childField.Name) is string cc)
                        {
                            occurrences.TryAdd(cc, (child.Row, parent));
                        }
                    }
                }
            }

            if (occurrences.Count == 0)
            {
                continue;
            }

            if (!_resolvers.TryGetValue(kind, out var resolver))
            {
                issues.Add(new ImportIssue(
                    ImportSeverity.Warning, RegistryImportKeys.ReferenceUnchecked, null, null, null,
                    new Dictionary<string, string> { ["kind"] = kind }));
                continue;
            }

            var existing = await resolver.FindExistingAsync([.. occurrences.Keys], request.Scope, ct).ConfigureAwait(false);

            foreach (var (code, at) in occurrences.Where(o => !existing.Contains(o.Key)).OrderBy(o => o.Value.Row))
            {
                var isParentLevel = parentField is not null && at.Parent.Values.GetValueOrDefault(parentField.Name) as string == code;
                resolutions.TryGetValue((kind, code), out var decision);
                var action = isParentLevel ? decision?.Action : null;

                missing.Add(new MissingReference(kind, code, at.Row, at.Parent.GroupLabel, action));

                if (action == MissingAction.Skip)
                {
                    skipped.Add(at.Parent.Key);
                }
                else if (action == MissingAction.Create)
                {
                    creations.Add(new ReferenceCreation(kind, code, decision!.CreateUnder, at.Parent.Row, at.Parent.GroupLabel, at.Parent.Values));
                }
                else
                {
                    issues.Add(new ImportIssue(
                        ImportSeverity.Error, RegistryImportKeys.ReferenceMissing, at.Row, null, null,
                        new Dictionary<string, string> { ["kind"] = kind, ["key"] = code }));
                }
            }
        }

        var inserted = parsed.Parents.Where(p => !skipped.Contains(p.Key)).ToList();

        // ---- stored rows, diff, replace scope ----
        var stored = await target
            .ReadExistingAsync(request.Scope, [.. parsed.Parents.Select(p => p.Key)], ct)
            .ConfigureAwait(false);

        var own = stored.Where(s => s.OwnerKey == request.Scope.OwnerKey).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var insertKeys = inserted.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var others = stored
            .Where(s => s.OwnerKey != request.Scope.OwnerKey && insertKeys.Contains(s.Key))
            .ToList();

        var added = 0;
        var changed = 0;
        var same = 0;
        foreach (var parent in inserted)
        {
            if (!own.TryGetValue(parent.Key, out var old))
            {
                added++;
            }
            else if (Equivalent(parent, old))
            {
                same++;
            }
            else
            {
                changed++;
            }
        }

        var removed = own.Keys.Count(k => !insertKeys.Contains(k));
        var diff = new ImportDiffCounts(added, changed, same, removed, skipped.Count, inserted.Sum(p => p.Children.Count));

        var thisOwner = new OwnerReplace(
            request.Scope.OwnerKey,
            own.Values.Select(o => o.OwnerLabel).FirstOrDefault(l => l is not null),
            own.Count,
            own.Values.Sum(o => o.Children.Count));

        var otherOwners = others
            .GroupBy(o => o.OwnerKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OwnerReplace(g.Key, g.Select(x => x.OwnerLabel).FirstOrDefault(l => l is not null), g.Count(), g.Sum(x => x.Children.Count)))
            .ToList();

        var replace = new ReplaceScope(thisOwner, otherOwners, otherOwners.Count > 0);

        var delete = own.Values.Select(o => new ExistingRef(o.OwnerKey, o.Key))
            .Concat(others.Select(o => new ExistingRef(o.OwnerKey, o.Key)))
            .OrderBy(d => d.OwnerKey, StringComparer.Ordinal)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

        var fingerprint = Fingerprint(request.Scope, delete, inserted, creations);
        var canApply = !issues.Any(i => i.Severity == ImportSeverity.Error);

        var stats = parsed.Stats with
        {
            Parents = inserted.Count,
            Children = inserted.Sum(p => p.Children.Count),
        };

        var plan = new ImportApplyPlan(
            request.Scope, delete, inserted, creations, fingerprint, request.FileSha256, request.ProfileCode, stats);

        var preview = new ImportPreview(
            parsed.Header, parsed.Stats, parsed.Groups, issues, parsed.DroppedIssues, diff, missing, replace, canApply, fingerprint);

        return new Built(preview, plan);
    }

    // ---------- equality and fingerprint ----------

    private static bool Equivalent(ImportedParent file, ExistingParent stored)
    {
        if (!SameValues(file.Values, stored.Values) || file.Children.Count != stored.Children.Count)
        {
            return false;
        }

        var storedChildren = stored.Children.ToDictionary(c => c.Key, StringComparer.Ordinal);
        return file.Children.All(c => storedChildren.TryGetValue(c.Key, out var s) && SameValues(c.Values, s.Values));
    }

    private static bool SameValues(IReadOnlyDictionary<string, object?> file, IReadOnlyDictionary<string, object?> stored)
        => file.All(p => SameValue(p.Value, stored.GetValueOrDefault(p.Key)));

    private static bool SameValue(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (IsNumber(a) && IsNumber(b))
        {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        }

        return Canon(a) == Canon(b);
    }

    private static bool IsNumber(object o) => o is decimal or long or int or short or double;

    private static string Canon(object? value) => value switch
    {
        null => "~",
        decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
        long or int or short => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("0.############################", CultureInfo.InvariantCulture),
        double d => ((decimal)d).ToString("0.############################", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "~",
    };

    private static string Fingerprint(
        ImportScope scope, IReadOnlyList<ExistingRef> delete, IReadOnlyList<ImportedParent> insert, IReadOnlyList<ReferenceCreation> create)
    {
        var sb = new StringBuilder();
        sb.Append("S|").Append(scope.OwnerKey).Append('|').Append(scope.ScopeKey).Append('\n');

        foreach (var d in delete)
        {
            sb.Append("D|").Append(d.OwnerKey).Append('|').Append(d.Key).Append('\n');
        }

        foreach (var c in create.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal))
        {
            sb.Append("C|").Append(c.Kind).Append('|').Append(c.Code).Append('|').Append(c.UnderNode).Append('\n');
        }

        foreach (var p in insert)
        {
            sb.Append("P|").Append(p.Key).Append('|').Append(p.GroupLabel).Append('|').Append(Values(p.Values)).Append('\n');
            foreach (var c in p.Children)
            {
                sb.Append("c|").Append(c.Key).Append('|').Append(Values(c.Values)).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Values(IReadOnlyDictionary<string, object?> values)
        => string.Join(';', values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + "=" + Canon(v.Value)));
}
