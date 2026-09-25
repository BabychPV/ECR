// tests/Ecr.Api.Tests/RegistryEntryImportQueryCountTests.cs
using System.Globalization;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
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

namespace Ecr.Api.Tests;

/// <summary>
/// <c>B-10</c>: імпорт CSV довідника читає базу СТАЛИМ набором запитів —
/// скільки б рядків не було у файлі.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ На стенді 80 рядків із двома <c>Lookup</c>-полями давали 245
/// <c>SELECT TOP(1) … dic.RegistryEntry</c>: 80 — пошук запису за кодом, 160 —
/// повторна перевірка цілі посилання (<c>RequireLookupTargetAsync</c> →
/// <c>FindEntryAsync</c>) повз наявний кеш кодів, — і ще 80 <c>ListValues</c>.
/// </para>
/// <para>
/// ⚠ Замір — <c>DbCommandCounter</c> (EF-перехоплювач): увесь шлях читання
/// імпорту EF-овий. <c>dryRun</c> навмисно: N+1 був у ЧИТАННІ, а запис
/// (пакетні <c>INSERT</c> EF і журнал) ріс би з N законно й робив би
/// рівність нечесною. Журнал підмінено; одна подія <c>aud.SecurityEvent</c>
/// на змінений запис лишається (порт <c>IAuditWriter</c> пакетного методу не
/// має) — див. відкрите питання в коміті.
/// </para>
/// <para>
/// ⛔ Мутації, що валять тест (перевірено перезбіркою; N=5 / N=40 проти 4 / 4
/// після виправлення): код до виправлення — 23 / 146; повернути в циклі
/// рядків <c>FindEntryByCodeAsync</c> замість пакетного словника — 9 / 44;
/// не передати <c>LookupTargets</c> у <c>RegistryValuesPrefetch</c> —
/// 14 / 84 (повторна перевірка кожної цілі посилання).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryImportQueryCountTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B-10")]
    public async Task Число_запитів_імпорту_не_росте_з_кількістю_рядків()
    {
        var five = await MeasureAsync(rows: 5).ConfigureAwait(true);
        var forty = await MeasureAsync(rows: 40).ConfigureAwait(true);

        // ⛔ Без цього рівність нижче була б зеленою і для імпорту, що нічого
        // не прочитав: звіт мусить бачити і наявні записи, і нові.
        Assert.Empty(five.Report.Errors);
        Assert.Empty(forty.Report.Errors);
        Assert.Equal((3, 2), (five.Report.Added, five.Report.Updated));
        Assert.Equal((20, 20), (forty.Report.Added, forty.Report.Updated));

        Assert.True(
            five.Queries == forty.Queries,
            $"N=5 дав {five.Queries}, N=40 дав {forty.Queries}.\nN=5:\n{five.Detail}\nN=40:\n{forty.Detail}");

        // Рівно чотири: опис довідника, записи за кодами, записи-цілі посилань
        // (один довідник-джерело), значення наявних записів.
        Assert.True(forty.Queries == 4, $"очікувалося 4 запити, а було {forty.Queries}:\n{forty.Detail}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B-10")]
    public async Task Посилання_на_видалений_запис_відхиляється_і_в_пакетному_шляху()
    {
        // ⚠ Пакет читає цілі посилань за КОДОМ і бачить видалені логічно записи
        // так само, як поштучний FindEntryByCodeAsync; відсікає їх та сама
        // перевірка RequireLookupTargetAsync — тепер на вже прочитаному
        // об'єкті. Тест тримає, що пакет не обійшов цю перевірку.
        var fixture = await SeedAsync(existing: 0, deletedRefIndex: 0).ConfigureAwait(true);

        var csv = new StringBuilder("code,Name,RefA,RefB\r\n")
            .Append(CultureInfo.InvariantCulture, $"N{fixture.Tag}0,n,{fixture.RefCodes[0]},{fixture.RefCodes[1]}\r\n")
            .ToString();

        await using var db = Context(counter: null);
        var report = await Handler(db).HandleAsync(
                fixture.Code, csv, Encoding.UTF8.GetByteCount(csv), 1024 * 1024, dryRun: true, CancellationToken.None)
            .ConfigureAwait(true);

        var error = Assert.Single(report.Errors);
        Assert.Equal("err.ECR-REG-0422.lookupEntryNotFound", error.MessageKey);
    }

    private async Task<(RegistryEntryImportReport Report, int Queries, string Detail)> MeasureAsync(int rows)
    {
        // Половина рядків — наявні записи (шлях ListValues), половина — нові;
        // обидва Lookup-поля посилаються на записи сусіднього довідника, коди
        // яких чергуються — як той самий дозвіл на десятках рядків.
        var existing = rows / 2;
        var fixture = await SeedAsync(existing, deletedRefIndex: null).ConfigureAwait(true);

        var csv = new StringBuilder("code,Name,RefA,RefB\r\n");
        for (var i = 0; i < rows; i++)
        {
            var code = i < existing ? fixture.ExistingCodes[i] : $"N{fixture.Tag}{i}";
            var a = fixture.RefCodes[i % fixture.RefCodes.Count];
            var b = fixture.RefCodes[(i + 1) % fixture.RefCodes.Count];
            csv.Append(CultureInfo.InvariantCulture, $"{code},name {i},{a},{b}\r\n");
        }

        var text = csv.ToString();
        var counter = new DbCommandCounter();
        await using var db = Context(counter);

        counter.Tally.Reset();
        var report = await Handler(db).HandleAsync(
                fixture.Code, text, Encoding.UTF8.GetByteCount(text), 1024 * 1024, dryRun: true, CancellationToken.None)
            .ConfigureAwait(true);
        var seen = counter.Tally.Snapshot();

        return (report, seen.Total, seen.Format());
    }

    private ImportRegistryEntriesHandler Handler(EcrDbContext db)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        return new ImportRegistryEntriesHandler(
            new RegistryStore(db),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IAuditWriter>(),
            Access(UpsertRegistryEntryHandler.Permission),
            User(),
            clock);
    }

    /// <summary>
    /// Довідник-ціль із полем <c>Name</c> і двома <c>Lookup</c>-полями на
    /// сусідній довідник із п'ятьма записами; <paramref name="existing"/>
    /// наявних записів у цілі (з уже заповненим <c>Name</c>).
    /// </summary>
    private async Task<Fixture> SeedAsync(int existing, int? deletedRefIndex)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var db = Context(counter: null);

        var source = new RegistryDef(EcrCode.Create($"B10S{tag}"), Name($"Source {tag}"), isTemporal: false);
        var target = new RegistryDef(EcrCode.Create($"B10T{tag}"), Name($"Target {tag}"), isTemporal: false);
        db.RegistryDefs.AddRange(source, target);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var refEntries = Enumerable.Range(0, 5)
            .Select(i => new RegistryEntry(source.Id, EcrCode.Create($"S{tag}{i}"), Name($"S{i}")))
            .ToList();
        if (deletedRefIndex is { } deleted)
        {
            refEntries[deleted].SoftDelete();
        }

        db.RegistryEntries.AddRange(refEntries);

        var name = new RegistryFieldDef(target.Id, EcrCode.Create("Name"), Name("Name"), CellDataType.String, 0);
        var refA = new RegistryFieldDef(target.Id, EcrCode.Create("RefA"), Name("RefA"), CellDataType.Lookup, 1);
        refA.PointTo(source.Id);
        var refB = new RegistryFieldDef(target.Id, EcrCode.Create("RefB"), Name("RefB"), CellDataType.Lookup, 2);
        refB.PointTo(source.Id);
        db.RegistryFieldDefs.AddRange(name, refA, refB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var existingEntries = Enumerable.Range(0, existing)
            .Select(i => new RegistryEntry(target.Id, EcrCode.Create($"E{tag}{i}"), Name($"E{i}")))
            .ToList();
        db.RegistryEntries.AddRange(existingEntries);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var entry in existingEntries)
        {
            var value = new RegistryValue(entry, name.Id);
            value.Set(CellDataType.String, "old", null);
            db.RegistryValues.Add(value);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Fixture(
            target.Code, tag, [.. existingEntries.Select(e => e.Code)], [.. refEntries.Select(e => e.Code)]);
    }

    private EcrDbContext Context(DbCommandCounter? counter)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"));

        if (counter is not null)
        {
            options.AddInterceptors(counter);
        }

        return new EcrDbContext(options.Options);
    }

    private static IAccessDecisionService Access(string permission)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(permission).Build());

        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns("uk");
        user.CorrelationId.Returns("b10");

        return user;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Fixture(
        string Code, string Tag, IReadOnlyList<string> ExistingCodes, IReadOnlyList<string> RefCodes);
}
