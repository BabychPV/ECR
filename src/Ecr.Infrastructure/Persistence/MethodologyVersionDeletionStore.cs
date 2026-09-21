// src/Ecr.Infrastructure/Persistence/MethodologyVersionDeletionStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IMethodologyVersionDeletionStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⛔ Усі зовнішні ключі на <c>calc.MethodologyVersion</c> — <c>Restrict</c>, тож вміст
/// видаляється явно, від листя до кореня. Забутий набір не мовчить: базу зупинить FK.
/// </remarks>
public sealed class MethodologyVersionDeletionStore(EcrDbContext db) : IMethodologyVersionDeletionStore
{
    /// <inheritdoc />
    public async Task<(MethodologyVersion Version, bool UsedInCalculations)?> LockAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        // UPDLOCK, HOLDLOCK: паралельна публікація чекає, доки видалення завершиться.
        var version = await db.MethodologyVersions
            .FromSql($"SELECT * FROM calc.MethodologyVersion WITH (UPDLOCK, HOLDLOCK) WHERE Id = {methodologyVersionId}")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        // Архів (`arc.CalculationResult`) теж: перенесений туди рік лишається
        // порахованим цією версією, і без неї його не пояснити.
        var used = await db.Database
            .SqlQuery<int>($"""
                SELECT CASE WHEN EXISTS (SELECT 1 FROM calc.CalculationResult WHERE MethodologyVersionId = {methodologyVersionId})
                              OR EXISTS (SELECT 1 FROM arc.CalculationResult WHERE MethodologyVersionId = {methodologyVersionId})
                            THEN 1 ELSE 0 END AS Value
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);

        return (version, used == 1);
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(int methodologyVersionId, CancellationToken ct)
    {
        var id = methodologyVersionId;
        var children = 0;

        children += await db.MethodologyFormulas.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        children += await db.MethodologyConstants.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        children += await db.MethodologyRules.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        children += await db.MethodologyRequiredInputs.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        children += await db.MethodologyImports.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        children += await db.MethodologySubstances.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        children += await db.MethodologyOutputs.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        children += await db.MethodologyTestCases.Where(x => x.MethodologyVersionId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await db.MethodologyVersions.Where(v => v.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return children;
    }
}
