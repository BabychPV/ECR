using Ecr.Application.RegistryImport;

namespace Ecr.TestKit;

/// <summary>In-memory storage for registry import tests: stores parents per owner, applies a plan atomically.</summary>
public sealed class FakeRegistryImportTarget : IRegistryImportTarget
{
    private readonly object _gate = new();

    /// <summary>Stored parents (all owners).</summary>
    public List<ExistingParent> Stored { get; } = [];

    /// <summary>Scope bucket of each stored owner; a parent is "in the bucket" when its owner maps to the requested scope key.</summary>
    public Dictionary<string, string> BucketOfOwner { get; } = new(StringComparer.Ordinal);

    /// <summary>Plans handed to <see cref="ApplyAsync"/> (a failed apply is recorded too).</summary>
    public List<ImportApplyPlan> Applied { get; } = [];

    /// <summary>Throw this after staging the plan, to prove that nothing becomes visible.</summary>
    public Exception? FailOnApply { get; set; }

    /// <summary>Entries created by applied plans.</summary>
    public List<ReferenceCreation> Created { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<ExistingParent>> ReadExistingAsync(
        ImportScope scope, IReadOnlyCollection<string> parentKeys, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<ExistingParent> result =
            [
                .. Stored.Where(p =>
                    (p.OwnerKey == scope.OwnerKey && BucketOfOwner.GetValueOrDefault(p.OwnerKey, scope.ScopeKey) == scope.ScopeKey)
                    || (p.OwnerKey != scope.OwnerKey && parentKeys.Contains(p.Key))),
            ];

            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task ApplyAsync(ImportApplyPlan plan, CancellationToken ct)
    {
        lock (_gate)
        {
            Applied.Add(plan);

            // Stage on a copy; publish only when the whole plan went through (the contract of a real transaction).
            var staged = Stored.ToList();
            staged.RemoveAll(p => plan.Delete.Any(d => d.OwnerKey == p.OwnerKey && d.Key == p.Key));
            staged.AddRange(plan.Insert.Select(i => new ExistingParent(
                plan.Scope.OwnerKey,
                null,
                i.Key,
                i.Values,
                [.. i.Children.Select(c => new ExistingChild(c.Key, c.Values))])));

            if (FailOnApply is { } failure)
            {
                throw failure;
            }

            Stored.Clear();
            Stored.AddRange(staged);
            Created.AddRange(plan.CreateReferences);
            return Task.CompletedTask;
        }
    }
}

/// <summary>A resolver that knows a fixed set of codes.</summary>
public sealed class FakeReferenceResolver(string kind, IEnumerable<string> known) : INaeResolver, ISubstanceResolver
{
    private readonly HashSet<string> _known = new(known, StringComparer.Ordinal);

    /// <inheritdoc />
    public string Kind => kind;

    /// <summary>How many times codes were looked up.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> FindExistingAsync(
        IReadOnlyCollection<string> codes, ImportScope scope, CancellationToken ct)
    {
        Calls++;
        IReadOnlySet<string> found = codes.Where(_known.Contains).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(found);
    }
}
