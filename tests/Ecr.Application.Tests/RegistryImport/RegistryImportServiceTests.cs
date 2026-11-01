using Ecr.Application.Errors;
using Ecr.Application.RegistryImport;
using Ecr.TestKit;
using Xunit;
using static Ecr.Application.Tests.RegistryImport.EraGrid;

namespace Ecr.Application.Tests.RegistryImport;

public sealed class RegistryImportServiceTests
{
    private static readonly RegistryImportSpec Era = BuiltInImportProfiles.EraTable33;
    private static readonly ImportScope Scope = new("permit-1", "Atyrau/Offshore");

    private static InMemoryImportGrid File(Action<EraGrid>? more = null)
    {
        var era = new EraGrid()
            .Group("Site one")
            .Source(3d, "Source A", "Т", [Sub("0333", "H2S", 0.0024d, null, 0.07d), Sub(2735d, "Oil", 0.0002d, 1.263d, 0.0063d)], diameter: 0.25d)
            .Source(4d, "Source B", "П1", [Sub(333d, "H2S", 1.6e-07d)])
            .Group("Site two")
            .Source(6001d, "Source C", "T", [Sub("2902", "Dust", 0.0007d)]);
        more?.Invoke(era);
        return era.Grid;
    }

    private static RegistryImportService Service(
        FakeRegistryImportTarget target, IReferenceResolver? nae = null, IReferenceResolver? substance = null)
        => new(target, new[] { nae, substance }.OfType<IReferenceResolver>());

    private static ImportRequest Request(
        IReadOnlyList<MissingResolution>? resolutions = null, IReadOnlyList<string>? confirmed = null, string? fingerprint = null, ImportScope? scope = null)
        => new(scope ?? Scope, resolutions, confirmed, fingerprint, "FILEHASH", "ERA_TABLE_33");

    private static FakeReferenceResolver AllNae() => new(INaeResolver.NaeKind, ["0003", "0004", "6001"]);

    private static FakeReferenceResolver AllSubstances() => new(ISubstanceResolver.SubstanceKind, ["0333", "2735", "2902"]);

    private static ExistingParent Stored(string owner, string key, string? label = null, params string[] children)
        => new(owner, label, key, new Dictionary<string, object?>(), [.. children.Select(c => new ExistingChild(c, new Dictionary<string, object?>()))]);

    // ---------- preview ----------

    [Fact]
    public async Task Preview_of_an_empty_target_is_all_new_and_writes_nothing()
    {
        var target = new FakeRegistryImportTarget();

        var preview = await Service(target, AllNae(), AllSubstances()).PreviewAsync(File(), Era, Request(), default);

        Assert.True(preview.CanApply);
        Assert.Equal(new ImportDiffCounts(3, 0, 0, 0, 0, 4), preview.Diff);
        Assert.Equal(0, preview.Replace.ThisOwner.Parents);
        Assert.False(preview.Replace.ConfirmationRequired);
        Assert.Empty(preview.Missing);
        Assert.Empty(target.Applied);
        Assert.Empty(target.Stored);
        Assert.Equal(["Site one", "Site two"], preview.Groups.Select(g => g.Label));
    }

    [Fact]
    public async Task Preview_without_resolvers_warns_that_references_were_not_checked()
    {
        var preview = await Service(new FakeRegistryImportTarget()).PreviewAsync(File(), Era, Request(), default);

        Assert.True(preview.CanApply);
        Assert.Equal(2, preview.Issues.Count(i => i.MessageKey == RegistryImportKeys.ReferenceUnchecked));
    }

    [Fact]
    public async Task References_are_checked_in_one_call_per_kind()
    {
        var nae = AllNae();
        var substances = AllSubstances();

        await Service(new FakeRegistryImportTarget(), nae, substances).PreviewAsync(File(), Era, Request(), default);

        Assert.Equal(1, nae.Calls);
        Assert.Equal(1, substances.Calls);
    }

