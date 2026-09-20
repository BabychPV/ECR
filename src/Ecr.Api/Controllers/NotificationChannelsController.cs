using Ecr.Application.Notifications;
using Ecr.Domain.Entities.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Канали сповіщень (<c>BE-33</c>). Право <c>System.ManageNotifications</c>.</summary>
/// <remarks>⛔ Жодна відповідь не містить секрету — лише <c>hasSecret</c>.</remarks>
[ApiController]
[Route("api/v1/notifications/channels")]
[Authorize]
public sealed class NotificationChannelsController(
    ListNotificationChannelsHandler list,
    SaveNotificationChannelHandler save,
    DeleteNotificationChannelHandler delete,
    ReplaceNotificationChannelSecretHandler secret,
    TestNotificationChannelHandler test) : ControllerBase
{
    /// <summary>Усі канали за назвою.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<NotificationChannelView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Створює ввімкнений канал без секрету.</summary>
    [HttpPost]
    [ProducesResponseType<NotificationChannelView>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateNotificationChannelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await save
            .CreateAsync(request.Kind, request.Name, request.Settings, ct).ConfigureAwait(false);

        return Created(new Uri("/api/v1/notifications/channels", UriKind.Relative), created);
    }

    /// <summary>Змінює назву, стан і несекретні параметри.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<NotificationChannelView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(
        int id, [FromBody] UpdateNotificationChannelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await save
            .UpdateAsync(id, request.Name, request.IsEnabled, request.Settings, ct).ConfigureAwait(false));
    }

    /// <summary>Видаляє канал разом із його правилами; журнал доставок лишається.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await delete.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Замінює секрет (пароль SMTP або URL вебхука Teams); порожній — прибирає.</summary>
    /// <remarks>
    /// ⛔ URL вебхука: лише <c>https</c> і хост із переліку
    /// <c>Notifications:WebhookAllowedHostSuffixes</c>, інакше <c>422 ECR-REQ-0422</c>.
    /// </remarks>
    [HttpPut("{id:int}/secret")]
    [ProducesResponseType<NotificationChannelView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplaceSecret(
        int id, [FromBody] ReplaceNotificationChannelSecretRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await secret.HandleAsync(id, request.Secret, ct).ConfigureAwait(false));
    }

    /// <summary>Шле пробне повідомлення; відмова каналу — <c>ok: false</c>, а не помилка запиту.</summary>
    [HttpPost("{id:int}/test")]
    [ProducesResponseType<NotificationTestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Test(int id, CancellationToken ct)
        => Ok(await test.HandleAsync(id, ct).ConfigureAwait(false));
}

/// <summary>Тіло створення каналу.</summary>
/// <param name="Kind">Транспорт; після створення не змінюється.</param>
/// <param name="Name">Назва, унікальна серед каналів.</param>
/// <param name="Settings">Несекретні параметри.</param>
public sealed record CreateNotificationChannelRequest(
    NotificationChannelKind Kind, string Name, NotificationChannelSettings? Settings = null);

/// <summary>Тіло зміни каналу.</summary>
/// <param name="Name">Назва.</param>
/// <param name="IsEnabled">Чи ввімкнений.</param>
/// <param name="Settings">Несекретні параметри.</param>
public sealed record UpdateNotificationChannelRequest(
    string Name, bool IsEnabled, NotificationChannelSettings? Settings = null);

/// <summary>Тіло заміни секрету.</summary>
/// <param name="Secret">Новий секрет; <c>null</c> або порожній — прибрати.</param>
public sealed record ReplaceNotificationChannelSecretRequest(string? Secret);
