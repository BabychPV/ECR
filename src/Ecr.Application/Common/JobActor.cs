namespace Ecr.Application.Common;

/// <summary>
/// Від чийого імені виконується фонова задача: користувач, що її поставив.
/// </summary>
/// <param name="UserId">Автор задачі — ним перевіряються права й підписується аудит.</param>
/// <param name="UserName">Ім'я для аудиту й повідомлень; <c>null</c> — невідоме.</param>
/// <param name="Language">Мова автора на момент постановки.</param>
/// <param name="GroupSids">
/// SID груп із токена входу на момент постановки — доменному користувачеві ролі
/// призначаються здебільшого на групу, а токена у фоні немає.
/// </param>
/// <param name="CorrelationId">Кореляція запиту-постановника: та сама, що в журналі задачі.</param>
/// <remarks>
/// ⛔ F-01 (UX-PASS, четвертий раунд). Запис комірок вимагає поточного
/// користувача, а єдина реалізація <see cref="ICurrentUser"/> бере його з
/// HTTP-запиту. У задачі запиту немає — і великий імпорт (&gt; 2000 комірок)
/// падав на 10 % із <c>ECR-AUTH-0401</c> ЗАВЖДИ, не записавши жодної комірки.
/// Задача тепер несе автора в завданні й виконується від його імені, а не від
/// «нікого» і не від системи: права — його, рядок журналу — його.
/// </remarks>
public sealed record JobActor(
    int UserId,
    string? UserName,
    string Language,
    IReadOnlyList<string> GroupSids,
    string CorrelationId);

/// <summary>
/// Автор фонової задачі в межах її DI-scope.
/// </summary>
/// <remarks>
/// ⚠ Scoped і змінний навмисно. Залежності задачі (імпортер, обробник запису
/// комірок) створюються контейнером ДО того, як задача прочитала своє
/// завдання, тож передати автора конструктором неможливо. Натомість вони
/// отримують <see cref="JobAwareCurrentUser"/>, який читає цей тримач ЛІНИВО —
/// у мить звернення до властивості, коли автора вже встановлено.
/// </remarks>
public sealed class JobActorScope
{
    /// <summary>Автор, від імені якого зараз виконується задача; <c>null</c> — поза задачею.</summary>
    public JobActor? Current { get; private set; }

    /// <summary>Встановлює автора до кінця виконання задачі.</summary>
    /// <param name="actor">Автор.</param>
    /// <returns>Звільнення повертає попередній стан.</returns>
    /// <exception cref="InvalidOperationException">Автора вже встановлено в цьому scope.</exception>
    /// <remarks>
    /// ⛔ Повторне встановлення — помилка, а не заміна: дві задачі в одному
    /// scope означали б, що друга тихо переписує автора першої посеред запису.
    /// </remarks>
    public IDisposable Enter(JobActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Current is not null)
        {
            throw new InvalidOperationException(
                "Автора фонової задачі вже встановлено в цьому scope: одна задача — один scope.");
        }

        Current = actor;

        return new Exit(this);
    }

    private sealed class Exit(JobActorScope owner) : IDisposable
    {
        public void Dispose() => owner.Current = null;
    }
}

/// <summary>
/// <see cref="ICurrentUser"/>, що у фоновій задачі повертає її автора, а в
/// HTTP-запиті — користувача запиту.
/// </summary>
/// <param name="request">Користувач HTTP-запиту (cookie).</param>
/// <param name="scope">Автор задачі, якщо scope належить задачі.</param>
/// <remarks>
/// ⚠ Симуляції «очима користувача» у задачі немає й бути не може: її знає лише
/// сеанс входу (V-06), і задача, що успадкувала б чужу симуляцію, писала б під
/// забороною, якої автор не вмикав.
/// </remarks>
public sealed class JobAwareCurrentUser(ICurrentUser request, JobActorScope scope) : ICurrentUser
{
    /// <inheritdoc />
    public int? UserId => scope.Current is { } actor ? actor.UserId : request.UserId;

    /// <inheritdoc />
    public string? UserName => scope.Current is { } actor ? actor.UserName : request.UserName;

    /// <inheritdoc />
    public string CorrelationId => scope.Current is { } actor ? actor.CorrelationId : request.CorrelationId;

    /// <inheritdoc />
    public string Language => scope.Current is { } actor ? actor.Language : request.Language;

    /// <inheritdoc />
    public IReadOnlyList<string> GroupSids => scope.Current is { } actor ? actor.GroupSids : request.GroupSids;

    /// <inheritdoc />
    public long? SimulationSessionId => scope.Current is null ? request.SimulationSessionId : null;
}
