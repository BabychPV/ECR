using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Програна гонитва за кодом запису довідника доїжджає клієнтові реченням
/// із каталогу — без фігурних дужок у <c>detail</c>.
/// </summary>
/// <remarks>
/// ⛔ Предмет — зв'язка «справжній <c>UQ_RegistryEntry</c> у справжній базі →
/// <c>UnitOfWork.TryMapDuplicateKey</c> → <c>ExceptionHandlingMiddleware</c>
/// → рядок <c>09-seed.sql</c>». Доти відмова несла ключ
/// <c>err.ECR-REG-0409.entryCodeTaken</c>, чий шаблон чекає <c>{id}</c>
/// запису-переможця, а в місці мапінгу відомий лише переможений: користувач
/// бачив «… (Id {id}).». Кожна ланка окремо була зелена — тест
/// <c>RegistryEntryDuplicateRaceTests</c> перевіряв ключ, а сторож
/// плейсхолдерів <c>return new …</c> не бачив.
///
/// ⚠ Гонитва відтворюється детерміновано, без паралелізму (як у
/// <c>RegistryEntryDuplicateRaceTests</c>): другий <c>DbContext</c> вставляє
/// той самий код, минаючи пре-чек обробника, — рівно шлях переможеного.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryRaceLocalizedErrorTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Програна_гонитва_за_кодом_запису_дає_речення_без_плейсхолдера()
    {
        await using var db = Context();

        var def = new RegistryDef(EcrCode.Create($"RACEL_{_tag}"), Text("Race"), isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();

        const string code = "E1";

        db.RegistryEntries.Add(new RegistryEntry(def.Id, EcrCode.Create(code), Text("Перший"), 9, DateTime.UtcNow));
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        await using var db2 = Context();
        db2.RegistryEntries.Add(new RegistryEntry(def.Id, EcrCode.Create(code), Text("Другий"), 9, DateTime.UtcNow));

        // `DetailAsync` сам перевіряє відсутність кирилиці й `{плейсхолдера}`.
        var detail = await MainPathLocalizedErrorTests.DetailAsync(
            () => new UnitOfWork(db2).SaveChangesAsync(CancellationToken.None));

        Assert.Equal($"An entry with code \"{code}\" was just created in this registry by another request.", detail);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
