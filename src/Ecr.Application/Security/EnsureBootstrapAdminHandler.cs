// src/Ecr.Application/Security/EnsureBootstrapAdminHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Створює локального адміністратора при першому старті (ФВ-6.18, D-115).
/// Пароль — зі змінної середовища <c>ECR_Bootstrap__Password</c>.
/// </summary>
/// <remarks>
/// Без цього запису систему неможливо налаштувати до під'єднання домену. Але
/// він же є найпривабливішою мішенню, тому живе за трьома обмеженнями: один на
/// систему, обов'язкова зміна пароля при першому вході, автоматичне вимкнення
/// після появи доменного адміністратора.
/// </remarks>
public sealed class EnsureBootstrapAdminHandler(
    IUserStore users, IPasswordHasher hasher, IUnitOfWork uow, IClock clock)
{
    private readonly List<string> _warnings = [];

    /// <summary>
    /// Попередження старту.
    /// </summary>
    /// <remarks>
    /// ⛔ Жодне з них не містить пароля — ні значення, ні довжини, ні
    /// фрагмента (ФВ-6.11). Попередження старту йдуть у лог, а лог живе довше
    /// за секрет.
    /// </remarks>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Що зроблено останнім викликом.</summary>
    public BootstrapAdmin.Outcome LastOutcome { get; private set; } = BootstrapAdmin.Outcome.Skip;

    /// <summary>Виконує рішення про bootstrap-запис.</summary>
    /// <param name="bootstrapPassword">Значення змінної; <c>null</c> — не задано.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(string? bootstrapPassword, CancellationToken ct)
    {
        _warnings.Clear();

        var existing = await users.FindBootstrapAdminAsync(ct).ConfigureAwait(false);
        var hasDomainAdmin = await users
            .HasActiveDomainAdminAsync(BootstrapAdmin.AdminPermission, ct).ConfigureAwait(false);

        LastOutcome = BootstrapAdmin.Decide(
            !string.IsNullOrWhiteSpace(bootstrapPassword), existing, hasDomainAdmin);

        switch (LastOutcome)
        {
            case BootstrapAdmin.Outcome.Warn:
                // Не помилка: контур, де вхід уже переведено на домен, стартує
                // саме так (D-115). Падіння тут зупинило б робочу систему через
                // відсутність запису, який їй не потрібен.
                _warnings.Add(
                    "Локального адміністратора немає, і змінна ECR_Bootstrap__Password не задана. " +
                    "Якщо доменний вхід ще не налаштовано, увійти буде нікому.");
                return;

            case BootstrapAdmin.Outcome.Disable:
                // Вимкнення — робота DisableBootstrapAdminHandler: воно має йти
                // в аудит із автором, а старт автора не має.
                _warnings.Add(
                    "Bootstrap-адміністратор ще активний, хоча доменний адміністратор уже є.");
                return;

            case BootstrapAdmin.Outcome.Create:
                break;

            default:
                return;
        }

        // ⛔ Пароль існує лише як аргумент цього рядка: далі йде хеш.
        var user = BootstrapAdmin.Create(hasher.Hash(bootstrapPassword!), clock.UtcNow);

        users.Add(user);
        await users.GrantRoleAsync(user, BootstrapAdmin.RoleCode, ct).ConfigureAwait(false);

        // ⚠ Унікальність гарантує частковий індекс UX_User_Bootstrap: гонка
        // двох інстансів при старті дасть помилку вставки, а не другий запис.
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
