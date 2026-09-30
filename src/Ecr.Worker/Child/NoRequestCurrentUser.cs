// src/Ecr.Worker/Child/NoRequestCurrentUser.cs

using Ecr.Application.Common;

namespace Ecr.Worker.Child;

/// <summary>
/// «Користувач запиту» дочірнього воркера: HTTP-запиту тут немає й не буде,
/// тож ніхто.
/// </summary>
/// <remarks>
/// ⛔ Безпечна заглушка, а не спрощення: вона поводиться рівно так, як
/// <c>CurrentUser</c> Api поза запитом (фонова задача Quartz у процесі Api) —
/// анонім (<see cref="UserId"/> = <c>null</c>, жодних груп), тож будь-яка
/// перевірка прав ВІДМОВЛЯЄ, а не пропускає; <see cref="CorrelationId"/> кидає,
/// як і там. Автора задачі, коли вона його несе, дає <see cref="JobAwareCurrentUser"/>
/// з <see cref="JobActorScope"/> — поверх цього класу (<c>AddEcrJobActor</c>).
/// </remarks>
internal sealed class NoRequestCurrentUser : ICurrentUser
{
    /// <summary>Мова за замовчуванням — та сама, що в Api без запиту.</summary>
    private const string FallbackLanguage = "en";

    /// <inheritdoc />
    public int? UserId => null;

    /// <inheritdoc />
    public string? UserName => null;

    /// <inheritdoc />
    public string CorrelationId => throw new InvalidOperationException(
        "ICurrentUser використано поза запитом у воркері: кореляцію задачі дає її JobActor.");

    /// <inheritdoc />
    public string Language => FallbackLanguage;

    /// <inheritdoc />
    public IReadOnlyList<string> GroupSids => [];
}
