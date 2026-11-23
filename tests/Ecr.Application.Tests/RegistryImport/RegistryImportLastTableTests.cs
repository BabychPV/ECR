using Ecr.Application.RegistryImport;
using Ecr.TestKit;
using Xunit;
using static Ecr.Application.Tests.RegistryImport.EraGrid;

namespace Ecr.Application.Tests.RegistryImport;

/// <summary>
/// ✎ D-569 (P-1123-1, людина 23.11: «загальне правило одна остання завантажена таблиця актуальна»): the owner's last uploaded table
/// is the only current one. Every stored row of the owner — in every bucket — is replaced, nothing is carried over, no stale row
/// or duplicate survives, and the preview lists per parent and child what is added, changed (field: old → new) or removed.
/// </summary>
public sealed class RegistryImportLastTableTests
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

    private static RegistryImportService Service(FakeRegistryImportTarget target)
        => new(target, [
            new FakeReferenceResolver(INaeResolver.NaeKind, ["0003", "0004", "6001", "7001"]),
            new FakeReferenceResolver(ISubstanceResolver.SubstanceKind, ["0333", "2735", "2902"]),
        ]);

    private static ImportRequest Request(IReadOnlyList<string>? confirmed = null)
        => new(Scope, null, confirmed, null, "FILEHASH", "ERA_TABLE_33", "params.xlsx", "{\"spec\":1}");

    private static ExistingParent Row(string owner, string key, string? scope = null, params string[] children)
        => new(owner, "P-" + owner, key, new Dictionary<string, object?> { ["sourceName"] = "old " + key },
            [.. children.Select(c => new ExistingChild(c, new Dictionary<string, object?> { ["tonsPerYear"] = 1m }))], scope);

    [Fact]
    public async Task Rows_of_the_owner_in_OTHER_buckets_are_removed_listed_with_their_bucket_and_deleted_by_the_plan()
    {
        var target = new FakeRegistryImportTarget();
        // The permit holds an earlier table for another region / area (different NAE).
        target.Stored.Add(Row("permit-1", "0901", "Mangistau / -", "0333", "2902"));
        target.Stored.Add(Row("permit-1", "0902", "Mangistau / -", "0333"));

        var preview = await Service(target).PreviewAsync(File(), Era, Request(), default);

        Assert.True(preview.CanApply);
        Assert.Equal(2, preview.Replace.ThisOwner.Parents);
        Assert.Equal(3, preview.Diff.NewParents);
        Assert.Equal(2, preview.Diff.RemovedParents);
        Assert.Equal(3, preview.Diff.RemovedChildren);
        var rows = preview.Rows!;
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Removed, Key: "0901", ChildKey: null, Scope: "Mangistau / -" });
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Removed, Key: "0901", ChildKey: "2902" });
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Added, Key: "0003", ChildKey: "2735" });

        var result = await Service(target).CommitAsync(File(), Era, Request(), default);

        // Only the rows of the last table remain; the plan carried the file name and the specification for the audit.
        Assert.Equal(["0003", "0004", "6001"], target.Stored.Where(s => s.OwnerKey == "permit-1").Select(s => s.Key).Order());
        Assert.Equal(2, result.Plan.Delete.Count(d => d.Scope == "Mangistau / -"));
        Assert.Equal("params.xlsx", result.Plan.FileName);
        Assert.Equal("{\"spec\":1}", result.Plan.SpecJson);
    }

    [Fact]
    public async Task A_second_stored_row_of_the_same_key_is_removed_so_no_duplicate_survives()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target);
        await service.CommitAsync(File(), Era, Request(), default);
        // A duplicate left by an older import (same NAE twice in the permit).
        target.Stored.Add(Row("permit-1", "0003", null, "0333"));

        var preview = await service.PreviewAsync(File(), Era, Request(), default);

        Assert.Equal(1, preview.Diff.RemovedParents);
        Assert.Equal(4, preview.Replace.ThisOwner.Parents);
        await service.CommitAsync(File(), Era, Request(), default);
        var mine = target.Stored.Where(s => s.OwnerKey == "permit-1").ToList();
        Assert.Equal(3, mine.Count);
        Assert.Equal(mine.Count, mine.Select(s => s.Key).Distinct().Count());
    }

    [Fact]
    public async Task A_stored_value_the_file_does_not_carry_is_a_change_to_empty_not_kept()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target);
        await service.CommitAsync(File(), Era, Request(), default);

        // «Цех» stored on 0003 by an earlier table (an added column the new file does not have).
        var stored = target.Stored.Single(p => p.Key == "0003");
        target.Stored[target.Stored.IndexOf(stored)] = stored with
        {
            Values = new Dictionary<string, object?>(stored.Values) { ["FILE_B"] = "WS-1" },
        };

        var preview = await service.PreviewAsync(File(), Era, Request(), default);

        Assert.Equal(1, preview.Diff.ChangedParents);
        var change = Assert.Single(preview.Rows!, r => r.Kind == ImportRowChangeKind.Changed);
        Assert.Equal("0003", change.Key);
        Assert.Null(change.ChildKey);
        var field = Assert.Single(change.Fields);
        Assert.Equal(new ImportFieldChange("FILE_B", "WS-1", null), field);
    }

    [Fact]
    public async Task The_preview_lists_added_changed_and_removed_parents_and_children_with_old_and_new_values()
    {
        var target = new FakeRegistryImportTarget();
        var service = Service(target);
        await service.CommitAsync(File(), Era, Request(), default);

        // The new table: 0003 loses its oil row and gets a new t/year for H2S; 0004 is gone; 7001 is new.
        var next = new EraGrid()
            .Group("Site one")
            .Source(3d, "Source A", "Т", [Sub("0333", "H2S", 0.0024d, null, 0.5d)], diameter: 0.25d)
            .Group("Site two")
            .Source(6001d, "Source C", "T", [Sub("2902", "Dust", 0.0007d)])
            .Source(7001d, "Source D", "T", [Sub("2902", "Dust", 0.1d)])
            .Grid;

        var preview = await service.PreviewAsync(next, Era, Request(), default);

        Assert.Equal((1, 1, 1, 1), (preview.Diff.NewParents, preview.Diff.ChangedParents, preview.Diff.UnchangedParents, preview.Diff.RemovedParents));
        Assert.Equal((1, 1, 2), (preview.Diff.NewChildren, preview.Diff.ChangedChildren, preview.Diff.RemovedChildren));
        var rows = preview.Rows!;
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Added, Key: "7001", ChildKey: null });
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Added, Key: "7001", ChildKey: "2902" });
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Removed, Key: "0004", ChildKey: null });
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Removed, Key: "0004", ChildKey: "0333" });
        Assert.Contains(rows, r => r is { Kind: ImportRowChangeKind.Removed, Key: "0003", ChildKey: "2735" });
        var tons = Assert.Single(rows, r => r is { Kind: ImportRowChangeKind.Changed, Key: "0003", ChildKey: "0333" });
        Assert.Equal(new ImportFieldChange("tonsPerYear", "0.07", "0.5"), Assert.Single(tons.Fields));
        Assert.DoesNotContain(rows, r => r.Key == "6001");
        Assert.Equal(0, preview.RowsDropped);
    }

    [Fact]
    public async Task Rows_of_OTHER_owners_for_the_same_key_still_need_the_confirmation()
    {
        var target = new FakeRegistryImportTarget();
        target.Stored.Add(Row("permit-2", "0003", null, "0333"));

        var preview = await Service(target).PreviewAsync(File(), Era, Request(), default);

        Assert.True(preview.Replace.ConfirmationRequired);
        Assert.Equal("permit-2", Assert.Single(preview.Replace.OtherOwners).OwnerKey);
        // Another owner's row is not this owner's «removed»: it is the confirmed replacement.
        Assert.Equal(0, preview.Diff.RemovedParents);
        Assert.Equal(0, preview.Diff.RemovedChildren);
        // …but the preview still shows exactly which rows of which permit the confirmed replace takes.
        Assert.Contains(preview.Rows!, r => r is { Kind: ImportRowChangeKind.Removed, Key: "0003", ChildKey: null, OwnerLabel: "P-permit-2" });
        Assert.Contains(preview.Rows!, r => r is { Kind: ImportRowChangeKind.Removed, Key: "0003", ChildKey: "0333", OwnerLabel: "P-permit-2" });
        await Assert.ThrowsAsync<Ecr.Application.Errors.BusinessRuleException>(() => Service(target).CommitAsync(File(), Era, Request(), default));
        Assert.Single(target.Stored);
    }
}
