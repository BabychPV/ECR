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
    private readonly List<TraceStep> _steps = [];

    /// <summary>Рівень деталізації.</summary>
    public TraceLevel Level { get; } = level;

    /// <summary>Записує успішний крок, якщо рівень це передбачає.</summary>
    /// <param name="code">Код кроку — зазвичай код формули або виходу.</param>
    /// <param name="expression">Вираз як його бачив рушій.</param>
    /// <param name="value">Значення кроку.</param>
    public void Step(string code, string? expression, decimal? value)
    {
        // ⛔ Off не пише НІЧОГО — навіть у пам'ять. Накопичити «про всяк
        // випадок» і не зберегти означало б платити пам'яттю воркера за те,
        // що ніхто не прочитає; а на річному перерахунку це мільйони кроків.
        if (Level != TraceLevel.Full)
        {
            return;
        }

        _steps.Add(new TraceStep(_steps.Count + 1, code, expression, value, null));
    }

    /// <summary>Записує крок, що завершився помилкою.</summary>
    /// <param name="code">Код кроку.</param>
    /// <param name="expression">Вираз.</param>
    /// <param name="error">Код помилки-значення (<c>#DIV/0</c>, <c>#UNIT</c>).</param>
    /// <remarks>
    /// ⚠ Помилковий крок пишеться і на <c>ErrorsOnly</c>, і на <c>Full</c>:
    /// саме заради нього рівень <c>ErrorsOnly</c> і є типовим. Число, яке не
    /// порахувалося, без запису неможливо ні пояснити, ні відтворити.
    /// </remarks>
    public void Failed(string code, string? expression, string error)
    {
        if (Level == TraceLevel.Off)
        {
            return;
        }

        _steps.Add(new TraceStep(_steps.Count + 1, code, expression, null, error));
    }

    /// <summary>Зібрані кроки.</summary>
    public IReadOnlyList<TraceStep> Steps => _steps;
}

/// <summary>Крок трейсу.</summary>
public sealed record TraceStep(int Order, string Code, string? Expression, decimal? Value, string? Error);
