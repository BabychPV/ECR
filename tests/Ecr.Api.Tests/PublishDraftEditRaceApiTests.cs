// tests/Ecr.Api.Tests/PublishDraftEditRaceApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// C5: публікація шаблону й паралельна структурна правка тієї самої чернетки.
/// </summary>
/// <remarks>
/// ⛔ До C5 публікація читала знімок без жодного блоку, а перехід стану
/// комітила окремо, пізніше. Колонка, додана між цими моментами, лягала в
/// ОПУБЛІКОВАНУ версію, яку перевірки публікації не бачили: версія
/// «структурно незмінна» отримувала неперевірену структуру, а
/// <c>cfg.FormulaDependency</c> не знав нових формул.
///
/// ⚠ Гонка детермінована шлюзом над справжнім сховищем
/// (<see cref="GatedTemplateVersionStore"/>, той самий прийом, що
/// <c>RoleGrantsConcurrencyApiTests</c>): публікація читає знімок і
/// тримається, доки паралельна правка не прочитає ту саму версію, і ще 1 с
/// після того. Без блоку правка за цю секунду комітиться; під блоком вона
/// чекає коміту публікації й бачить <c>Published</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishDraftEditRaceApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_додана_під_час_публікації_не_потрапляє_в_опубліковану_версію()
    {
        var draft = await ArrangeAsync();
        var gate = new FirstReadGate();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped(sp => GatedTemplateVersionStore.Create(
                new TemplateVersionStore(sp.GetRequiredService<EcrDbContext>()), gate))));
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        gate.Arm();

        var publish = client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/publish", UriKind.Relative),
            new { reason = "C5 race" });

        // Публікація вже прочитала знімок і тримається — тепер правка.
        var arrived = await Task.WhenAny(gate.FirstArrived, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(arrived == gate.FirstArrived, $"Публікація не дійшла до читання знімка.\n{baseApp.ErrorsText}");

        var addColumn = client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/columns/RACE", UriKind.Relative),
            new
            {
                headerL10n = new Dictionary<string, string> { ["en"] = "Race" },
                ordinal = (int?)null,
                dataType = "Decimal",
                isRequired = false, isReadOnly = false, isHidden = false,
                precision = (byte?)null, scale = (byte?)null,
                defaultValue = (string?)null, displayFormat = (string?)null, styleId = (int?)null,
                lookupRegistryDefId = (int?)null, lookupFilter = (string?)null,
                unitId = (int?)null,
            });

        await Task.WhenAll(publish, addColumn);

        using var published = await publish;
        using var added = await addColumn;
        var addedBody = await added.Content.ReadAsStringAsync();

        Assert.True(
            published.IsSuccessStatusCode,
            $"Публікація: {(int)published.StatusCode} {await published.Content.ReadAsStringAsync()}\n{baseApp.ErrorsText}");

        await using var db = Context();
        var status = await db.TemplateVersions
            .Where(v => v.Id == draft.VersionId).Select(v => v.Status).SingleAsync();
        var raceColumn = await db.ColumnDefs.AnyAsync(c => c.TableDefId == draft.TableId && c.Code == "RACE");

        // ⛔ Головне: опублікована версія не отримала колонки, якої її перевірки не бачили.
        Assert.Equal(TemplateVersionStatus.Published, status);
        Assert.False(
            raceColumn,
            $"Колонка RACE лягла в опубліковану версію (правка: {(int)added.StatusCode} {addedBody}).");

        // Правка відмовлена чесно — тим самим кодом, що й будь-яка правка опублікованої версії.
        Assert.True(added.StatusCode == HttpStatusCode.Conflict, $"{(int)added.StatusCode}: {addedBody}\n{baseApp.ErrorsText}");
        using var problem = JsonDocument.Parse(addedBody);
        Assert.Equal("ECR-TMPL-0409", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-TMPL-0409.structurallyFrozen", problem.RootElement.GetProperty("messageKey").GetString());
    }

    private async Task<Draft> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"C5{tag}"), Name("C5"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("CDEC"), Name("CDEC"), 1, CellDataType.Decimal));
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));
        await db.SaveChangesAsync();

        return new Draft(version.Id, table.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int VersionId, int TableId);

    /// <summary>
    /// Тримає ПЕРШЕ після <see cref="Arm"/> читання структури, доки не прийде
    /// друге (або 10 с), і ще 1 с після того; решта проходять одразу.
    /// </summary>
    public sealed class FirstReadGate
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _arrived;

        /// <summary>Завершується, щойно перше читання прочитало й зупинилося.</summary>
        public Task FirstArrived => _first.Task;

        /// <summary>Вмикає шлюз.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>Пропускає читання — одразу або після очікування.</summary>
        public async Task PassAsync()
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return;
            }

            var order = Interlocked.Increment(ref _arrived);
            if (order == 2)
            {
                _second.TrySetResult();
            }

            if (order != 1)
            {
                return;
            }

            _first.TrySetResult();
            await Task.WhenAny(_second.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Справжнє сховище версій, у якого <c>GetWithStructureAsync</c> після
    /// читання проходить шлюз.
    /// </summary>
    public class GatedTemplateVersionStore : DispatchProxy
    {
        private ITemplateVersionStore _inner = null!;
        private FirstReadGate _gate = null!;

        /// <summary>Будує проксі над <paramref name="inner"/>.</summary>
        public static ITemplateVersionStore Create(ITemplateVersionStore inner, FirstReadGate gate)
        {
            var proxy = Create<ITemplateVersionStore, GatedTemplateVersionStore>();
            var self = (GatedTemplateVersionStore)(object)proxy;
            self._inner = inner;
            self._gate = gate;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(ITemplateVersionStore.GetWithStructureAsync))
            {
                return GatedAsync((int)args![0]!, (CancellationToken)args[1]!);
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        private async Task<TemplateVersion> GatedAsync(int templateVersionId, CancellationToken ct)
        {
            var version = await _inner.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
            await _gate.PassAsync().ConfigureAwait(false);
            return version;
        }
    }
}
