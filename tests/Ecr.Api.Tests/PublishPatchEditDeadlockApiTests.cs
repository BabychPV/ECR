// tests/Ecr.Api.Tests/PublishPatchEditDeadlockApiTests.cs
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
/// C5: публікація, презентаційний патч і структурна правка ТІЄЇ САМОЇ версії
/// одночасно — без взаємного блокування.
/// </summary>
/// <remarks>
/// ⛔ Порядок блокувань — рядок <c>cfg.TemplateVersion</c> ЗАВЖДИ першим. Без
/// блоку версії на початку патч брав X на рядку колонки
/// (<c>ApplyPresentationAsync</c>) і лише потім, інкрементом ревізії, — на
/// рядку версії. Правка чернетки після C5 робить навпаки: U на версії, потім
/// X на тому самому рядку колонки. Це цикл, тобто 1205, — тому патч теж бере
/// блок версії першим.
///
/// ⚠ Що тест доводить: під фіксом усі три серіалізуються на блоці версії й
/// завершуються штатно. Без блоку в патчі впав би лічильник контенції
/// (<c>≥ 3</c> входи в блок до відпуску), а сам 1205 залежав би від того,
/// хто з двох першим отримає рядок версії після публікації.
///
/// ⚠ Інтерлівінг детермінований шлюзом: публікація тримає блок версії й
/// стоїть на читанні знімка, доки і патч, і правка не дійдуть до своїх
/// транзакцій (<c>HasDocumentsAsync</c> — їхній останній крок перед
/// транзакцією), і ще 2 с — за цей час обидва впираються в блок версії.
/// Тест перевіряє, що вони справді там стояли, а не проскочили після.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishPatchEditDeadlockApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_патч_і_правка_однієї_версії_серіалізуються_без_дедлоку()
    {
        var draft = await ArrangeAsync();
        var gate = new PublishHoldGate();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped(sp => GatedStore.Create(
                new TemplateVersionStore(sp.GetRequiredService<EcrDbContext>()), gate))));
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish");

        gate.Arm();

        var publish = client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/publish", UriKind.Relative),
            new { reason = "C5 deadlock" });

        var arrived = await Task.WhenAny(gate.PublishHolding, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(arrived == gate.PublishHolding, $"Публікація не дійшла до знімка.\n{baseApp.ErrorsText}");

        // Той самий рядок колонки — і в патчі, і в правці.
        using var patchRequest = new HttpRequestMessage(
            HttpMethod.Patch, new Uri($"/api/v1/template-versions/{draft.VersionId}/presentation", UriKind.Relative))
        {
            Content = JsonContent.Create(new[]
            {
                new { entityType = "ColumnDef", entityId = draft.ColumnId, field = "DisplayFormat", value = "0.00" },
            }),
        };
        var patch = client.SendAsync(patchRequest);

        var edit = client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{draft.VersionId}/tables/{draft.TableId}/columns/CDEC", UriKind.Relative),
            new
            {
                headerL10n = new Dictionary<string, string> { ["en"] = "CDEC" },
                ordinal = (int?)null,
                dataType = "Decimal",
                isRequired = true, isReadOnly = false, isHidden = false,
                precision = (byte?)null, scale = (byte?)null,
                defaultValue = (string?)null, displayFormat = (string?)null, styleId = (int?)null,
                lookupRegistryDefId = (int?)null, lookupFilter = (string?)null,
                unitId = (int?)null,
            });

        var all = Task.WhenAll(publish, patch, edit);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(90)));
        Assert.True(finished == all, $"Хтось завис.\n{baseApp.ErrorsText}");

        using var published = await publish;
        using var patched = await patch;
        using var edited = await edit;
        var publishBody = await published.Content.ReadAsStringAsync();
        var patchBody = await patched.Content.ReadAsStringAsync();
        var editBody = await edited.Content.ReadAsStringAsync();
        var summary =
            $"publish {(int)published.StatusCode} {publishBody}\npatch {(int)patched.StatusCode} {patchBody}\n" +
            $"edit {(int)edited.StatusCode} {editBody}\nlocks before release: {gate.LocksEnteredBeforeRelease}\n{baseApp.ErrorsText}";

        // ⛔ Жертви дедлоку немає: жодного 500, у журналі сервера — жодного 1205.
        Assert.DoesNotContain("1205", baseApp.ErrorsText, StringComparison.Ordinal);
        Assert.DoesNotContain("deadlock", baseApp.ErrorsText, StringComparison.OrdinalIgnoreCase);

        // Контенція справжня: патч і правка стояли на блоці версії, поки публікація його тримала.
        Assert.True(gate.LocksEnteredBeforeRelease >= 3, summary);

        // Публікація — перша; патч законний і на опублікованій версії; правка бачить Published.
        Assert.True(published.IsSuccessStatusCode, summary);
        Assert.True(patched.StatusCode == HttpStatusCode.OK, summary);
        Assert.True(edited.StatusCode == HttpStatusCode.Conflict, summary);
        using (var problem = JsonDocument.Parse(editBody))
        {
            Assert.Equal("err.ECR-TMPL-0409.structurallyFrozen", problem.RootElement.GetProperty("messageKey").GetString());
        }

        await using var db = Context();
        var version = await db.TemplateVersions.AsNoTracking().SingleAsync(v => v.Id == draft.VersionId);
        var column = await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == draft.ColumnId);

        Assert.Equal(TemplateVersionStatus.Published, version.Status);
        Assert.Equal(1, version.PresentationRevision);
        Assert.Equal("0.00", column.DisplayFormat);
        Assert.False(column.IsRequired, "Правка чернетки пройшла в опубліковану версію.");
    }

    private async Task<Draft> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"C5D{tag}"), Name("C5D"), 1, Now);
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

        var column = new ColumnDef(table.Id, EcrCode.Create("CDEC"), Name("CDEC"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));
        await db.SaveChangesAsync();

        return new Draft(version.Id, table.Id, column.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Draft(int VersionId, int TableId, int ColumnId);

    /// <summary>
    /// Тримає публікацію на читанні знімка (перше <c>GetWithStructureAsync</c>
    /// після <see cref="Arm"/>), доки не прийдуть два <c>HasDocumentsAsync</c>
    /// (патч і правка), і ще 2 с; рахує входи в блок версії до відпуску.
    /// </summary>
    public sealed class PublishHoldGate
    {
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _othersReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _structureReads;
        private int _documentChecks;
        private int _locksEntered;
        private int _released;

        /// <summary>Публікація тримає блок версії й стоїть на шлюзі.</summary>
        public Task PublishHolding => _holding.Task;

        /// <summary>Скільки входів у блок версії було до відпуску публікації.</summary>
        public int LocksEnteredBeforeRelease { get; private set; }

        /// <summary>Вмикає шлюз.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>Вхід у <c>LockVersionForUpdateAsync</c>.</summary>
        public void LockEntered() => Interlocked.Increment(ref _locksEntered);

        /// <summary>Після <c>HasDocumentsAsync</c>.</summary>
        public void DocumentsChecked()
        {
            if (Volatile.Read(ref _armed) == 1 && Interlocked.Increment(ref _documentChecks) == 2)
            {
                _othersReady.TrySetResult();
            }
        }

        /// <summary>Після <c>GetWithStructureAsync</c>: перше — тримається.</summary>
        public async Task AfterStructureReadAsync()
        {
            if (Volatile.Read(ref _armed) == 0 || Interlocked.Increment(ref _structureReads) != 1)
            {
                return;
            }

            _holding.TrySetResult();
            await Task.WhenAny(_othersReady.Task, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                LocksEnteredBeforeRelease = Volatile.Read(ref _locksEntered);
            }
        }
    }

    /// <summary>Справжнє сховище версій зі шлюзом на трьох методах.</summary>
    public class GatedStore : DispatchProxy
    {
        private ITemplateVersionStore _inner = null!;
        private PublishHoldGate _gate = null!;

        /// <summary>Будує проксі над <paramref name="inner"/>.</summary>
        public static ITemplateVersionStore Create(ITemplateVersionStore inner, PublishHoldGate gate)
        {
            var proxy = Create<ITemplateVersionStore, GatedStore>();
            var self = (GatedStore)(object)proxy;
            self._inner = inner;
            self._gate = gate;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            switch (targetMethod.Name)
            {
                case nameof(ITemplateVersionStore.GetWithStructureAsync):
                    return StructureAsync((int)args![0]!, (CancellationToken)args[1]!);
                case nameof(ITemplateVersionStore.HasDocumentsAsync):
                    return DocumentsAsync((int)args![0]!, (CancellationToken)args[1]!);
                case nameof(ITemplateVersionStore.LockVersionForUpdateAsync):
                    _gate.LockEntered();
                    break;
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

        private async Task<TemplateVersion> StructureAsync(int templateVersionId, CancellationToken ct)
        {
            var version = await _inner.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
            await _gate.AfterStructureReadAsync().ConfigureAwait(false);
            return version;
        }

        private async Task<bool> DocumentsAsync(int templateVersionId, CancellationToken ct)
        {
            var result = await _inner.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
            _gate.DocumentsChecked();
            return result;
        }
    }
}
