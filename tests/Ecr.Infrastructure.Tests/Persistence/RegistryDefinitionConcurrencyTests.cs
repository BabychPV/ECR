// tests/Ecr.Infrastructure.Tests/Persistence/RegistryDefinitionConcurrencyTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
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
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Борг після ФВ-8.12 (порція 1) на реальному SQL Server: <c>If-Match</c> на збереженні опису,
/// блокування рядка опису й прямий <c>EXISTS</c> гейта ретаргета.
/// </summary>
/// <remarks>
/// Мутаційні докази: без <c>RequireCurrentVersion</c> червоний
/// <c>Чужа_версія_в_If_Match_дає_409_і_опис_лишається_незмінним</c>; без читання
/// <c>RegistryValue</c> у <c>FindFieldHoldingReferenceAsync</c> — <c>Гейт_бачить_значення_видаленого_запису</c>;
/// з <c>Take(MaxEntries)</c> замість EXISTS гейт пропустив би хвіст довідника >50 000 записів
/// (доказ — прямий запит у <c>Гейт_не_залежить_від_кількості_записів_довідника</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryDefinitionConcurrencyTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Гейт_бачить_значення_видаленого_запису_а_порожнє_поле_не_блокує()
    {
        var seed = await SeedAsync(deleteOwnerEntry: true);

        await using var db = Context();
        var store = new RegistryStore(db);

        Assert.Equal(seed.LinkFieldId, await store.FindFieldHoldingReferenceAsync([seed.LinkFieldId], default));
        Assert.Null(await store.FindFieldHoldingReferenceAsync([seed.EmptyLinkFieldId], default));
        Assert.Null(await store.FindFieldHoldingReferenceAsync([], default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Гейт_не_залежить_від_кількості_записів_довідника()
    {
        // Значення лежить НЕ в першому записі: вибірка зі стелею `Take` його б не побачила на
        // довіднику, більшому за стелю. Тут перевіряється, що гейт — запит по значеннях, а не
        // перебір записів: перший (за порядком) запис значення не має.
        var seed = await SeedAsync(deleteOwnerEntry: false, extraEntriesBefore: 3);

        await using var db = Context();
        var store = new RegistryStore(db);

        Assert.Equal(seed.LinkFieldId, await store.FindFieldHoldingReferenceAsync([seed.LinkFieldId], default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Блокування_опису_каже_чи_версія_застаріла_і_вимагає_транзакції()
    {
        var seed = await SeedAsync(deleteOwnerEntry: false);

        await using var db = Context();
        var store = new RegistryStore(db);
        var uow = new UnitOfWork(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.LockDefinitionIsStaleAsync(seed.OwnerId, 1, default));

        var results = new List<bool>();
        await uow.ExecuteInTransactionAsync(
            async ct =>
            {
                results.Add(await store.LockDefinitionIsStaleAsync(seed.OwnerId, 1, ct));
                results.Add(await store.LockDefinitionIsStaleAsync(seed.OwnerId, 99, ct));
            },
            default);

        Assert.Equal([false, true], results);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Блокування_опису_тримається_до_коміту_і_друга_транзакція_його_не_обходить()
    {
        // ⛔ Мутація: прибрати `UPDLOCK, HOLDLOCK` із `LockDefinitionIsStaleAsync` — другий запит
        // проходить без очікування, `LOCK_TIMEOUT` не спрацьовує, тест червоніє. Без блокування дві
        // правки з однаковим If-Match проходять звірку версії одна одної й пишуть обидві.
        const int lockTimeoutExpired = 1222;
        var seed = await SeedAsync(deleteOwnerEntry: false);

        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = Task.Run(async () =>
        {
            await using var db1 = Context();
            await new UnitOfWork(db1).ExecuteInTransactionAsync(
                async ct =>
                {
                    Assert.False(await new RegistryStore(db1).LockDefinitionIsStaleAsync(seed.OwnerId, 1, ct));
                    locked.SetResult();
                    await release.Task;
                },
                default);
        });

        try
        {
            await Task.WhenAny(locked.Task, holder);
            Assert.True(locked.Task.IsCompletedSuccessfully, "перша транзакція не взяла блокування");

            await using var db2 = Context();
            await using var tx2 = await db2.Database.BeginTransactionAsync();
            await db2.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 500");

            var blocked = await Assert.ThrowsAnyAsync<Exception>(
                () => new RegistryStore(db2).LockDefinitionIsStaleAsync(seed.OwnerId, 1, default));
            var sqlError = blocked as Microsoft.Data.SqlClient.SqlException
                           ?? blocked.InnerException as Microsoft.Data.SqlClient.SqlException;
            Assert.NotNull(sqlError);
            Assert.Equal(lockTimeoutExpired, sqlError.Number);
        }
        finally
        {
            release.TrySetResult();
            await holder;
        }

        // Після коміту першої блокування знято: друга бере його без очікування.
        await using var db3 = Context();
        await new UnitOfWork(db3).ExecuteInTransactionAsync(
            async ct => Assert.False(await new RegistryStore(db3).LockDefinitionIsStaleAsync(seed.OwnerId, 1, ct)),
            default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Чужа_версія_в_If_Match_дає_409_і_опис_лишається_незмінним()
    {
        var seed = await SeedAsync(deleteOwnerEntry: false);

        await using (var db = Context())
        {
            var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                async () => await Handler(db).HandleAsync(seed.OwnerCode, await DtoAsync(db, seed, newTarget: null), default, "\"99\""));
            Assert.Equal("err.ECR-REG-0409.definitionChanged", error.Details!["messageKey"]);
        }

        await using var fresh = Context();
        var stored = await fresh.RegistryDefs.AsNoTracking().SingleAsync(d => d.Id == seed.OwnerId);
        Assert.Equal(1, stored.DefinitionVersion);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Версія_з_If_Match_зберігає_а_друге_збереження_з_нею_ж_відхиляється()
    {
        var seed = await SeedAsync(deleteOwnerEntry: false);

        await using (var db = Context())
        {
            var version = await Handler(db).HandleAsync(
                seed.OwnerCode, await DtoAsync(db, seed, newTarget: seed.LinkTargetId), default, "\"1\"");
            Assert.Equal(2, version);
        }

        // Та сама версія вдруге: опис уже пішов далі — це і є «втрачена правка», яку ловить заголовок.
        await using var again = Context();
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            async () => await Handler(again).HandleAsync(
                seed.OwnerCode, await DtoAsync(again, seed, newTarget: seed.LinkTargetId), default, "\"1\""));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Ретаргет_поля_зі_значенням_відхиляється_у_реальній_базі_а_порожнього_проходить()
    {
        var seed = await SeedAsync(deleteOwnerEntry: false);

        await using (var db = Context())
        {
            // `LINK` має значення (навіть у видаленого запису це було б так само): ціль не міняється.
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                async () => await Handler(db).HandleAsync(
                    seed.OwnerCode, await DtoAsync(db, seed, newTarget: seed.OtherTargetId), default, "\"1\""));
            Assert.Equal("err.ECR-REG-0422.lookupRetargetInUse", error.Details!["messageKey"]);
        }

        await using var fresh = Context();
        var link = await fresh.RegistryFieldDefs.AsNoTracking().SingleAsync(f => f.Id == seed.LinkFieldId);
        Assert.Equal(seed.LinkTargetId, link.RefRegistryDefId);
    }

    private SaveRegistryDefinitionHandler Handler(EcrDbContext db)
    {
        var store = new RegistryStore(db);
        var keys = new RegistryKeyStore(db);
        var uow = new UnitOfWork(db);
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }
            .Permission("Registry.View").Permission("Registry.EditDefinition").Permission("Registry.Publish").Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.CorrelationId.Returns("test");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));

        return new SaveRegistryDefinitionHandler(
            store, uow, new AuditWriter(db), access, user, clock, new UnitCatalog(db), keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(keys, uow));
    }

    /// <summary>Повний стан опису; поле <c>LINK</c> — з новою ціллю (<c>null</c> — як є).</summary>
    private static async Task<SaveRegistryDefinitionDto> DtoAsync(EcrDbContext db, Seed seed, int? newTarget)
    {
        var owner = await new RegistryStore(db).FindDefinitionAsync(seed.OwnerCode, default);
        var fields = owner!.Fields.OrderBy(f => f.Ordinal).Select(f => new RegistryFieldSaveDto(
            f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.Ordinal, f.IsRequired, f.IsKey,
            f.Id == seed.LinkFieldId && newTarget is not null ? newTarget : f.RefRegistryDefId, f.UnitId)).ToList();
        return new SaveRegistryDefinitionDto(fields, [], "ФВ-8.12 борг");
    }

    private async Task<Seed> SeedAsync(bool deleteOwnerEntry, int extraEntriesBefore = 0)
    {
        await using var db = Context();

        var target = new RegistryDef(EcrCode.Create($"TGT_{_tag}"), Text("Ціль"), isTemporal: false);
        var otherTarget = new RegistryDef(EcrCode.Create($"OTH_{_tag}"), Text("Інша ціль"), isTemporal: false);
        var owner = new RegistryDef(EcrCode.Create($"OWN_{_tag}"), Text("Власник"), isTemporal: false);
        db.RegistryDefs.AddRange(target, otherTarget, owner);
        await db.SaveChangesAsync();

        var name = new RegistryFieldDef(owner.Id, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 1);
        name.MarkKey(true);
        var link = new RegistryFieldDef(owner.Id, EcrCode.Create("LINK"), Text("LINK"), CellDataType.Lookup, 2);
        link.PointTo(target.Id);
        var emptyLink = new RegistryFieldDef(owner.Id, EcrCode.Create("EMPTY"), Text("EMPTY"), CellDataType.Lookup, 3);
        emptyLink.PointTo(target.Id);
        db.RegistryFieldDefs.AddRange(name, link, emptyLink);

        var targetEntry = new RegistryEntry(target.Id, EcrCode.Create("T1"), Text("T1"));
        db.RegistryEntries.Add(targetEntry);
        for (var i = 0; i < extraEntriesBefore; i++)
        {
            var filler = new RegistryEntry(owner.Id, EcrCode.Create($"A{i}"), Text($"A{i}"));
            filler.SetOrdinal(i);
            db.RegistryEntries.Add(filler);
        }

        var holder = new RegistryEntry(owner.Id, EcrCode.Create("Z1"), Text("Z1"));
        holder.SetOrdinal(1000);
        db.RegistryEntries.Add(holder);
        await db.SaveChangesAsync();

        var value = new RegistryValue(holder.Id, link.Id);
        value.Set(CellDataType.Lookup, targetEntry.Id, null);
        db.RegistryValues.Add(value);
        await db.SaveChangesAsync();

        if (deleteOwnerEntry)
        {
            holder.SoftDelete();
            await db.SaveChangesAsync();
        }

        return new Seed(owner.Id, owner.Code, link.Id, emptyLink.Id, target.Id, otherTarget.Id);
    }

    private sealed record Seed(
        int OwnerId, string OwnerCode, int LinkFieldId, int EmptyLinkFieldId, int LinkTargetId, int OtherTargetId);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);
}
