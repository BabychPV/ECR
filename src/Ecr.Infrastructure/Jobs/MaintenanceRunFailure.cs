// src/Ecr.Infrastructure/Jobs/MaintenanceRunFailure.cs
using System.Text.Encodings.Web;
using System.Text.Json;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Закриває прогін у <c>itg.MaintenanceRun</c>, коли задача впала (<c>Q-239</c>).
/// </summary>
/// <remarks>
/// ⛔ Задача відкривала прогін (<c>Status = "Running"</c>, <c>FinishedAt = NULL</c>)
/// і закривала його ЛИШЕ на успішному шляху. Виняток — і рядок лишався
/// <c>Running</c> назавжди. Наслідок не в самому рядку, а в тому, хто його
/// читає: зведення <see cref="NotificationJob"/> бере збої запитом
/// <c>FinishedAt &gt;= since &amp;&amp; Status != "Succeeded"</c>, а
/// <c>NULL &gt;= since</c> у SQL — не істина. Тобто провалена нічна перевірка
/// не потрапляла у зведення ЖОДНОГО разу і не породжувала ані рядка, ані
/// листа. Рівно та сама тиша замість затримки (ІНТ-3.3), яку решта конвеєра
/// сповіщень свідомо не дозволяє.
/// <para>
/// ⚠ Стан <c>"Failed"</c> не новий: <see cref="MaintenanceRun.Complete"/>
/// документує його третім можливим значенням від самого початку — просто
/// жоден шлях коду його не писав. Це прогалина, а не рішення.
/// </para>
/// <para>
/// ⚠ Скасування (<c>OperationCanceledException</c>) сюди НЕ потрапляє —
/// свідомо, тим самим правилом, що й у <c>RecalculationJob</c>: зупинку
/// застосунку попросили, і позначати її провалом означало б слати лист про
/// кожне розгортання. Стан скасування вже фіксує
/// <see cref="QuartzJobAdapter"/> у <c>itg.JobProgress</c>.
/// </para>
/// </remarks>
internal static class MaintenanceRunFailure
{
    /// <summary>Стан прогону, що завершився виключенням.</summary>
    public const string FailedStatus = "Failed";

    /// <summary>
    /// Скільки символів тексту помилки лишається в <c>DetailsJson</c>.
    /// </summary>
    /// <remarks>
    /// Повідомлення SQL Server про дедлок буває на кілька кілобайт; у зведенні
    /// воно витіснило б решту рядків, заради яких зведення й читають.
    /// </remarks>
    public const int MaxErrorLength = 500;

    /// <summary>
    /// Налаштування серіалізації; спільні на всі виклики.
    /// </summary>
    /// <remarks>
    /// ⛔ Кодувальник — послаблений НАВМИСНО. Цей рядок не йде в HTML: він
    /// лягає в <c>itg.MaintenanceRun.DetailsJson</c>, а звідти
    /// <see cref="NotificationJob"/> вставляє його ТЕКСТОМ у тіло листа. З
    /// типовим кодувальником кирилиця виходить послідовністю
    /// <c>\u04XX</c> — тобто лист, який формально надіслано і якого
    /// неможливо прочитати.
    /// </remarks>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Позначає прогін проваленим і зберігає це окремо від решти змін.
    /// </summary>
    /// <param name="db">Контекст, яким задача відкривала прогін.</param>
    /// <param name="run">Прогін, який лишився незакритим.</param>
    /// <param name="error">Виняток, що обірвав задачу.</param>
    /// <param name="utcNow">Момент завершення.</param>
    /// <remarks>
    /// ⛔ Трекер очищається ПЕРЕД записом — той самий механізм, що вже описаний
    /// у <c>RecalculationJob</c>. <c>EcrDbContext</c> тут спільний зі сховищем
    /// прогресу (<c>JobProgressStore</c>, той самий DI-скоуп): якщо лишити в
    /// трекері сутність, чий запис щойно провалився, наступне
    /// <c>SaveChangesAsync</c> — навіть чуже — повторно спробує записати той
    /// самий зіпсований рядок і провалиться теж. Тоді
    /// <see cref="QuartzJobAdapter"/> не зможе позначити задачу <c>Failed</c>,
    /// і замість одного невидимого стану вийшло б два.
    ///
    /// ⚠ Очищення трекера ВІДКИДАЄ незбережені зміни задачі — і це правильно:
    /// задача впала, її напівроблена робота не має доїхати до бази повз
    /// власний сценарій.
    ///
    /// ⛔ <c>CancellationToken.None</c>, а не токен задачі: причиною падіння
    /// могло бути саме скасування сусідньої операції, і запис про провал,
    /// скасований тим самим токеном, лишив би рядок <c>Running</c> — тобто
    /// рівно той стан, заради якого все це й пишеться.
    ///
    /// ⚠ Помилка самого запису не підміняє початкову: її ковтаємо, бо
    /// викликач одразу кидає свій виняток далі, і той — справжня причина.
    /// Кинути звідси означало б підмінити «чому задача впала» на «чому не
    /// вдалося записати, що вона впала».
    /// </remarks>
    public static async Task RecordAsync(
        EcrDbContext db, MaintenanceRun run, Exception error, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(error);

        // Позначати нема чого, якщо самого рядка в базі немає: INSERT прогону
        // міг і не відбутися — тоді впало вже на ньому.
        if (run.Id <= 0)
        {
            return;
        }

        try
        {
            db.ChangeTracker.Clear();
            db.MaintenanceRuns.Attach(run);

            run.Complete(FailedStatus, Details(error), utcNow);

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // База не прийняла запис — початковий виняток однаково піде нагору
            // і потрапить у itg.JobProgress через QuartzJobAdapter.
        }
        catch (InvalidOperationException)
        {
            // Контекст уже непридатний (розірване з'єднання, зіпсований стан) —
            // те саме міркування.
        }
    }

    /// <summary>Текст помилки як <c>DetailsJson</c> — без стека (ФВ-6.11).</summary>
    private static string Details(Exception error)
    {
        var message = error.Message;

        if (message.Length > MaxErrorLength)
        {
            message = string.Concat(message.AsSpan(0, MaxErrorLength), "…");
        }

        return JsonSerializer.Serialize(new { error = message }, Options);
    }
}
