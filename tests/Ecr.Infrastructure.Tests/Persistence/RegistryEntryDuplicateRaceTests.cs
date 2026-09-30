// tests/Ecr.Infrastructure.Tests/Persistence/RegistryEntryDuplicateRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Finding 3: подвійний клік «Зберегти» на новому записі довідника посилає
/// два <c>POST …/entries</c> тим самим кодом майже одночасно — перший
/// <c>201</c>, другий падав НЕОБРОБЛЕНИМ <c>500</c> замість того самого
/// чистого <c>422 ECR-REG-0409</c>, який СЕКВЕНЦІЙНИЙ дублікат уже отримує
/// через пре-чек у <c>UpsertRegistryEntryHandler.CreateAsync</c>.
/// </summary>
/// <remarks>
/// ⛔ Пре-чек (<c>FindEntryByCodeAsync</c>) звіряє з ЗНІМКОМ, прочитаним на
/// початку обробки запиту (TOCTOU, той самий клас, що Q-241/Q-245): два
/// одночасні запити тим самим кодом ОБИДВА проходять пре-чек ДО того, як
/// перший закомітиться, і другий падає на <c>UQ_RegistryEntry</c> вже в
/// БАЗІ. Тест не відтворює гонитву реальним паралелізмом (джерело
/// флакі-тестів, той самий підхід, що <c>RowStoreRaceTests</c>) — досить
/// детерміновано відтворити сам конфлікт: другий <c>SaveChangesAsync</c> тим
/// самим (<c>RegistryDefId</c>, <c>Code</c>) — точнісінько шлях, яким пройшов
/// би переможений гонитви, ЩО ВЖЕ ПРОЙШОВ пре-чек, минаючи сам виклик
/// обробника (як і <c>RowStoreRaceTests</c> минає <c>CreateRowHandler</c>,
/// щоб перевірити сáме шар збереження, не пре-чек).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryDuplicateRaceTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "lane1-2-4-unhandled-500-pattern")]
    public async Task Гонитва_двох_вставок_тим_самим_кодом_дає_ECR_REG_0409_а_не_сирий_виняток_бази()
    {
        await using var db = Context();

        var def = new Domain.Entities.Configuration.RegistryDef(
            EcrCode.Create($"RACE_{_tag}"), Text("Race"), isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();

        var code = "E1";

        var winner = new RegistryEntry(def.Id, EcrCode.Create(code), Text("Перший"), 9, DateTime.UtcNow);
        db.RegistryEntries.Add(winner);
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        // Другий запис — ОКРЕМИЙ DbContext (як окремий HTTP-запит, що вже
        // пройшов свій пре-чек над знімком без переможця), той самий код у
        // тому самому довіднику: саме цей шлях доходив би необробленим до
        // ExceptionHandlingMiddleware до фіксу.
        await using var db2 = Context();
        var loser = new RegistryEntry(def.Id, EcrCode.Create(code), Text("Другий"), 9, DateTime.UtcNow);
        db2.RegistryEntries.Add(loser);

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new UnitOfWork(db2).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-REG-0409", thrown.ErrorCode);
        Assert.Contains(code, thrown.Message, StringComparison.Ordinal);

        // ⛔ Q-30x: без Details["messageKey"] подробиця доїжджала клієнту
        // сирим українським реченням незалежно від мови інтерфейсу.
        Assert.NotNull(thrown.Details);
        //
        // ⚠ Ключ `.entryCodeTakenConcurrently`, а не `.entryCodeTaken`: шаблон
        // останнього чекає `{id}` переможця, якого тут немає, і лишав фігурні
        // дужки на екрані. Жодного поля, крім `code`, новий шаблон не чекає.
        Assert.Equal("err.ECR-REG-0409.entryCodeTakenConcurrently", thrown.Details!["messageKey"]);
        Assert.Equal(code, thrown.Details["code"]);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
