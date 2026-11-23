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
/// <param name="NewChildren">✎ D-569: child rows in the file only (also those of new parents).</param>
/// <param name="ChangedChildren">✎ D-569: child rows in both, different.</param>
/// <param name="RemovedChildren">✎ D-569: stored child rows of the owner the file no longer has (also those of removed parents).</param>
public sealed record ImportDiffCounts(
    int NewParents,
    int ChangedParents,
    int UnchangedParents,
    int RemovedParents,
    int SkippedParents,
    int Children,
    int NewChildren = 0,
    int ChangedChildren = 0,
    int RemovedChildren = 0);

/// <summary>What happens to one row.</summary>
public enum ImportRowChangeKind
{
    /// <summary>In the file only: inserted.</summary>
    Added,

    /// <summary>In both, at least one value differs.</summary>
    Changed,

    /// <summary>Stored for the owner, not in the file: deleted.</summary>
    Removed,
}

/// <summary>One value that the apply changes.</summary>
/// <param name="Field">Field name (built-in field or added column code).</param>
/// <param name="Old">Stored value as text; <c>null</c> — empty.</param>
/// <param name="New">Value of the file as text; <c>null</c> — empty (also a column the file does not have: the last table is the truth).</param>
public sealed record ImportFieldChange(string Field, string? Old, string? New);

/// <summary>
/// ✎ D-569 (P-1123-1): one row of the preview's list «what the new table replaces» — a parent (<paramref name="ChildKey"/> is
/// <c>null</c>) or one child of it.
/// </summary>
/// <param name="Kind">Added, changed or removed.</param>
/// <param name="Key">Parent key (NAE).</param>
/// <param name="ChildKey">Child key (substance); <c>null</c> — the row is the parent itself.</param>
/// <param name="Scope">Bucket of a removed row outside the import's bucket; <c>null</c> — the import's bucket.</param>
/// <param name="Fields">Changed values (changed rows only; empty for added and removed).</param>
/// <param name="OwnerLabel">
/// A removed row of ANOTHER owner for the same key (replaced only with the confirmation): that owner's label; <c>null</c> — this owner.
/// </param>
public sealed record ImportRowChange(
    ImportRowChangeKind Kind, string Key, string? ChildKey, string? Scope, IReadOnlyList<ImportFieldChange> Fields, string? OwnerLabel = null);

/// <summary>A referenced code the target registry does not have.</summary>
/// <param name="Kind">Reference kind.</param>
/// <param name="Code">Normalised code.</param>
/// <param name="Row">First Excel row.</param>
/// <param name="GroupLabel">Group-row label above (the site), for "create under".</param>
/// <param name="Resolved">The decision taken, if any.</param>
public sealed record MissingReference(string Kind, string Code, int Row, string? GroupLabel, MissingAction? Resolved);

/// <summary>What a preview shows.</summary>
public sealed record RegistryImportPreview(
    ImportHeaderReport? Header,
    ImportStats Stats,
    IReadOnlyList<ImportGroupRow> Groups,
    IReadOnlyList<ImportIssue> Issues,
    int DroppedIssues,
    ImportDiffCounts Diff,
    IReadOnlyList<MissingReference> Missing,
    ReplaceScope Replace,
    bool CanApply,
    string Fingerprint,
    IReadOnlyList<ImportRowChange>? Rows = null,
    int RowsDropped = 0);

/// <summary>Caller's choices for a preview or a commit.</summary>
/// <param name="Scope">Owner and bucket.</param>
/// <param name="Resolutions">Decisions about missing references.</param>
/// <param name="ConfirmedOtherOwners">Owners (keys) whose rows the human agreed to replace.</param>
/// <param name="ExpectedFingerprint">Fingerprint the preview showed; a commit that would apply something else is refused.</param>
/// <param name="FileSha256">Hash of the uploaded file for the audit trail.</param>
/// <param name="ProfileCode">Profile used, for the audit trail.</param>
/// <param name="FileName">✎ D-569: name of the uploaded file, for the audit trail.</param>
/// <param name="SpecJson">✎ D-569: the specification as JSON, for the audit trail.</param>
public sealed record ImportRequest(
    ImportScope Scope,
    IReadOnlyList<MissingResolution>? Resolutions = null,
    IReadOnlyList<string>? ConfirmedOtherOwners = null,
    string? ExpectedFingerprint = null,
    string? FileSha256 = null,
    string? ProfileCode = null,
    string? FileName = null,
    string? SpecJson = null);