    [Fact]
    public async Task Unchanged_changed_and_removed_rows_are_counted_against_the_owners_stored_rows()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target, AllNae(), AllSubstances());
        await service.CommitAsync(File(), Era, Request(), default);

        // Re-import the same file: everything unchanged.
        var again = await service.PreviewAsync(File(), Era, Request(), default);
        Assert.Equal(new ImportDiffCounts(0, 0, 3, 0, 0, 4), again.Diff);

        // Change a stored value, drop a source from the file, add a new one.
        var stored = target.Stored.Single(p => p.Key == "0003");
        target.Stored[target.Stored.IndexOf(stored)] = stored with
        {
            Values = new Dictionary<string, object?>(stored.Values) { ["height"] = 99m },
        };
        target.Stored.Add(Stored("permit-1", "7777", "P-1", "0333"));

        var changed = await service.PreviewAsync(
            File(e => e.Source(7001d, "New", "T", [Sub("0333", "x")])), Era, Request(), default);

        // The new source is unknown to the NAE resolver: make it known for this check.
        Assert.Equal(1, changed.Diff.ChangedParents);
        Assert.Equal(2, changed.Diff.UnchangedParents);
        Assert.Equal(1, changed.Diff.NewParents);
        Assert.Equal(1, changed.Diff.RemovedParents);
        Assert.Equal(4, changed.Replace.ThisOwner.Parents);
    }

    [Fact]
    public async Task A_child_added_or_a_child_value_changed_makes_the_parent_changed()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target, AllNae(), AllSubstances());
        await service.CommitAsync(File(), Era, Request(), default);

        var stored = target.Stored.Single(p => p.Key == "0003");
        target.Stored[target.Stored.IndexOf(stored)] = stored with { Children = [stored.Children[0]] };

        Assert.Equal(1, (await service.PreviewAsync(File(), Era, Request(), default)).Diff.ChangedParents);

        target.Stored[target.Stored.IndexOf(target.Stored.Single(p => p.Key == "0003"))] = stored with
        {
            Children = [stored.Children[0], stored.Children[1] with { Values = new Dictionary<string, object?>(stored.Children[1].Values) { ["tonsPerYear"] = 1m } }],
        };

        Assert.Equal(1, (await service.PreviewAsync(File(), Era, Request(), default)).Diff.ChangedParents);
    }

    [Fact]
    public async Task Numeric_equality_ignores_the_stored_type_and_trailing_zeros()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target, AllNae(), AllSubstances());
        await service.CommitAsync(File(), Era, Request(), default);

        var stored = target.Stored.Single(p => p.Key == "0003");
        var values = new Dictionary<string, object?>(stored.Values) { ["height"] = 9.50m, ["hoursPerYear"] = 8784L, ["quantity"] = 131d };
        target.Stored[target.Stored.IndexOf(stored)] = stored with { Values = values };

        Assert.Equal(3, (await service.PreviewAsync(File(), Era, Request(), default)).Diff.UnchangedParents);
    }

    // ---------- replace scope ----------

    [Fact]
    public async Task Rows_of_other_owners_for_the_same_source_are_listed_with_counts_and_need_confirmation()
    {
        var target = new FakeRegistryImportTarget();
        target.Stored.Add(Stored("permit-2", "0003", "KZ46", "0333", "2735"));
        target.Stored.Add(Stored("permit-2", "0004", "KZ46", "0333"));
        target.Stored.Add(Stored("permit-3", "6001", "KZ79", "2902"));
        target.Stored.Add(Stored("permit-3", "9999", "KZ79", "2902"));    // not in the file: untouched

        var preview = await Service(target, AllNae(), AllSubstances()).PreviewAsync(File(), Era, Request(), default);

        Assert.True(preview.Replace.ConfirmationRequired);
        Assert.Equal(
            [new OwnerReplace("permit-2", "KZ46", 2, 3), new OwnerReplace("permit-3", "KZ79", 1, 1)],
            preview.Replace.OtherOwners);
        Assert.True(preview.CanApply);
    }

    [Fact]
    public async Task Commit_without_confirming_other_owners_is_refused_and_writes_nothing()
    {
        var target = new FakeRegistryImportTarget();
        target.Stored.Add(Stored("permit-2", "0003", "KZ46", "0333"));
        target.Stored.Add(Stored("permit-3", "6001", "KZ79", "2902"));
        var service = Service(target, AllNae(), AllSubstances());

        var none = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.CommitAsync(File(), Era, Request(), default));
        var partial = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.CommitAsync(File(), Era, Request(confirmed: ["permit-2"]), default));

        Assert.Equal(RegistryImportKeys.ReplaceNotConfirmed, none.Details!["messageKey"]);
        Assert.Equal("KZ46, KZ79", none.Details["owners"]);
        Assert.Equal("KZ79", partial.Details!["owners"]);
        Assert.Empty(target.Applied);
        Assert.Equal(2, target.Stored.Count);
    }

    [Fact]
    public async Task Confirmed_commit_replaces_this_owners_rows_and_the_confirmed_owners_rows_only()
    {
        var target = new FakeRegistryImportTarget();
        target.Stored.Add(Stored("permit-1", "5555", "P-1", "0333"));        // this owner, not in file: replaced (removed)
        target.Stored.Add(Stored("permit-2", "0003", "KZ46", "0333"));       // other owner, same key: superseded
        target.Stored.Add(Stored("permit-2", "8888", "KZ46", "0333"));       // other owner, other key: untouched
        var service = Service(target, AllNae(), AllSubstances());

        var result = await service.CommitAsync(File(), Era, Request(confirmed: ["permit-2"]), default);

        Assert.Equal(
            [new ExistingRef("permit-1", "5555"), new ExistingRef("permit-2", "0003")],
            result.Plan.Delete);
        Assert.Equal(
            ["permit-1:0003", "permit-1:0004", "permit-1:6001", "permit-2:8888"],
            target.Stored.Select(p => p.OwnerKey + ":" + p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Another_owners_rows_in_another_bucket_of_this_owner_are_not_replaced()
    {
        var target = new FakeRegistryImportTarget();
        target.Stored.Add(Stored("permit-1", "4242", "P-1", "0333"));
        target.BucketOfOwner["permit-1"] = "Mangistau";   // this owner's rows live in another bucket

        var preview = await Service(target, AllNae(), AllSubstances()).PreviewAsync(File(), Era, Request(), default);

        Assert.Equal(0, preview.Diff.RemovedParents);
        Assert.Equal(0, preview.Replace.ThisOwner.Parents);
    }

    // ---------- references ----------

    [Fact]
    public async Task A_source_missing_in_the_tree_blocks_the_import_until_it_is_resolved()
    {
        var target = new FakeRegistryImportTarget();
        var nae = new FakeReferenceResolver(INaeResolver.NaeKind, ["0003", "0004"]);
        var service = Service(target, nae, AllSubstances());

        var preview = await service.PreviewAsync(File(), Era, Request(), default);

        Assert.False(preview.CanApply);
        var missing = Assert.Single(preview.Missing);
        Assert.Equal(("nae", "6001", "Site two"), (missing.Kind, missing.Code, missing.GroupLabel));
        Assert.Null(missing.Resolved);
        Assert.Contains(preview.Issues, i => i.MessageKey == RegistryImportKeys.ReferenceMissing && i.Args!["key"] == "6001");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CommitAsync(File(), Era, Request(), default));
        Assert.Equal(RegistryImportKeys.Rejected, ex.Details!["messageKey"]);
        Assert.Empty(target.Applied);
    }

    [Fact]
    public async Task Skipping_a_missing_source_leaves_it_out_and_the_rest_applies()
    {
        var target = new FakeRegistryImportTarget();
        var nae = new FakeReferenceResolver(INaeResolver.NaeKind, ["0003", "0004"]);
        var service = Service(target, nae, AllSubstances());
        var skip = new[] { new MissingResolution("nae", "6001", MissingAction.Skip) };

        var preview = await service.PreviewAsync(File(), Era, Request(skip), default);
        var result = await service.CommitAsync(File(), Era, Request(skip), default);

        Assert.True(preview.CanApply);
        Assert.Equal(1, preview.Diff.SkippedParents);
        Assert.Equal(MissingAction.Skip, preview.Missing.Single().Resolved);
        Assert.Equal(["0003", "0004"], result.Plan.Insert.Select(p => p.Key));
        Assert.Equal(["0003", "0004"], target.Stored.Select(p => p.Key));
    }

    [Fact]
    public async Task Creating_a_missing_source_hands_the_target_where_and_with_which_attributes()
    {
        var target = new FakeRegistryImportTarget();
        var nae = new FakeReferenceResolver(INaeResolver.NaeKind, ["0003", "0004"]);
        var service = Service(target, nae, AllSubstances());
        var create = new[] { new MissingResolution("nae", "6001", MissingAction.Create, "node-DC05") };

        await service.CommitAsync(File(), Era, Request(create), default);

        var created = Assert.Single(target.Created);
        Assert.Equal(("nae", "6001", "node-DC05", "Site two"), (created.Kind, created.Code, created.UnderNode, created.GroupLabel));
        Assert.Equal("Source C", created.Values["sourceName"]);
        Assert.Equal(["0003", "0004", "6001"], target.Stored.Select(p => p.Key));
    }

    [Fact]
    public async Task An_unknown_substance_cannot_be_skipped_or_created_it_is_an_error()
    {
        var target = new FakeRegistryImportTarget();
        var substances = new FakeReferenceResolver(ISubstanceResolver.SubstanceKind, ["0333", "2735"]);   // 2902 unknown
        var service = Service(target, AllNae(), substances);
        var attempt = new[]
        {
            new MissingResolution("substance", "2902", MissingAction.Skip),
            new MissingResolution("substance", "2902", MissingAction.Create, "x"),
        };

        var preview = await service.PreviewAsync(File(), Era, Request(attempt.Take(1).ToList()), default);

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Issues, i => i.MessageKey == RegistryImportKeys.ReferenceMissing && i.Args!["kind"] == "substance");
        Assert.Null(preview.Missing.Single().Resolved);
    }

    // ---------- all or nothing ----------

    [Fact]
    public async Task One_unreadable_cell_refuses_the_whole_file_and_the_target_is_never_called()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target, AllNae(), AllSubstances());
        var file = File(e => e.Source(7001d, "Bad", "T", [Sub("0333", "x", gs: "oops")]));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CommitAsync(file, Era, Request(), default));

        Assert.Equal("ECR-IMP-0422", ex.ErrorCode);
        Assert.Equal(RegistryImportKeys.Rejected, ex.Details!["messageKey"]);
        Assert.Equal("2", ex.Details["errorCount"]);    // the cell and the source 7001 unknown to the tree
        Assert.Empty(target.Applied);
        Assert.Empty(target.Stored);
    }

    [Fact]
    public async Task A_failing_apply_leaves_the_target_untouched()
    {
        var target = new FakeRegistryImportTarget();
        target.Stored.Add(Stored("permit-1", "5555", "P-1", "0333"));
        target.FailOnApply = new InvalidOperationException("db down");
        var service = Service(target, AllNae(), AllSubstances());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync(File(), Era, Request(), default));

        Assert.Equal(["5555"], target.Stored.Select(p => p.Key));
        Assert.Single(target.Applied);   // handed over exactly once, as ONE plan
    }

    // ---------- preview == commit ----------

    [Fact]
    public async Task The_commit_applies_exactly_the_plan_the_preview_fingerprinted()
    {
        var target = new FakeRegistryImportTarget();
        target.Stored.Add(Stored("permit-2", "0003", "KZ46", "0333"));
        var service = Service(target, AllNae(), AllSubstances());
        var request = Request(confirmed: ["permit-2"]);

        var preview = await service.PreviewAsync(File(), Era, request, default);
        var result = await service.CommitAsync(File(), Era, request with { ExpectedFingerprint = preview.Fingerprint }, default);

        Assert.Equal(preview.Fingerprint, result.Plan.Fingerprint);
        Assert.Same(result.Plan, Assert.Single(target.Applied));
        Assert.Equal(preview.Diff, result.Diff);
        Assert.Equal("FILEHASH", result.Plan.FileSha256);
        Assert.Equal("ERA_TABLE_33", result.Plan.ProfileCode);
        Assert.Equal(preview.Diff.Children, result.Plan.Insert.Sum(p => p.Children.Count));
    }

    [Fact]
    public async Task The_fingerprint_changes_with_the_content_the_scope_and_the_stored_rows()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target, AllNae(), AllSubstances());

        var baseline = (await service.PreviewAsync(File(), Era, Request(), default)).Fingerprint;
        var sameAgain = (await service.PreviewAsync(File(), Era, Request(), default)).Fingerprint;
        var otherValue = (await service.PreviewAsync(
            File(e => e.Source(4001d, "x", "T", [Sub("0333", "x")])), Era, Request(), default)).Fingerprint;
        var otherScope = (await service.PreviewAsync(File(), Era, Request(scope: Scope with { ScopeKey = "Mangistau" }), default)).Fingerprint;
        target.Stored.Add(Stored("permit-1", "5555", "P-1"));
        var otherStored = (await service.PreviewAsync(File(), Era, Request(), default)).Fingerprint;

        Assert.Equal(baseline, sameAgain);
        Assert.NotEqual(baseline, otherValue);
        Assert.NotEqual(baseline, otherScope);
        Assert.NotEqual(baseline, otherStored);
    }

    [Fact]
    public async Task A_commit_whose_plan_differs_from_the_previewed_one_is_refused()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target, AllNae(), AllSubstances());
        var preview = await service.PreviewAsync(File(), Era, Request(), default);

        // Somebody else stored a row for this owner between the preview and the commit.
        target.Stored.Add(Stored("permit-1", "5555", "P-1"));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.CommitAsync(File(), Era, Request(fingerprint: preview.Fingerprint), default));

        Assert.Equal(RegistryImportKeys.PlanChanged, ex.Details!["messageKey"]);
        Assert.Empty(target.Applied);
    }
}
