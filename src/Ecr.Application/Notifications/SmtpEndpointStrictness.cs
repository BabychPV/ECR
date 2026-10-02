// src/Ecr.Application/Notifications/SmtpEndpointStrictness.cs
namespace Ecr.Application.Notifications;

/// <summary>
/// Контекст «ПРОБА»: у ньому політика напрямку пошти закрита при збої DNS (fail-closed). Поза ним (черга сповіщень) —
/// fail-open із Warning-логом. Потік — <see cref="AsyncLocal{T}"/>, тож нічого не міняє в інтерфейсі відправника.
/// </summary>
public static class SmtpEndpointStrictness
{
    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>Чи діє суворий режим у поточному потоці виконання.</summary>
    public static bool IsStrict => Current.Value;

    /// <summary>Вмикає суворий режим до звільнення результату.</summary>
    public static IDisposable Begin()
    {
        var previous = Current.Value;
        Current.Value = true;

        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}