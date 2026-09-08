using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Заводить адміністратора з потрібними правами ЧЕРЕЗ HTTP — той самий шлях,
/// що <c>tools/e2e-stand.ps1</c>: bootstrap → роль → користувач → грант.
/// </summary>
/// <remarks>
/// ⚠ Це НЕ один із трьох названих допоміжників і не <c>ProjectBuilder</c>,
/// який замінював би собою бізнес-логіку: кожен метод тут — це рівно ті самі
/// HTTP-виклики, які зробив би адміністратор руками. Причина, чому він
/// існує окремо від сценаріїв: bootstrap-обліковий запис у системі РІВНО
/// ОДИН (`ФВ-6.18`, унікальний індекс), а <see cref="Ecr.TestKit.SqlServerFixture"/>
/// створює ОДНУ базу на всю збірку. 28 сценаріїв тому фізично діляться цим
/// самим записом, і те, хто з них заходить під ним першим, залежить від
/// порядку виконання xUnit, а не від сценарію.
///
/// ⛔ Тому одноразовий пароль bootstrap тут не жорстко очікується, а
/// вирішується ЗА ДВОМА кандидатами (початковий і робочий) — це не
/// «сценарій підлаштувався під провал», а коректна модель СПІЛЬНОГО
/// одноразового ресурсу. Сам факт «пароль можна змінити» перевіряє S-01
/// напряму, власним послідовним викликом, не через цей клас (він читає
/// поточний пароль і одразу змінює його ще раз на свій — ФВ-6.18 не вимагає,
/// щоб зміна пароля була можлива рівно один раз).
/// </remarks>
internal static class Provisioning
{
    private const string InitialBootstrapPassword = "Scenario-Bootstrap-2026-Initial!";
    private const string WorkingBootstrapPassword = "Scenario-Bootstrap-2026-Working!";
    private const string IssuedUserPassword = "Scenario-Issued-2026!";
    private const string WorkUserPassword = "Scenario-Work-2026!";

    private static readonly object Gate = new();
    private static string _bootstrapPassword = InitialBootstrapPassword;

    /// <summary>Обліковий запис, готовий діяти в системі: клієнт і його ідентифікатори.</summary>
    /// <param name="Client">Автентифікований клієнт. Застаріває одразу після зміни ролей/грантів (ФВ-6.7) —
    /// див. <see cref="ReauthenticateAsync"/>.</param>
    /// <param name="UserId">Обліковий запис.</param>
    /// <param name="RoleId">Роль, призначена цьому обліковому запису.</param>
    /// <param name="UserName">Ім'я входу — для повторного входу після зміни SecurityStamp.</param>
    public sealed record Administrator(HttpClient Client, int UserId, int RoleId, string UserName);

    /// <summary>
    /// Поточний чинний пароль bootstrap у цьому прогоні — для S-01, який сам,
    /// явно і послідовно, доводить, що bootstrap може змінити пароль ще раз.
    /// </summary>
    public static string CurrentBootstrapPassword()
    {
        lock (Gate)
        {
            return _bootstrapPassword;
        }
    }

    /// <summary>Фіксує пароль bootstrap ПІСЛЯ того, як сценарій сам його змінив.</summary>
    public static void AdvanceBootstrapPassword(string newPassword)
    {
        lock (Gate)
        {
            _bootstrapPassword = newPassword;
        }
    }

