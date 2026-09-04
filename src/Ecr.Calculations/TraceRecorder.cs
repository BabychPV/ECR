using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Накопичує трейс обчислення згідно з <see cref="TraceLevel"/>.
/// </summary>
/// <remarks>
/// Політика зберігання — «нічого не затирається» (ЗБР-1), тому обсяг керується
/// **тим, що пишемо**, а не строком: записане живе назавжди, непотрібне просто
/// не пишеться (ЗБР-3). Повний трейс кожного кроку кожної методології кожного
/// періоду перевищив би обсяг самих даних.
/// </remarks>
public sealed class TraceRecorder(TraceLevel level)
{
    /// <summary>Записує крок, якщо рівень це передбачає.</summary>
    public void Step(string code, string? expression, decimal? value)
        => throw new NotImplementedException(
            "TODO: Off → нічого; ErrorsOnly → лише кроки з помилкою; Full → усі. " +
            "Накопичувати в пам'яті, писати пакетно наприкінці прогону.");

    /// <summary>Зібрані кроки.</summary>
    public IReadOnlyList<TraceStep> Steps => throw new NotImplementedException("TODO");
}

/// <summary>Крок трейсу.</summary>
public sealed record TraceStep(int Order, string Code, string? Expression, decimal? Value, string? Error);
