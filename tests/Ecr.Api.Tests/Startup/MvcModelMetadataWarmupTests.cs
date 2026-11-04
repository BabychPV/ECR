// tests/Ecr.Api.Tests/Startup/MvcModelMetadataWarmupTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Ecr.Api.Controllers;
using Ecr.Api.Startup;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// Гонитва ASP.NET Core на першому запиті: два одночасні перші запити до дії з record-тілом після рестарту давали
/// <c>500 InvalidOperationException: No property found that maps to constructor parameter …</c> (CI: run
/// 41555543027 api-4 — <c>ApproveSheetRequest.Reason</c>; run 41557687171 api-1 —
/// <c>MigrateDocumentVersionRequest.DryRun</c>).
/// </summary>
/// <remarks>
/// ⛔ Корінь — у фреймворку: <c>ModelMetadata.GetHashCode()</c> кешує хеш у <c>int? _hashCode</c> без
/// синхронізації, і конкурентне перше хешування читає «розірване» значення; першим параметр конструктора
/// record-типу хешує <c>ModelMetadata.CalculateRecordTypeConstructorDetails</c> (словник
/// <c>_parameterMapping</c>), тож ключ із хибним хешем лягає не в той кошик — і дія лишається зламаною до
/// рестарту. Вікно — наносекунди (локально на 20 000 ітерацій конкурентної валідації — 0 падінь; на
/// завантаженому CI-раннері витіснення потоку посеред запису розширює його), тому доказ тут не «стрес до
/// червоного», а детермінований: після старту хеш і відображення «параметр → властивість» уже обчислені для
/// КОЖНОГО вузла графа параметрів КОЖНОЇ дії, тобто конкурентним запитам нічого лінивого не лишилось.
///
/// ⛔ Мутація: прибрати виклик <see cref="MvcModelMetadataWarmup.Warm"/> зі <c>StartupSequence</c> (або
/// повернути з <c>Warm</c> одразу) — обидва тести червоні.
///
/// ⚠ Тести читають приватні поля <c>ModelMetadata</c> (<c>_hashCode</c>,
/// <c>_recordTypeConstructorDetailsCalculated</c>) — саме ті, на яких гонитва. Якщо нова версія ASP.NET Core їх
/// перейменує, тест падає з прямою вказівкою: перевір, чи гонитва ще є, і онови перевірку.
/// </remarks>
[Collection("SqlServer")]
public sealed class MvcModelMetadataWarmupTests(SqlServerFixture sql)
{
    private const string Password = "Mvc-Warmup-Race-01!";

