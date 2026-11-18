using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Знімок моделі міграцій узгоджений з моделлю за токенами паралельності (F13a).
/// </summary>
/// <remarks>
/// ⚠ Перевірка «немає незгенерованих міграцій» (<c>PendingModelChangesWarning</c>, <c>HasDifferences</c>) токени
/// паралельності НЕ бачить: вони не змінюють схему, тож знімок без <c>.IsConcurrencyToken()</c> проходить її мовчки
/// (виміряно: прибрати рядок зі знімка — <c>Migrate</c> і всі тести зелені). Тому токени звіряються окремо.
/// </remarks>
[Collection("SqlServer")]
public sealed class ModelSnapshotConcurrencyTokenTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Токени_паралельності_знімка_міграцій_збігаються_з_моделлю()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var snapshot = ((IInfrastructure<IServiceProvider>)db).Instance
            .GetService(typeof(IMigrationsAssembly)) is IMigrationsAssembly assembly
            ? assembly.ModelSnapshot!.Model
            : throw new InvalidOperationException("Немає IMigrationsAssembly.");
        var model = db.GetService<IDesignTimeModel>().Model;

        var mismatches = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            if (snapshot.FindEntityType(entity.Name) is not { } inSnapshot)
            {
                continue;
            }

            foreach (var property in entity.GetProperties())
            {
                if (inSnapshot.FindProperty(property.Name) is { } stored && stored.IsConcurrencyToken != property.IsConcurrencyToken)
                {
                    mismatches.Add($"{entity.Name}.{property.Name}: модель={property.IsConcurrencyToken}, знімок={stored.IsConcurrencyToken}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, "Знімок розходиться з моделлю за токеном паралельності: " + string.Join("; ", mismatches));
    }
}
