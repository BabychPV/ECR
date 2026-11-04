using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// NAE restore/purge (human 04.11): every foreign key onto <c>dic.RegistryEntry</c> in the EF model is decided — either a
/// where-used kind of <c>IRegistryStore.FindHardReferencesAsync</c> (<see cref="RegistryEntryHardReferences.CheckedForeignKeys"/>)
/// or a row the entry owns and the purge deletes with it (<see cref="RegistryEntryHardReferences.OwnedForeignKeys"/>).
/// </summary>
/// <remarks>
/// ⛔ A new FK onto registry entries without a decision here would let the purge reach the DELETE and fail on SQL 547 (caught,
/// <c>409 entryUsed</c> kind <c>other</c>) — or, for a non-FK id column, delete what is still used. The DB-level twin (sys.foreign_keys
/// of the deployed schema, which also sees FKs from raw <c>Sql/*.sql</c>) is <c>RegistryRestorePurgeHttpTests</c>.
/// Mutation proof (local): remove <c>FK_SEVM_Entry</c> from <c>CheckedForeignKeys</c> → this test is red naming it.
/// </remarks>
public sealed class RegistryEntryForeignKeyCoverageTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Every_foreign_key_onto_registry_entries_is_checked_or_owned()
    {
        using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(guard-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);

        var names = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(RegistryEntry))
            .Select(fk => fk.GetConstraintName() ?? $"{fk.DeclaringEntityType.ClrType.Name}.{string.Join(",", fk.Properties.Select(p => p.Name))}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(names);

        var undecided = names
            .Where(n => !RegistryEntryHardReferences.CheckedForeignKeys.ContainsKey(n) && !RegistryEntryHardReferences.OwnedForeignKeys.Contains(n))
            .ToList();
        Assert.True(
            undecided.Count == 0,
            "FK onto dic.RegistryEntry without a where-used kind or an owned-row decision (RegistryEntryHardReferences): "
            + string.Join(", ", undecided));

        // And no stale decision: a listed FK that no longer exists is a lie in the guard.
        var stale = RegistryEntryHardReferences.CheckedForeignKeys.Keys.Concat(RegistryEntryHardReferences.OwnedForeignKeys)
            .Where(n => !names.Contains(n, StringComparer.Ordinal))
            .ToList();
        Assert.True(stale.Count == 0, "Listed FK not in the model: " + string.Join(", ", stale));
    }
}
