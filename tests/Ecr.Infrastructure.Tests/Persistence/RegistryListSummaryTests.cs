// tests/Ecr.Infrastructure.Tests/Persistence/RegistryListSummaryTests.cs
using Ecr.Application.Common;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// UI-35: лічильники в переліку довідників — записи, чинні сьогодні; використання в шаблонах і
/// чернетка лише з <c>Registry.EditDefinition</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази: рахувати всі активні записи без <c>IsValidOn</c> — червоний «2»; віддавати
/// використання без перевірки права — червоний тест «без права null».
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryListSummaryTests(SqlServerFixture sql)
{
    private static readonly DateOnly Today = new(2026, 10, 7);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_довідників_рахує_чинні_записи_використання_і_чернетку_лише_з_правом_опису()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);

        int withEntries;
        int withDraft;
        await using (var db = sql.CreateContext())
        {
            var entries = new RegistryDef(EcrCode.Create($"RLS_{_tag}"), Text("Entries"), isTemporal: true);
            var drafted = new RegistryDef(EcrCode.Create($"RLD_{_tag}"), Text("Drafted"), isTemporal: false);
            db.RegistryDefs.AddRange(entries, drafted);
            await db.SaveChangesAsync();

            RegistryEntry Entry(string code, DateOnly? from, DateOnly? to)
            {
                var entry = new RegistryEntry(entries.Id, EcrCode.Create($"{code}_{_tag}"), Text(code), 1, DateTime.UnixEpoch);
                entry.SetValidity(from, to);
                return entry;
            }

            var inactive = Entry("INACT", null, null);
            inactive.Deactivate();
            var deleted = Entry("DEL", null, null);
            deleted.SoftDelete();

            db.RegistryEntries.AddRange(
                Entry("OPEN", null, null),
                Entry("STARTS", Today, null),               // межа «від» чинна
                Entry("ENDS", null, Today),                  // «до» виключно: сьогодні вже нечинний
                Entry("FUTURE", Today.AddDays(5), null),
                inactive,
                deleted);

            // Дві колонки одного шаблону посилаються на довідник: 2 колонки, 1 шаблон.
            foreach (var n in new[] { 30, 31 })
            {
                var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"LK{n}_{_tag}"), Text("Lookup"), n, CellDataType.Lookup);
                column.SetLookup(entries.Id);
                db.ColumnDefs.Add(column);
            }

            db.RegistryDefinitionDrafts.Add(new RegistryDefinitionDraft(
                drafted.Id, 1, "{}", "probe", 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));

            await db.SaveChangesAsync();
            withEntries = entries.Id;
            withDraft = drafted.Id;
        }

        var editor = await ListAsync(new AccessBuilder { UserId = 9 }
            .Permission("Registry.View").Permission("Registry.EditDefinition"));
        var viewer = await ListAsync(new AccessBuilder { UserId = 9 }.Permission("Registry.View"));

        var seen = editor.Single(d => d.Id == withEntries);
        Assert.Equal(2, seen.EntryCount);
        Assert.Equal(2, seen.UsedInColumns);
        Assert.Equal(1, seen.UsedInTemplates);
        Assert.False(seen.HasDraft);
        Assert.True(seen.DefinitionVersion >= 1);

        var drafts = editor.Single(d => d.Id == withDraft);
        Assert.True(drafts.HasDraft);
        Assert.Equal(0, drafts.EntryCount);
        Assert.Equal(0, drafts.UsedInTemplates);

        // ⛔ Без Registry.EditDefinition — null, а не 0: число використання й чернетка не розкриваються.
        var plain = viewer.Single(d => d.Id == withEntries);
        Assert.Equal(2, plain.EntryCount);
        Assert.Null(plain.UsedInColumns);
        Assert.Null(plain.UsedInTemplates);
        Assert.Null(plain.HasDraft);
        Assert.Null(viewer.Single(d => d.Id == withDraft).HasDraft);
    }

    private async Task<IReadOnlyList<RegistryDefDto>> ListAsync(AccessBuilder builder)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));

        await using var db = sql.CreateContext();

        return await new ListRegistriesHandler(new RegistryStore(db), access, user, clock)
            .HandleAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
