// src/Ecr.Infrastructure/Persistence/CellChangeWatermarks.cs
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ W15z M7 (огляд b11 №3): закомічена межа журналу комірок — найбільший <c>aud.CellChange.Id</c>, нижче якого (включно)
/// НЕМАЄ жодної незакоміченої вставки. Прогін, що зняв межу до читання входів, бачить кожну правку з <c>Id</c> ≤ межі.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Чому не <c>IDENT_CURRENT</c> і не <c>MAX(Id)</c> самі по собі: ідентичність видається на вставці, а коміт — пізніше;
/// правка з меншим <c>Id</c> може бути ще в польоті, коли більший уже закомічено (чи коли <c>IDENT_CURRENT</c> уже його
/// видав). Межа «за часом» (<c>ChangedAt</c> — мітка ПОЧАТКУ транзакції правки, годинник Api) має ту саму ваду й ще
/// розбіжність годинників хостів.
/// </para>
/// <para>
/// Як: коротка окрема транзакція бере табличне S-блокування <c>aud.CellChange</c> (<c>TABLOCK, HOLDLOCK</c>; ескалація
/// таблиці — <c>TABLE</c>). Кожен записувач тримає IX на таблиці від вставки до коміту (журнал пишеться в транзакції
/// комірок, WR-08), тож отримане S означає: незакомічених вставок немає, і поки воно тримається, нових немає. Під ним
/// <c>IDENT_CURRENT</c> — межа: усе ≤ неї закомічено або відкочено (дірка). Порожня таблиця — 0.
/// </para>
/// <para>
/// ⚠ Ціна — записувачі журналу чекають мілісекунди знімка; довгий записувач (імпорт) — не довше <c>LOCK_TIMEOUT</c>
/// знімка: таймаут дає <c>null</c> («невідомо» — позначку не знімати), а не помилку прогону. У відкритій транзакції
/// контексту знімок не робиться (<c>null</c>): власні блокування транзакції зробили б S несумісним із нею самою не
/// завжди, а відкат знімка відкотив би її.
/// </para>
/// </remarks>
internal static class CellChangeWatermarks
{
    /// <summary>Скільки знімок чекає на табличне блокування, мс.</summary>
    internal const int DefaultLockTimeoutMs = 2_000;

    /// <summary>Закомічена межа журналу комірок; <c>null</c> — не вдалося (таймаут, відкрита транзакція).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="lockTimeoutMs">Очікування табличного блокування, мс.</param>
    /// <returns>Межа або <c>null</c>.</returns>
    internal static async Task<long?> CommittedAsync(EcrDbContext db, CancellationToken ct, int lockTimeoutMs = DefaultLockTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is not null)
        {
            return null;
        }

        // LOCK_TIMEOUT не приймає змінну — ціле число вписується в текст (не ввід користувача).
        var sql = "SET LOCK_TIMEOUT " + Math.Max(0, lockTimeoutMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + ";\n" + Body;
        var found = await db.Database
            .SqlQueryRaw<long?>(sql)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.FirstOrDefault();
    }

    private const string Body = """
                DECLARE @w bigint = NULL;
                IF @@TRANCOUNT = 0
                BEGIN
                    BEGIN TRY
                        BEGIN TRANSACTION;
                        DECLARE @any int = (SELECT TOP (1) 1 FROM aud.CellChange WITH (TABLOCK, HOLDLOCK));
                        SET @w = CASE WHEN @any IS NULL THEN 0 ELSE CAST(IDENT_CURRENT('aud.CellChange') AS bigint) END;
                        COMMIT TRANSACTION;
                    END TRY
                    BEGIN CATCH
                        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                        SET @w = NULL;
                    END CATCH
                END
                SET LOCK_TIMEOUT -1;
                SELECT @w AS Value;
                """;
}