/// <summary>Result of a commit.</summary>
/// <param name="Plan">The plan that was applied.</param>
/// <param name="Diff">Counts.</param>
/// <param name="Replace">What the apply replaced: the owner's own rows and the confirmed rows of other owners.</param>
public sealed record ImportCommitResult(ImportApplyPlan Plan, ImportDiffCounts Diff, ReplaceScope Replace);

/// <summary>
/// Preview and commit over a parsed sheet. The commit rebuilds the SAME plan with the SAME code as the preview
/// (preview == commit), refuses anything with an error (all or nothing) and hands ONE plan to the target.
/// </summary>
public sealed class RegistryImportService(IRegistryImportTarget target, IEnumerable<IReferenceResolver> resolvers)
{
    private readonly Dictionary<string, IReferenceResolver> _resolvers =
        resolvers.ToDictionary(r => r.Kind, StringComparer.Ordinal);

    /// <summary>Builds the preview. Nothing is written.</summary>
    public async Task<RegistryImportPreview> PreviewAsync(
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
        return new ImportCommitResult(built.Plan, preview.Diff, preview.Replace);
    }

    private sealed record Built(RegistryImportPreview Preview, ImportApplyPlan Plan);

    private async Task<Built> BuildAsync(
        IImportGrid grid, RegistryImportSpec spec, ImportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parsed = RegistryImportParser.Parse(grid, spec, ct);
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
                    var missingField = isParentLevel ? parentField!.Name : childField!.Name;
                    issues.Add(new ImportIssue(
                        ImportSeverity.Error, RegistryImportKeys.ReferenceMissing, at.Row,
                        parsed.Header?.Mapped.FirstOrDefault(m => m.Field == missingField)?.ColumnLetter, missingField,
                        new Dictionary<string, string> { ["kind"] = kind, ["key"] = code }));
                }
            }
        }

        var inserted = parsed.Parents.Where(p => !skipped.Contains(p.Key)).ToList();

        // ---- stored rows, diff, replace scope ----
        var stored = await target
            .ReadExistingAsync(request.Scope, [.. parsed.Parents.Select(p => p.Key)], ct)
            .ConfigureAwait(false);

        // ✎ D-569 (P-1123-1): the owner's last uploaded table is the only current one. EVERY stored row of the owner is replaced:
        // the rows of the import's bucket are compared with the file by key, the rows of its other buckets (and a second stored
        // row of the same key) are removed. The other owners' rows stay matched by key, as before.
        var ownAll = stored.Where(s => s.OwnerKey == request.Scope.OwnerKey).ToList();
        var own = new Dictionary<string, ExistingParent>(StringComparer.Ordinal);
        var ownElsewhere = new List<ExistingParent>();
        foreach (var row in ownAll)
        {
            if (row.Scope is not null || !own.TryAdd(row.Key, row))
            {
                ownElsewhere.Add(row);
            }
        }

        var insertKeys = inserted.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var others = stored
            .Where(s => s.OwnerKey != request.Scope.OwnerKey && insertKeys.Contains(s.Key))
            .ToList();

        var ignored = spec.Fields
            .Where(f => f.Informational || f.Name == spec.ParentKeyField || f.Name == spec.ChildKeyField)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);
        var rows = new RowChanges();
        var added = 0;
        var changed = 0;
        var same = 0;
        foreach (var parent in inserted)
        {
            if (!own.TryGetValue(parent.Key, out var old))
            {
                added++;
                rows.Add(new ImportRowChange(ImportRowChangeKind.Added, parent.Key, null, null, []));
                foreach (var child in parent.Children)
                {
                    rows.Add(new ImportRowChange(ImportRowChangeKind.Added, parent.Key, child.Key, null, []));
                }
            }
            else if (Compare(parent, old, ignored, rows))
            {
                same++;
            }
            else
            {
                changed++;
            }
        }

        var gone = own.Values.Where(o => !insertKeys.Contains(o.Key)).Concat(ownElsewhere).ToList();
        foreach (var old in gone)
        {
            rows.Add(new ImportRowChange(ImportRowChangeKind.Removed, old.Key, null, old.Scope, []));
            foreach (var child in old.Children)
            {
                rows.Add(new ImportRowChange(ImportRowChangeKind.Removed, old.Key, child.Key, old.Scope, []));
            }
        }

        // Rows of OTHER owners the confirmed replace takes: listed too (who loses what), never counted as this owner's change.
        foreach (var theirs in others)
        {
            var label = theirs.OwnerLabel ?? theirs.OwnerKey;
            rows.Add(new ImportRowChange(ImportRowChangeKind.Removed, theirs.Key, null, null, [], label));
            foreach (var child in theirs.Children)
            {
                rows.Add(new ImportRowChange(ImportRowChangeKind.Removed, theirs.Key, child.Key, null, [], label));
            }
        }

        var diff = new ImportDiffCounts(
            added, changed, same, gone.Count, skipped.Count, inserted.Sum(p => p.Children.Count),
            rows.Count(ImportRowChangeKind.Added, child: true),
            rows.Count(ImportRowChangeKind.Changed, child: true),
            rows.Count(ImportRowChangeKind.Removed, child: true));

        var thisOwner = new OwnerReplace(
            request.Scope.OwnerKey,
            ownAll.Select(o => o.OwnerLabel).FirstOrDefault(l => l is not null),
            ownAll.Count,
            ownAll.Sum(o => o.Children.Count));

        var otherOwners = others
            .GroupBy(o => o.OwnerKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OwnerReplace(g.Key, g.Select(x => x.OwnerLabel).FirstOrDefault(l => l is not null), g.Count(), g.Sum(x => x.Children.Count)))
            .ToList();

        var replace = new ReplaceScope(thisOwner, otherOwners, otherOwners.Count > 0);

        var delete = ownAll.Select(o => new ExistingRef(o.OwnerKey, o.Key, o.Scope))
            .Concat(others.Select(o => new ExistingRef(o.OwnerKey, o.Key, o.Scope)))
            .Distinct()
            .OrderBy(d => d.OwnerKey, StringComparer.Ordinal)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ThenBy(d => d.Scope, StringComparer.Ordinal)
            .ToList();

        var fingerprint = Fingerprint(request.Scope, delete, inserted, creations);
        var canApply = !issues.Any(i => i.Severity == ImportSeverity.Error);

        var stats = parsed.Stats with
        {
            Parents = inserted.Count,
            Children = inserted.Sum(p => p.Children.Count),
        };

        var plan = new ImportApplyPlan(
            request.Scope, delete, inserted, creations, fingerprint, request.FileSha256, request.ProfileCode, stats,
            request.FileName, request.SpecJson);

        var preview = new RegistryImportPreview(
            parsed.Header, parsed.Stats, parsed.Groups, issues, parsed.DroppedIssues, diff, missing, replace, canApply, fingerprint,
            rows.Kept, rows.Dropped);

        return new Built(preview, plan);
    }

    // ---------- equality and fingerprint ----------

    /// <summary>Most row changes a preview lists; the rest are only counted (<see cref="RegistryImportPreview.RowsDropped"/>).</summary>
    public const int MaxRowChanges = 20_000;

    /// <summary>The preview's list of row changes with its cap; counts every change, also the dropped ones.</summary>
    private sealed class RowChanges
    {
        private readonly Dictionary<(ImportRowChangeKind, bool), int> _counts = [];

        public List<ImportRowChange> Kept { get; } = [];

        public int Dropped { get; private set; }

        public void Add(ImportRowChange change)
        {
            if (change.OwnerLabel is null)
            {
                var key = (change.Kind, change.ChildKey is not null);
                _counts[key] = _counts.GetValueOrDefault(key) + 1;
            }

            if (Kept.Count < MaxRowChanges)
            {
                Kept.Add(change);
            }
            else
            {
                Dropped++;
            }
        }

        public int Count(ImportRowChangeKind kind, bool child) => _counts.GetValueOrDefault((kind, child));
    }

    /// <summary>
    /// Compares a file parent with the stored one and lists the differences. ✎ D-569 (P-1123-1, no carry-over): the stored row is
    /// replaced as a whole, so a stored value of a field the file does not carry counts as «→ empty»; a field of the file the store
    /// does not have (a value that is not saved) is not a difference.
    /// </summary>
    /// <returns><c>true</c> — the parent and all its children are the same.</returns>
    private static bool Compare(ImportedParent file, ExistingParent stored, ISet<string> ignored, RowChanges rows)
    {
        var same = true;
        var fields = Changes(file.Values, stored.Values, ignored);
        if (fields.Count > 0)
        {
            same = false;
            rows.Add(new ImportRowChange(ImportRowChangeKind.Changed, file.Key, null, null, fields));
        }

        var storedChildren = new Dictionary<string, ExistingChild>(StringComparer.Ordinal);
        foreach (var child in stored.Children)
        {
            if (!storedChildren.TryAdd(child.Key, child))
            {
                // A second stored row of the same substance: the replace removes it (no duplicates survive).
                same = false;
                rows.Add(new ImportRowChange(ImportRowChangeKind.Removed, file.Key, child.Key, null, []));
            }
        }

        var fileKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in file.Children)
        {
            fileKeys.Add(child.Key);
            if (!storedChildren.TryGetValue(child.Key, out var old))
            {
                same = false;
                rows.Add(new ImportRowChange(ImportRowChangeKind.Added, file.Key, child.Key, null, []));
                continue;
            }

            var childFields = Changes(child.Values, old.Values, ignored);
            if (childFields.Count > 0)
            {
                same = false;
                rows.Add(new ImportRowChange(ImportRowChangeKind.Changed, file.Key, child.Key, null, childFields));
            }
        }

        foreach (var old in storedChildren.Values.Where(c => !fileKeys.Contains(c.Key)))
        {
            same = false;
            rows.Add(new ImportRowChange(ImportRowChangeKind.Removed, file.Key, old.Key, null, []));
        }

        return same;
    }

    private static List<ImportFieldChange> Changes(
        IReadOnlyDictionary<string, object?> file, IReadOnlyDictionary<string, object?> stored, ISet<string> ignored)
    {
        var result = new List<ImportFieldChange>();
        foreach (var name in stored.Keys.Where(k => !ignored.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            var now = file.GetValueOrDefault(name);
            var was = stored[name];
            if (!SameValue(now, was))
            {
                result.Add(new ImportFieldChange(name, Shown(was), Shown(now)));
            }
        }

        return result;
    }

    private static string? Shown(object? value)
        => value is null || (value is string s && s.Length == 0) ? null : Canon(value);

    private static bool SameValue(object? a, object? b)
    {
        // An empty text is «no value» on both sides (the store keeps no row for it).
        a = a is string { Length: 0 } ? null : a;
        b = b is string { Length: 0 } ? null : b;
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (IsNumber(a) && IsNumber(b))
        {
            // The store keeps StoredScale decimals: a file number with more (Excel double 0.19091925399999998) comes back rounded
            // (0.1909192540000000), so the same file must not read as «changed». Compare as the store would keep them.
            return Stored(a) == Stored(b);
        }

        return Canon(a) == Canon(b);
    }

    private static bool IsNumber(object o) => o is decimal or long or int or short or double;

    /// <summary>
    /// Decimals the registry keeps for a number (<c>dic.RegistryValue.ValueNumeric decimal(34,16)</c>; SQL Server rounds half
    /// away from zero when it stores more).
    /// </summary>
    internal const int StoredScale = 16;

    private static decimal Stored(object number)
        => Math.Round(Convert.ToDecimal(number, CultureInfo.InvariantCulture), StoredScale, MidpointRounding.AwayFromZero);

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
            sb.Append("D|").Append(d.OwnerKey).Append('|').Append(d.Key).Append('|').Append(d.Scope).Append('\n');
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