    /// <summary>
    /// Клієнт, автентифікований як bootstrap-адміністратор (`ФВ-6.18`).
    /// </summary>
    public static async Task<HttpClient> BootstrapAdministratorAsync(EcrApiFactory app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Environment.SetEnvironmentVariable("ECR_Bootstrap__Password", InitialBootstrapPassword);

        string attempt;
        lock (Gate)
        {
            attempt = _bootstrapPassword;
        }

        var client = app.CreateClient();
        var login = await client
            .PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = "bootstrap", password = attempt })
            .ConfigureAwait(false);

        if (!login.IsSuccessStatusCode && !string.Equals(attempt, WorkingBootstrapPassword, StringComparison.Ordinal))
        {
            client = app.CreateClient();
            attempt = WorkingBootstrapPassword;
            login = await client
                .PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = "bootstrap", password = attempt })
                .ConfigureAwait(false);
        }

        Assert.True(
            login.IsSuccessStatusCode,
            $"вхід bootstrap не пройшов жодним із відомих паролів: {login.StatusCode}: {app.ErrorsText}");

        // На цей момент `attempt` — ПІДТВЕРДЖЕНО чинний пароль (щойно ним
        // увійшли), незалежно від того, чи це був перший здогад, чи фолбек.
        var me = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(false);
        if (me.GetProperty("mustChangePassword").GetBoolean())
        {
            var change = await client
                .PostAsJsonAsync(
                    new Uri("/api/v1/auth/change-password", UriKind.Relative),
                    new { currentPassword = attempt, newPassword = WorkingBootstrapPassword })
                .ConfigureAwait(false);
            Assert.True(
                change.IsSuccessStatusCode,
                $"зміна одноразового пароля bootstrap: {change.StatusCode}: {app.ErrorsText}");

            client = app.CreateClient();
            login = await client
                .PostAsJsonAsync(
                    new Uri("/api/v1/login/local", UriKind.Relative),
                    new { userName = "bootstrap", password = WorkingBootstrapPassword })
                .ConfigureAwait(false);
            Assert.True(login.IsSuccessStatusCode, $"повторний вхід bootstrap: {login.StatusCode}: {app.ErrorsText}");
            attempt = WorkingBootstrapPassword;
        }

        // ⛔ Записуємо САМЕ ПІДТВЕРДЖЕНИЙ пароль (`attempt`), а не безумовно
        // константу `WorkingBootstrapPassword`. Безумовний запис тут раніше
        // затирав пароль, який S-01 (чи будь-хто інший) уже просунув ДАЛІ за
        // цю константу своєю власною зміною: наступний виклик намагався
        // увійти значенням, якого в базі вже немає, і кожен такий промах —
        // це один зайвий запис у `sec.LoginAttempt`, що за п'ять сценаріїв
        // поспіль замикав bootstrap у `ECR-AUTH-0423` для решти прогону.
        lock (Gate)
        {
            _bootstrapPassword = attempt;
        }

        return client;
    }

    /// <summary>
    /// Заводить роль з переліком прав і користувача з нею — точно як в
    /// <c>e2e-stand.ps1</c> (роль → користувач → зміна одноразового пароля).
    /// </summary>
    public static async Task<Administrator> AdministratorAsync(
        EcrApiFactory app, string roleCodePrefix, IReadOnlyList<string> permissionCodes)
    {
        ArgumentNullException.ThrowIfNull(app);

        var bootstrap = await BootstrapAdministratorAsync(app).ConfigureAwait(false);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var roleCode = $"{roleCodePrefix}{suffix}";

        var roleResponse = await bootstrap
            .PostAsJsonAsync(
                new Uri("/api/v1/roles", UriKind.Relative),
                new
                {
                    code = roleCode,
                    nameL10n = new Dictionary<string, string> { ["en"] = roleCode },
                    permissionCodes,
                })
            .ConfigureAwait(false);
        Assert.True(roleResponse.IsSuccessStatusCode, $"створення ролі {roleCode}: {roleResponse.StatusCode}: {app.ErrorsText}");
        var roleId = (await roleResponse.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false))
            .GetProperty("roleId").GetInt32();

        var userName = $"u{suffix}";
        var userResponse = await bootstrap
            .PostAsJsonAsync(
                new Uri("/api/v1/users", UriKind.Relative),
                new
                {
                    userName,
                    provider = "Local",
                    sid = (string?)null,
                    displayName = userName,
                    initialPassword = IssuedUserPassword,
                    roleCodes = new[] { roleCode },
                })
            .ConfigureAwait(false);
        Assert.True(userResponse.IsSuccessStatusCode, $"створення користувача {userName}: {userResponse.StatusCode}: {app.ErrorsText}");
        var userId = (await userResponse.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false))
            .GetProperty("userId").GetInt32();

        var userClient = app.CreateClient();
        var login = await userClient
            .PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = IssuedUserPassword })
            .ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        var change = await userClient
            .PostAsJsonAsync(
                new Uri("/api/v1/auth/change-password", UriKind.Relative),
                new { currentPassword = IssuedUserPassword, newPassword = WorkUserPassword })
            .ConfigureAwait(false);
        Assert.True(change.IsSuccessStatusCode, $"зміна пароля {userName}: {change.StatusCode}: {app.ErrorsText}");

        userClient = app.CreateClient();
        var relogin = await userClient
            .PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = WorkUserPassword })
            .ConfigureAwait(false);
        Assert.True(relogin.IsSuccessStatusCode, $"повторний вхід {userName}: {relogin.StatusCode}: {app.ErrorsText}");

        return new Administrator(userClient, userId, roleId, userName);
    }

    /// <summary>Видає ресурсний грант ролі (`PUT /roles/{id}/grants`) через bootstrap.</summary>
    /// <remarks>
    /// ⛔ Заміна набору грантів ролі змінює `SecurityStamp` кожного, хто цю
    /// роль має (ФВ-6.7: «зміна... ролей... діє негайно»), тож будь-яка ЖИВА
    /// сесія користувача цієї ролі одразу застаріває — наступний виклик під
    /// нею отримає `401`, а не оновлений профіль прав. Викликач зобов'язаний
    /// увійти заново через <see cref="ReauthenticateAsync"/>.
    /// </remarks>
    public static async Task GrantAsync(
        EcrApiFactory app, int roleId, string resourceKind, int resourceId, string level, bool isDeny = false)
    {
        ArgumentNullException.ThrowIfNull(app);

        var bootstrap = await BootstrapAdministratorAsync(app).ConfigureAwait(false);
        var response = await bootstrap
            .PutAsJsonAsync(
                new Uri($"/api/v1/roles/{roleId}/grants", UriKind.Relative),
                new { grants = new[] { new { resourceKind, resourceId, level, isDeny } } })
            .ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"грант {resourceKind}:{resourceId}={level} ролі {roleId}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>
    /// Видає КІЛЬКА ресурсних грантів ролі одним <c>PUT</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Набір грантів РОЛІ замінюється ЦІЛКОМ (<c>ReplaceResourceGrantsHandler</c>),
    /// а не додається по одному: другий виклик <see cref="GrantAsync"/> стер би
    /// перший. Для сценаріїв, де позитивний грант і <c>isDeny</c> мають діяти
    /// РАЗОМ (ФВ-6.6 — заборона на ширшому рівні перекриває дозвіл на вужчому),
    /// обидва мусять піти в одному запиті.
    /// </remarks>
    public static async Task GrantManyAsync(
        EcrApiFactory app, int roleId,
        params (string ResourceKind, int ResourceId, string Level, bool IsDeny)[] grants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(grants);

        var bootstrap = await BootstrapAdministratorAsync(app).ConfigureAwait(false);
        var response = await bootstrap
            .PutAsJsonAsync(
                new Uri($"/api/v1/roles/{roleId}/grants", UriKind.Relative),
                new
                {
                    grants = grants.Select(g => new
                    {
                        resourceKind = g.ResourceKind,
                        resourceId = g.ResourceId,
                        level = g.Level,
                        isDeny = g.IsDeny,
                    }),
                })
            .ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"гранти ролі {roleId}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>
    /// Повторний вхід тим самим користувачем — коли попередня cookie
    /// застаріла через зміну ролей чи грантів.
    /// </summary>
    /// <remarks>
    /// ⛔ Це не обхід дефекту, а сама вимога ФВ-6.7: «зміна пароля, ролей або
    /// блокування діє негайно, а не після закінчення cookie». `SecurityStamp`
    /// призначеної ролі змінюється разом із її грантами, і наступний запит
    /// живою сесією отримує `401`, а не тихо оновлений профіль прав —
    /// відкликання діє негайно САМЕ так. Сценарій, який хоче продовжити
    /// роботу під тим самим користувачем після гранта, повинен увійти
    /// заново — так само, як довелося б людині.
    /// </remarks>
    public static async Task<Administrator> ReauthenticateAsync(EcrApiFactory app, Administrator administrator)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(administrator);

        var client = app.CreateClient();
        var login = await client
            .PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = administrator.UserName, password = WorkUserPassword })
            .ConfigureAwait(false);
        Assert.True(
            login.IsSuccessStatusCode,
            $"повторний вхід {administrator.UserName} після зміни гранта: {login.StatusCode}: {app.ErrorsText}");

        return administrator with { Client = client };
    }
}
