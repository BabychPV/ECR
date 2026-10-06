namespace Ecr.Api.Middleware;

/// <summary>
/// Оболонка SPA (<c>index.html</c> із фолбека) — навігаційний запит браузера,
/// а не виклик API (<c>A1-01</c>, <c>A1-09</c>).
/// </summary>
/// <remarks>
/// ⛔ Ворота сесії (<c>SecurityStampMiddleware</c>, <c>PasswordChangeMiddleware</c>)
/// стоять у конвеєрі ДО фолбека, тож до цього фіксу будь-яке ПОВНЕ
/// завантаження сторінки (F5, нова вкладка, закладка, навіть <c>/login</c>)
/// з разовим паролем отримувало сирий JSON <c>428 ECR-PWD-0428</c>, а після
/// зміни прав — сирий <c>401</c>: SPA не вантажився взагалі, і вийти з цього
/// стану, крім очищення cookie руками, було нічим.
///
/// ⚠ Оболонка й так анонімна (сторінку входу треба завантажити ДО входу), тож
/// пропуск воріт для неї не відкриває нічого: дані лежать лише за
/// <c>/api/**</c>, і там ворота діють як і раніше. Застосунок, завантажившись,
/// сам питає <c>/api/v1/me</c> і отримує звідти або <c>mustChangePassword</c>
/// (екран зміни пароля), або <c>401 ECR-AUTH-0401</c> із причиною (вхід із
/// поясненням). Тому для оболонки штамп навіть не звіряється: інакше вихід
/// стався б на завантаженні сторінки, і причину «права змінилися» клієнт уже
/// не побачив би.
///
/// ⚠ Ознака — МЕТАДАНІ кінцевої точки фолбека, а не шлях. Перелік «не-API
/// шляхів» розійшовся б із маршрутами першим же новим ендпоінтом поза
/// <c>/api</c>, і той тихо обійшов би ворота разового пароля.
/// </remarks>
public static class SpaShell
{
    /// <summary>Мітка кінцевої точки, що віддає <c>index.html</c>.</summary>
    public static readonly SpaShellEndpointMetadata Metadata = new();

    /// <summary>Чи це GET/HEAD на оболонку SPA.</summary>
    /// <param name="context">Контекст запиту (маршрут уже обрано).</param>
    public static bool IsShellNavigation(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && context.GetEndpoint()?.Metadata.GetMetadata<SpaShellEndpointMetadata>() is not null;
    }
}

/// <summary>Мітка кінцевої точки оболонки SPA (див. <see cref="SpaShell"/>).</summary>
public sealed class SpaShellEndpointMetadata;
