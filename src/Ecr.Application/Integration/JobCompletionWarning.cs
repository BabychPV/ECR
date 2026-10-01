// src/Ecr.Application/Integration/JobCompletionWarning.cs
using System.Globalization;
using Ecr.Application.Ports;

namespace Ecr.Application.Integration;

/// <summary>
/// Похідний стан «виконано з попередженням» для задач, що завершуються
/// <c>Succeeded</c>, але нічого корисного не зробили (<c>NotificationJob</c>:
/// «Failures in digest: N; sent: 0» — транспорт сповіщень не налаштований).
/// </summary>
/// <remarks>
/// ⛔ Не новий стан автомата і не виняток із задачі: <c>Failed</c> змусив би
/// політику повторів перезапустити дайджест і задублювати
/// <c>NotificationOutbox</c>. Попередження виводиться при ЧИТАННІ з конверта
/// повідомлення (<c>jobs.notificationDone</c>), як і <c>FanOutStatus</c>, — схема
/// не змінюється; клієнт показує його наявним бейджем <c>SucceededWithErrors</c>.
/// </remarks>
public static class JobCompletionWarning
{
    /// <summary>Значення <c>EffectiveState</c> для попередження.</summary>
    public const string SucceededWithErrors = "SucceededWithErrors";

    /// <summary>Ключ підсумку <c>NotificationJob</c>.</summary>
    public const string NotificationDoneKey = "jobs.notificationDone";

    /// <summary>
    /// <c>SucceededWithErrors</c> для <c>Succeeded</c> дайджесту зі збоями
    /// (<c>count</c> &gt; 0) і нулем відправлених (<c>sent</c> = 0); інакше <c>null</c>.
    /// </summary>
    public static string? EffectiveStateOf(string state, string? rawMessage)
    {
        if (!string.Equals(state, "Succeeded", StringComparison.Ordinal)
            || !JobProgressMessageCodec.TryDecode(rawMessage, out var envelope)
            || !string.Equals(envelope.Key, NotificationDoneKey, StringComparison.Ordinal)
            || envelope.Params is not { } p
            || !TryInt(p, "count", out var failures)
            || !TryInt(p, "sent", out var sent))
        {
            return null;
        }

        return failures > 0 && sent == 0 ? SucceededWithErrors : null;
    }

    private static bool TryInt(IReadOnlyDictionary<string, string> p, string name, out int value)
    {
        value = 0;

        return p.TryGetValue(name, out var s)
               && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
