using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Виняткове блокування аркуша для ЗАПИСУ РЯДКІВ (створення рядка, пакет зі створенням рядків) — з відмовою,
/// що говорить правду про цю дію (Y7-03, аудит R11).
/// </summary>
/// <remarks>
/// ⛔ Що було. Створення рядків бере виняткове блокування аркуша (<see cref="ISheetEditGate.EnterSubmitAsync"/>) —
/// лише щоб серіалізувати «порахував рядки — вставив». Його відмова «зайнято» мала ключ подання
/// (<c>err.ECR-DOC-4091.sheetBeingEdited</c>: «Аркуш зараз зберігається чи перераховується. <b>Аркуш не подано</b>…»),
/// тобто для людини, яка додавала рядок, текст казав про подання, якого вона не робила; і клієнт не повторював
/// запит, бо розпізнає як минущі лише <c>lockTimeout</c> і <c>sheetBeingSubmitted</c>.
/// <para>
/// ⚠ Тепер ця відмова — <c>409 ECR-DOC-4091</c> з ключем <see cref="BusyKey"/> («дані зайняті, нічого не збережено,
/// повторіть за мить»): правда про рядок і той самий ключ, який клієнт уже повторює автоматично. Код і статус ті самі
/// (<c>409 ECR-DOC-4091</c>); справжнє подання аркуша (<c>SubmitSheetHandler</c>) бере те саме блокування напряму й
/// свого ключа не втрачає.
/// </para>
/// </remarks>
internal static class RowWriteLock
{
    /// <summary>Ключ відмови «дані зайняті» (той самий, що <c>LockWaitGuard.MessageKey</c>).</summary>
    public const string BusyKey = "err.ECR-DOC-4091.lockTimeout";

    /// <summary>Ключ відмови подання, який бере цей клас на себе (<c>SheetEditGate.Busy</c>, виняткове блокування).</summary>
    private const string SubmitBusyKey = "err.ECR-DOC-4091.sheetBeingEdited";

    /// <summary>Бере виняткове блокування аркуша для запису рядків; відмову «зайнято» читає як про рядки.</summary>
    /// <param name="gate">Шлюз блокувань.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task EnterAsync(
        ISheetEditGate gate, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);

        try
        {
            await gate.EnterSubmitAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException busy)
            when (busy.ErrorCode == ErrorCodes.SheetBusy
                  && busy.Details is { } details
                  && details.TryGetValue("messageKey", out var key)
                  && Equals(key, SubmitBusyKey))
        {
            var translated = new Dictionary<string, object?>(details, StringComparer.Ordinal)
            {
                ["messageKey"] = BusyKey,
            };

            throw new ConcurrencyConflictException(ErrorCodes.SheetBusy, busy.Message, translated);
        }
    }
}
