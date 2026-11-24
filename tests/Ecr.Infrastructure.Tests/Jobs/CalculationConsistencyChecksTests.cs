// tests/Ecr.Infrastructure.Tests/Jobs/CalculationConsistencyChecksTests.cs
using System.Globalization;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Правила узгодженості результатів розрахунку й методологій (аудит 24.11): кожне — стан-порушення (знахідка) і чистий
/// стан (знахідки немає) на власних рядках тесту.
/// </summary>
/// <remarks>
/// ⚠ Спільна тестова база: інші тести лишають методології, прогони й прив'язки, тож кожна перевірка — лише за
/// ідентифікаторами власних сутностей, а не загальною кількістю.
/// <para>
/// Мутації (звіт lane integ-consistency-checks): R1 — <c>effective?.Id != version.Id</c> → <c>false</c>; R3 — фільтр
/// <c>Engine: Formulas</c> → <c>Script</c>; R4 — пропуск рядка з проблемою прибрано / умову «усі входи» → «жоден»;
/// R5 — умову <c>live.Count == 0</c> → <c>true</c>; R2 — <c>!Any(run)</c> → <c>Any(run)</c>; R8 — умову
/// <c>!boundOutputs.Contains</c> → <c>Contains</c>; R7 — <c>c.IsDeleted || …</c> → <c>false</c>. Кожна червонить
/// відповідний тест.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-7.7")]
public sealed class CalculationConsistencyChecksTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>R1: результат версії Formulas, коли на період уже чинна версія Script, — знахідка; результат Script — ні.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R1_результат_не_чинної_версії_знахідка_чинної_ні(bool fromEffective)
    {
        var world = await World.ArrangeAsync(sql);
        var run = await world.CurrentRunAsync();
        var resultId = await world.InsertResultAsync(run, fromEffective ? world.ScriptVersionId : world.FormulasVersionId, world.RowKeys[0]);

        var (findings, complete) = await Checks().ResultVersionNotEffectiveAsync(1000, CancellationToken.None);

        Assert.True(complete);
        var own = findings.Where(f => f.EntityType == CalculationConsistencyChecks.ResultEntity && f.EntityId == resultId).ToList();
        if (fromEffective)
        {
            Assert.Empty(own);
            return;
        }

        var finding = Assert.Single(own);
        Assert.Equal(2, finding.Severity);
        Assert.Equal(world.Doc.PeriodKey.Value, finding.PeriodKey);
        Assert.Contains("consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE\"", finding.Message, StringComparison.Ordinal);
        Assert.Contains(world.MethodologyCode, finding.Message, StringComparison.Ordinal);
        Assert.Contains($"\"documentId\":\"{world.Doc.DocumentId}\"", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>R1 на місяці із затвердженим аркушем — та сама вага 2, але текст про затверджене (повернути в роботу).</summary>
    [Fact]
    public async Task R1_затверджений_місяць_вага_2_і_текст_про_затвердження()
    {
        var world = await World.ArrangeAsync(sql);
        var run = await world.CurrentRunAsync();
        var resultId = await world.InsertResultAsync(run, world.FormulasVersionId, world.RowKeys[0]);
        await world.ApproveSheetAsync();

        var (findings, _) = await Checks().ResultVersionNotEffectiveAsync(1000, CancellationToken.None);

        var finding = Assert.Single(findings, f => f.EntityId == resultId && f.EntityType == CalculationConsistencyChecks.ResultEntity);
        Assert.Equal(2, finding.Severity);
        Assert.Contains("consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE_APPROVED", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>R3: єдина опублікована версія — Formulas — знахідка; з пізнішою Script — ні.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R3_чинна_версія_Formulas_знахідка_після_Script_ні(bool withScript)
    {
        var world = await World.ArrangeAsync(sql, withScript: withScript);

        var (findings, _) = await Checks().EffectiveVersionFormulasAsync(1000, CancellationToken.None);

        var own = findings.Where(f => f.EntityType == CalculationConsistencyChecks.VersionEntity
                                      && (f.EntityId == world.FormulasVersionId || f.EntityId == world.ScriptVersionId)).ToList();
        if (withScript)
        {
            Assert.Empty(own);
            return;
        }

        var finding = Assert.Single(own);
        Assert.Equal(world.FormulasVersionId, finding.EntityId);
        Assert.Equal(2, finding.Severity);
    }

    /// <summary>R5: актуальний прогін без результатів і проблем при прив'язці з рядками — помилка; з результатом — ні.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R5_прогін_без_результатів_за_наявних_прив_язок_помилка(bool withResult)
    {
        var world = await World.ArrangeAsync(sql);
        await world.BindAsync("OUT_A");
        var run = await world.CurrentRunAsync();
        if (withResult)
        {
            await world.InsertResultAsync(run, world.ScriptVersionId, world.RowKeys[0]);
        }

        var (findings, _) = await Checks().CurrentRunNoResultsAsync(1000, CancellationToken.None);

        var own = findings.Where(f => f.EntityType == CalculationConsistencyChecks.RunEntity && f.EntityId == run).ToList();
        if (withResult)
        {
            Assert.Empty(own);
            return;
        }

        Assert.Equal(3, Assert.Single(own).Severity);
    }

    /// <summary>R5: прогін без результатів і без прив'язок — законний нуль (live: прогін 10143), знахідки немає.</summary>
    [Fact]
    public async Task R5_прогін_без_прив_язок_не_знахідка()
    {
        var world = await World.ArrangeAsync(sql);
        var run = await world.CurrentRunAsync();

        var (findings, _) = await Checks().CurrentRunNoResultsAsync(1000, CancellationToken.None);

        Assert.DoesNotContain(findings, f => f.EntityId == run && f.EntityType == CalculationConsistencyChecks.RunEntity);
    }

    /// <summary>R2: проблема розрахунку зниклого прогону — знахідка на прогін; проблема живого прогону — ні.</summary>
    [Fact]
    public async Task R2_проблема_без_прогону_знахідка_з_прогоном_ні()
    {
        var world = await World.ArrangeAsync(sql);
        var run = await world.CurrentRunAsync();
        await world.InsertIssueAsync(run, world.RowKeys[0]);
        var missingRun = -900_000_000L - Random.Shared.Next(1, 1_000_000);
        await world.InsertIssueAsync(missingRun, world.RowKeys[1], ignoreForeignKeys: true);

        try
        {
            var (findings, _) = await Checks().IssueWithoutRunAsync(1000, CancellationToken.None);

            var finding = Assert.Single(findings, f => f.EntityId == missingRun);
            Assert.Equal(3, finding.Severity);
            Assert.Equal(CalculationConsistencyChecks.RunEntity, finding.EntityType);
            Assert.DoesNotContain(findings, f => f.EntityId == run);
        }
        finally
        {
            await world.ExecuteAsync($"DELETE calc.CalculationIssue WHERE CalculationRunId = {missingRun};");
        }
    }

    /// <summary>R8: вихід чинної версії без прив'язки — інформація; коли прив'язано обидва виходи — нічого.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R8_вихід_без_прив_язки_інформація(bool bindBoth)
    {
        var world = await World.ArrangeAsync(sql);
        await world.BindAsync("OUT_A");
        if (bindBoth)
        {
            await world.BindAsync("OUT_B", columnIndex: 2);
        }

        var (findings, _) = await Checks().OutputWithoutBindingAsync(1000, CancellationToken.None);

        var own = findings.Where(f => f.EntityType == CalculationConsistencyChecks.OutputEntity
                                      && world.OutputIds.ContainsValue((int)f.EntityId)).ToList();
        if (bindBoth)
        {
            Assert.Empty(own);
            return;
        }

        var finding = Assert.Single(own);
        Assert.Equal(world.OutputIds["OUT_B"], finding.EntityId);
        Assert.Equal(1, finding.Severity);
    }

    /// <summary>R7: чинна прив'язка живої версії шаблону у видалену колонку — інформація; у живу колонку — ні.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R7_прив_язка_у_видалену_колонку_живої_версії(bool deleted)
    {
        var world = await World.ArrangeAsync(sql);
        var bindingId = await world.BindAsync("OUT_A");
        if (deleted)
        {
            await world.ExecuteAsync($"UPDATE cfg.ColumnDef SET IsDeleted = 1, DeletedAt = SYSUTCDATETIME() WHERE Id = {world.Doc.ColumnDefIds[0]};");
        }

        var (findings, _) = await Checks().BindingDeadColumnAsync(1000, CancellationToken.None);

        var own = findings.Where(f => f.EntityType == CalculationConsistencyChecks.BindingEntity && f.EntityId == bindingId).ToList();
        if (!deleted)
        {
            Assert.Empty(own);
            return;
        }

        Assert.Equal(1, Assert.Single(own).Severity);
    }

    /// <summary>
    /// R4: рядок з усіма обов'язковими входами без результату й без проблеми — знахідка; той самий рядок із результатом,
    /// із проблемою, з незаповненим входом чи відсіяний фільтром активності — ні.
    /// </summary>
    [Theory]
    [InlineData("silent", true)]
    [InlineData("result", false)]
    [InlineData("issue", false)]
    [InlineData("partial", false)]
    [InlineData("inactive", false)]
    public async Task R4_рядок_з_усіма_входами_без_результату(string state, bool found)
    {
        var world = await World.ArrangeAsync(sql);
        await world.BindAsync("OUT_A");
        await world.RequireInputsAsync();
        var run = await world.CurrentRunAsync();

        // Рядок 0 — предмет перевірки; рядок 1 — порахований (не має давати знахідки за будь-якого стану).
        await world.FillInputsAsync(0, both: state != "partial");
        await world.FillInputsAsync(1, both: true);
        await world.InsertResultAsync(run, world.ScriptVersionId, world.RowKeys[1]);
        switch (state)
        {
            case "result":
                await world.InsertResultAsync(run, world.ScriptVersionId, world.RowKeys[0]);
                break;
            case "issue":
                await world.InsertIssueAsync(run, world.RowKeys[0]);
                break;
            case "inactive":
                await world.MakeRowInactiveAsync(0);
                break;
        }

        var (findings, complete) = await Checks().RowInputsCompleteNoResultAsync(1000, CancellationToken.None);

        Assert.True(complete);
        Assert.DoesNotContain(findings, f => f.EntityType == CalculationConsistencyChecks.RowEntity && f.EntityId == world.Doc.RowIds[1]);
        var own = findings.Where(f => f.EntityType == CalculationConsistencyChecks.RowEntity && f.EntityId == world.Doc.RowIds[0]).ToList();
        if (!found)
        {
            Assert.Empty(own);
            return;
        }

        var finding = Assert.Single(own);
        Assert.Equal(2, finding.Severity);
        Assert.Contains(world.RowKeys[0], finding.Message, StringComparison.Ordinal);
        Assert.Contains(world.MethodologyCode, finding.Message, StringComparison.Ordinal);
    }

    /// <summary>R4: понад 50 рядків документа × періоду — 50 знахідок рядків і одна знахідка-підсумок «ще N» на прогін.</summary>
    [Fact]
    public async Task R4_понад_стелю_рядків_підсумок_на_прогін()
    {
        var extra = 3;
        var world = await World.ArrangeAsync(sql, rowCount: CalculationConsistencyChecks.MaxRowFindingsPerDocumentPeriod + extra);
        await world.BindAsync("OUT_A");
        await world.RequireInputsAsync();
        var run = await world.CurrentRunAsync();
        for (var i = 0; i < world.Doc.RowIds.Count; i++)
        {
            await world.FillInputsAsync(i, both: true);
        }

        var (findings, _) = await Checks().RowInputsCompleteNoResultAsync(1000, CancellationToken.None);

        var rows = world.Doc.RowIds.ToHashSet();
        Assert.Equal(
            CalculationConsistencyChecks.MaxRowFindingsPerDocumentPeriod,
            findings.Count(f => f.EntityType == CalculationConsistencyChecks.RowEntity && rows.Contains(f.EntityId)));
        var summary = Assert.Single(findings, f => f.EntityType == CalculationConsistencyChecks.RunEntity && f.EntityId == run);
        Assert.Contains("ROW_INPUTS_COMPLETE_NO_RESULT_MORE", summary.Message, StringComparison.Ordinal);
        Assert.Contains($"\"more\":\"{extra}\"", summary.Message, StringComparison.Ordinal);
    }

    /// <summary>Наскрізно через задачу: знахідка R1 записана в журнал, а деталі прогону називають усі правила з кількістю.</summary>
    [Fact]
    public async Task Задача_пише_знахідку_R1_і_перелік_правил_у_деталях()
    {
        var world = await World.ArrangeAsync(sql);
        var run = await world.CurrentRunAsync();
        var resultId = await world.InsertResultAsync(run, world.FormulasVersionId, world.RowKeys[0]);

        await using (var db = Context())
        {
            var job = new ConsistencyCheckJob(
                db, NSubstitute.Substitute.For<Application.Ports.IOrphanScanner>(), new TestClock(Now),
                NSubstitute.Substitute.For<Application.Ports.IConsistencyMetrics>());
            await job.ExecuteAsync(null, NSubstitute.Substitute.For<Application.Ports.IJobProgress>(), CancellationToken.None);
        }

        var severity = await world.ScalarAsync(
            $"SELECT Severity FROM aud.ConsistencyIssue WHERE RuleCode = N'CALC_RESULT_VERSION_NOT_EFFECTIVE' AND EntityType = N'calc.CalculationResult' AND EntityId = {resultId};");
        Assert.Equal((byte)2, severity);

        var details = (string?)await world.ScalarAsync(
            "SELECT TOP (1) DetailsJson FROM itg.MaintenanceRun WHERE JobCode = N'consistency-check' ORDER BY Id DESC;");
        Assert.NotNull(details);
        foreach (var code in ConsistencyCheckJob.KnownRules)
        {
            Assert.Contains($"\"code\":\"{code}\"", details, StringComparison.Ordinal);
        }
    }

    private CalculationConsistencyChecks Checks() => new(Context(), Now);

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Документ, методологія з версіями Formulas (2024) і Script (2026, за бажанням), виходи OUT_A/OUT_B.</summary>
    private sealed class World
    {
        private readonly string _cs;
        private readonly TestDocumentBuilder _builder;

        private World(string cs, TestDocumentBuilder builder, TestDocument doc)
        {
            _cs = cs;
            _builder = builder;
            Doc = doc;
        }

        public TestDocument Doc { get; }

        public List<string> RowKeys { get; private set; } = [];

        public int MethodologyId { get; private set; }

        public string MethodologyCode { get; private set; } = null!;

        public int FormulasVersionId { get; private set; }

        public int ScriptVersionId { get; private set; }

        public Dictionary<string, int> OutputIds { get; } = [];

        private int UnitId { get; set; }

        public static async Task<World> ArrangeAsync(SqlServerFixture sql, bool withScript = true, int rowCount = 4)
        {
            var builder = new TestDocumentBuilder(sql.ConnectionString);
            var doc = await builder.BuildAsync(rowCount: rowCount, ct: CancellationToken.None);
            var world = new World(sql.ConnectionString, builder, doc);

            await using var db = builder.CreateContext();
            world.RowKeys = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.TableInstanceId == doc.TableInstanceId)
                .OrderBy(r => r.Ordinal)
                .Select(r => r.RowKeyValue)
                .ToListAsync(CancellationToken.None);
            world.UnitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);

            var code = $"CCK_{Guid.NewGuid():N}"[..16].ToUpperInvariant();
            var methodology = new Methodology(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync(CancellationToken.None);
            world.MethodologyId = methodology.Id;
            world.MethodologyCode = code;

            world.FormulasVersionId = await world.PublishedVersionAsync(db, "V1", "2024-01-01", MethodologyEngine.Formulas);
            if (withScript)
            {
                world.ScriptVersionId = await world.PublishedVersionAsync(db, "V2", "2026-01-01", MethodologyEngine.Script);
            }

            var effective = withScript ? world.ScriptVersionId : world.FormulasVersionId;
            foreach (var output in new[] { "OUT_A", "OUT_B" })
            {
                world.OutputIds[output] = (int)(await world.ScalarAsync(
                    $"INSERT calc.MethodologyOutput (MethodologyVersionId, Code, UnitId, Ordinal, IsPerSubstance) OUTPUT inserted.Id VALUES ({effective}, N'{output}', {world.UnitId}, 0, 0);"))!;
            }

            return world;
        }

        private async Task<int> PublishedVersionAsync(EcrDbContext db, string label, string effectiveFrom, MethodologyEngine engine)
        {
            var version = new MethodologyVersion(MethodologyId, label, CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync(CancellationToken.None);
            await ExecuteAsync(
                $"UPDATE calc.MethodologyVersion SET Status = {(byte)TemplateVersionStatus.Published}, EffectiveFrom = '{effectiveFrom}', PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1, ChangeReason = N'test', Engine = {(byte)engine} WHERE Id = {version.Id};");
            return version.Id;
        }

        public async Task<long> BindAsync(string output, int columnIndex = 0)
        {
            await using var db = _builder.CreateContext();
            var binding = new CalculationBinding(Doc.TableDefId, Doc.ColumnDefIds[columnIndex], MethodologyId, output, "{}");
            db.CalculationBindings.Add(binding);
            await db.SaveChangesAsync(CancellationToken.None);
            return binding.Id;
        }

        /// <summary>Обов'язкові входи чинної (Script) версії — колонки C2 і C3 таблиці документа.</summary>
        public async Task RequireInputsAsync()
        {
            foreach (var column in Doc.ColumnDefIds.Skip(1).Take(2))
            {
                await ExecuteAsync(
                    $"INSERT calc.MethodologyRequiredInput (MethodologyVersionId, ColumnDefId, Severity) VALUES ({ScriptVersionId}, {column}, 0);");
            }
        }

        public async Task FillInputsAsync(int row, bool both)
        {
            foreach (var column in Doc.ColumnDefIds.Skip(1).Take(both ? 2 : 1))
            {
                await ExecuteAsync(
                    $"INSERT doc.CellValue (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES ({Doc.PeriodKey.Value}, {Doc.RowIds[row]}, {Doc.TableDefId}, {column}, 1.5, 0, 0);");
            }
        }

        /// <summary>
        /// Фільтр активності методології (D-425) на довідник статусів; колонка C1 рядків стає колонкою статусу, а рядок
        /// отримує не активний запис.
        /// </summary>
        public async Task MakeRowInactiveAsync(int row)
        {
            var registry = $"CCKST_{Guid.NewGuid():N}"[..14].ToUpperInvariant();
            long running, stopped;
            int registryId;
            await using (var db = _builder.CreateContext())
            {
                var def = new RegistryDef(EcrCode.Create(registry), Text("Status"), isTemporal: false);
                db.RegistryDefs.Add(def);
                await db.SaveChangesAsync(CancellationToken.None);
                var on = new RegistryEntry(def.Id, EcrCode.Create("Running"), Text("Running"));
                var off = new RegistryEntry(def.Id, EcrCode.Create("Stopped"), Text("Stopped"));
                db.RegistryEntries.AddRange(on, off);
                await db.SaveChangesAsync(CancellationToken.None);
                (registryId, running, stopped) = (def.Id, on.Id, off.Id);
            }

            await ExecuteAsync($"""
                UPDATE cfg.ColumnDef SET LookupRegistryDefId = {registryId} WHERE Id = {Doc.ColumnDefIds[0]};
                UPDATE calc.Methodology SET RowActivityFilter = N'{registry}:Running' WHERE Id = {MethodologyId};
                INSERT doc.CellValue (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueRegistryEntryId, IsCalculated, IsEmpty)
                VALUES ({Doc.PeriodKey.Value}, {Doc.RowIds[row]}, {Doc.TableDefId}, {Doc.ColumnDefIds[0]}, {stopped}, 0, 0);
                INSERT doc.CellValue (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueRegistryEntryId, IsCalculated, IsEmpty)
                VALUES ({Doc.PeriodKey.Value}, {Doc.RowIds[1]}, {Doc.TableDefId}, {Doc.ColumnDefIds[0]}, {running}, 0, 0);
                """);
        }

        public async Task<long> CurrentRunAsync()
        {
            await using var db = _builder.CreateContext();
            var run = new CalculationRun(Doc.ProjectId, Doc.PeriodKey.Value, triggeredByUserId: null, Now, documentId: Doc.DocumentId);
            run.Complete("Succeeded", Now, "{}", errorMessage: null);
            run.MakeCurrent();
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
            return run.Id;
        }

        public async Task<long> InsertResultAsync(long run, int versionId, string rowKey)
            => (long)(await ScalarAsync($"""
                DECLARE @id bigint = NEXT VALUE FOR calc.CalculationResultSeq;
                INSERT calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
                VALUES (@id, {run}, {versionId}, {Doc.PeriodKey.Value}, {Doc.DocumentId}, N'{rowKey}', N'OUT_A', 1, {UnitId}, {Doc.TableInstanceId});
                SELECT @id;
                """))!;

        public async Task InsertIssueAsync(long run, string rowKey, bool ignoreForeignKeys = false)
        {
            var insert = $"""
                INSERT calc.CalculationIssue (DocumentId, PeriodKey, TableDefId, RowKey, RowNumber, Kind, MethodologyId, CalculationRunId, DetectedAt)
                VALUES ({Doc.DocumentId}, {Doc.PeriodKey.Value}, {Doc.TableDefId}, N'{rowKey}', 1, N'MissingInputs', {MethodologyId}, {run}, SYSUTCDATETIME());
                """;

            // ⚠ Зниклий прогін неможливий звичайним записом після FK_CIssue_Run (lane calc-issue-orphans) — тест
            // відтворює дані, що потрапили в базу до ключа, тим самим прийомом, що й BROKEN_FK.
            await ExecuteAsync(ignoreForeignKeys
                ? "ALTER TABLE calc.CalculationIssue NOCHECK CONSTRAINT ALL;\n" + insert
                  + "\nALTER TABLE calc.CalculationIssue WITH NOCHECK CHECK CONSTRAINT ALL;"
                : insert);
        }

        public Task ApproveSheetAsync()
            => ExecuteAsync(
                $"INSERT wf.ApprovalState (DocumentId, SheetDefId, PeriodKey, Status, ApprovedAt, ApprovedByUserId) VALUES ({Doc.DocumentId}, {Doc.SheetDefId}, {Doc.PeriodKey.Value}, {(byte)DocumentStatus.Approved}, SYSUTCDATETIME(), 1);");

        public async Task ExecuteAsync(string text)
        {
            await using var connection = new SqlConnection(_cs);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = text;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async Task<object?> ScalarAsync(string text)
        {
            await using var connection = new SqlConnection(_cs);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = text;
            var value = await command.ExecuteScalarAsync(CancellationToken.None);
            return value is DBNull ? null : value;
        }

        public override string ToString() => MethodologyId.ToString(CultureInfo.InvariantCulture);

        private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
    }
}