    private static readonly FieldInfo HashCodeField = Field("_hashCode");
    private static readonly FieldInfo MappingCalculatedField = Field("_recordTypeConstructorDetailsCalculated");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-5.17")]
    public void Прогрів_кешує_хеш_і_відображення_record_для_графа_параметрів_усіх_дій()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(DocumentsController).Assembly);
        using var provider = services.BuildServiceProvider();

        var result = MvcModelMetadataWarmup.Warm(provider, NullLogger.Instance);

        Assert.Equal(0, result.Failures);
        Assert.True(result.Records >= 100, $"record-вузлів прогріто {result.Records}, очікувалось ≥ 100 (149 тіл запитів)");
        AssertWarm(provider);
    }

    /// <summary>
    /// Справжній старт (<c>StartupSequence</c>) прогріває метадані ДО першого запиту, і одночасні перші запити до
    /// record-дій свіжого застосунку не дають 5xx.
    /// </summary>
    /// <remarks>⚠ Конкурентний тест — правило N/20.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Свіжий_застосунок_прогрітий_до_першого_запиту_і_одночасні_перші_запити_без_500()
    {
        var userName = await NewUserAsync().ConfigureAwait(true);

        for (var iteration = 0; iteration < 3; iteration++)
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.CreateClient();

            // До ЖОДНОГО запиту: сервер піднято (CreateClient), StartupSequence відпрацював.
            AssertWarm(app.Services);

            using (var login = await client.PostAsJsonAsync(
                       new Uri("/api/v1/login/local", UriKind.Relative),
                       new { userName, password = Password }).ConfigureAwait(true))
            {
                Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
            }

            const long missing = 9_000_000_000_000;
            var calls = Enumerable.Range(0, 4).SelectMany(_ => new Func<Task<HttpResponseMessage>>[]
            {
                () => client.PostAsJsonAsync(Url(missing, "approve"), new { sheetDefId = 1, periodKey = 202601, approved = true, reason = (string?)null }),
                () => client.PostAsJsonAsync(Url(missing, "migrate-version"), new { targetVersionId = 1, dryRun = true }),
                () => client.PostAsJsonAsync(Url(missing, "submit"), new { sheetDefId = 1, periodKey = 202601 }),
                () => client.PostAsJsonAsync(Url(missing, "reopen"), new { sheetDefId = 1, periodKey = 202601, reason = "race" }),
            }).ToList();

            var replies = await Task.WhenAll(calls.Select(c => Task.Run(c))).ConfigureAwait(true);
            var codes = replies.Select(r => (int)r.StatusCode).ToList();
            foreach (var reply in replies)
            {
                reply.Dispose();
            }

            Assert.True(
                codes.TrueForAll(c => c is >= 400 and < 500),
                $"ітерація {iteration}: коди {string.Join(", ", codes)}\n{app.ErrorsText}");
            Assert.DoesNotContain(app.ServerErrors, e => e.Contains("No property found that maps", StringComparison.Ordinal));
        }
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static Uri Url(long id, string action) => new($"/api/v1/documents/{id}/{action}", UriKind.Relative);

    /// <summary>Кожен вузол графа параметрів кожної дії: хеш закешовано; кожен record — відображення пораховано.</summary>
    private static void AssertWarm(IServiceProvider services)
    {
        var provider = services.GetRequiredService<IModelMetadataProvider>();
        var typed = Assert.IsAssignableFrom<ModelMetadataProvider>(provider);
        var actions = services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;
        var seen = new HashSet<ModelMetadata>(ReferenceEqualityComparer.Instance);
        var cold = new List<string>();
        var records = 0;

        foreach (var parameter in actions.SelectMany(a => a.Parameters).OfType<ControllerParameterDescriptor>())
        {
            Visit(typed.GetMetadataForParameter(parameter.ParameterInfo, parameter.ParameterType), 0);
        }

        Assert.True(records >= 100, $"record-вузлів знайдено {records}");
        Assert.True(cold.Count == 0, $"непрогріті вузли ({cold.Count}): {string.Join("; ", cold.Take(20))}");

        void Visit(ModelMetadata node, int depth)
        {
            if (depth > 16 || !seen.Add(node))
            {
                return;
            }

            // ⚠ Лише поля через відбиття: виклик GetHashCode() тут сам би прогрів вузол і зробив тест зеленим.
            if (HashCodeField.GetValue(node) is null)
            {
                cold.Add($"хеш {node.MetadataKind} {node.ContainerType?.Name}.{node.Name ?? node.ModelType.Name}");
            }

            if (!node.ValidateChildren)
            {
                return;
            }

            if (node.IsEnumerableType && node.ElementMetadata is { } element)
            {
                Visit(element, depth + 1);
            }

            if (!node.IsComplexType)
            {
                return;
            }

            if (node.BoundConstructor is { } constructor)
            {
                records++;
                if (!node.ModelType.IsAbstract && MappingCalculatedField.GetValue(node) is not true)
                {
                    cold.Add($"відображення record {node.ModelType.Name} ({node.MetadataKind} {node.Name})");
                }

                foreach (var p in constructor.BoundConstructorParameters ?? [])
                {
                    Visit(p, depth + 1);
                }
            }

            foreach (var property in node.Properties)
            {
                Visit(property, depth + 1);
            }
        }
    }

    private static FieldInfo Field(string name)
        => typeof(ModelMetadata).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException(
               $"ModelMetadata.{name} зникло в цій версії ASP.NET Core: перевір, чи гонитва першого хешування "
               + "(MvcModelMetadataWarmup) ще є, і онови тест.");

    private async Task<string> NewUserAsync()
    {
        var name = $"mvcw_{Guid.NewGuid():N}"[..20];

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }
}
