// tests/Ecr.Infrastructure.Tests/Persistence/DocumentLockOrderDeadlockTests.HeaderSubmit.cs
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Workflow;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L6-06 (аудит 2026-10-03): правка шапки серіалізована з поданням аркуша.
/// </summary>
/// <remarks>
/// ⛔ Що було. Подання читало й валідувало шапку під RCSI, не тримаючи нічого, на що
/// чекала б правка шапки; правка брала стани аркушів <c>UPDLOCK, HOLDLOCK</c>, але
/// подання ще їх не чіпало. Правка комітилась посеред подання — у зріз ішла стара,
/// а жива шапка поданого аркуша ставала новою.
///
/// ⚠ T1 — подання до запису стану: ті самі перші дії, що в
/// <c>SubmitSheetHandler.HandleAsync</c> (справжній <c>SheetEditGate</c>:
/// структура спільно, шапка спільно, аркуш винятково) і читання шапки для зрізу.
/// T2 — справжній <c>PatchDocumentHeaderHandler</c>.
/// </remarks>
public sealed partial class DocumentLockOrderDeadlockTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-06")]
    public async Task Правка_шапки_під_час_подання_чекає_подання()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);
        var field = await ArrangeHeaderFieldAsync(doc);

        await using (var seed = CreateContext())
        {
            seed.ApprovalStates.Add(new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value));
            await seed.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var (get, patch) = BuildHeaderHandlers(db, doc, field);
        var baseVersion = (await get.HandleAsync(doc.DocumentId, CancellationToken.None)).Version;

        await using var monitor = await OpenAsync();

        // ── T1: подання взяло свої блокування й прочитало шапку для зрізу ──
        await using var submitDb = CreateContext();
        await submitDb.Database.OpenConnectionAsync();
        await using var submitTx = await submitDb.Database.BeginTransactionAsync();
        var submitConnection = (SqlConnection)submitDb.Database.GetDbConnection();
        var tx = (SqlTransaction)submitTx.GetDbTransaction();
        var submitSpid = await SpidAsync(submitConnection, tx);

        var gate = new SheetEditGate(submitDb);
        await gate.EnterStructureAsync(doc.DocumentId, exclusive: false, CancellationToken.None);
        await gate.EnterHeaderAsync(doc.DocumentId, exclusive: false, CancellationToken.None);
        await gate.EnterSubmitAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);
        var snapshotHeader = await HeaderValueAsync(submitConnection, tx, doc.DocumentId, field.Id);

        // ── T2: правка шапки в тому самому вікні ──
        var operation = Task.Run(() => patch.HandleAsync(
            doc.DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField(field.Code, "Kashagan")], baseVersion),
            CancellationToken.None));
        var waiter = await WaitForAsync(() => BlockedByAsync(monitor, submitSpid), () => operation.IsCompleted);

        // ⛔ Предмет тесту: правка не проходить повз подання, що триває.
        Assert.True(
            waiter is not null && !operation.IsCompleted,
            "Правка шапки завершилась посеред подання: у зріз піде шапка, якої вже немає. " +
            operation.Exception?.GetBaseException().Message);

        // T1: аркуш подано, коміт.
        await ExecAsync(submitConnection, tx,
            $"UPDATE wf.ApprovalState SET Status = 1 " +
            $"WHERE DocumentId = {doc.DocumentId} AND SheetDefId = {doc.SheetDefId} AND PeriodKey = {doc.PeriodKey.Value};");
        await submitTx.CommitAsync();

        // Правка, що дочекалась, бачить поданий аркуш і відмовляє; шапка — та, що в зрізі.
        await Assert.ThrowsAsync<AccessDeniedException>(() => operation.WaitAsync(Ceiling));
        Assert.Equal(
            snapshotHeader,
            await ScalarAsync<object>(
                $"SELECT ValueString FROM doc.DocumentHeaderValue WHERE DocumentId = {doc.DocumentId} " +
                $"AND HeaderFieldDefId = {field.Id}") as string);
    }

    private static async Task<string?> HeaderValueAsync(
        SqlConnection connection, SqlTransaction tx, long documentId, int fieldId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            $"SELECT ValueString FROM doc.DocumentHeaderValue WHERE DocumentId = {documentId} AND HeaderFieldDefId = {fieldId}";
        return await command.ExecuteScalarAsync() as string;
    }
}
