using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Templates;

/// <summary>Перелік шаблонів. Право <c>Template.View</c>.</summary>
public sealed class ListTemplatesHandler(
    ITemplateVersionStore templates, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право перегляду шаблонів.</summary>
    public const string Permission = "Template.View";

    /// <summary>Повертає сторінку шаблонів.</summary>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<TemplateSummary>> HandleAsync(CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⛔ Родина REQ, а не CELL (`P-25`, рядок 1): перелік шаблонів комірок
        // не має взагалі, тому `ECR-CELL-0422` доїжджав до обробника, у якого
        // для цієї відмови немає ні місця, ні тексту.
        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>
                {
                    // Наявний ключ, той самий патерн, що DocumentQueryHandlers/
                    // ListProjectsHandler для тієї самої перевірки курсорної сторінки.
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return await templates.ListTemplatesAsync(page, ct).ConfigureAwait(false);
    }

    /// <summary>Перевіряє функціональне право поточного користувача.</summary>
    /// <remarks>
    /// ⚠ Право перевіряється через <c>IAccessDecisionService</c>, а не
    /// атрибутом із назвою ролі: перевірка ролі поза єдиною точкою рішення
    /// заборонена архітектурним правилом 7. Атрибут бачить лише ім'я політики,
    /// а доступ у ECR залежить від ресурсу.
    /// </remarks>
    internal static async Task RequireAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401",
                         "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = permission,
                });
        }
    }
}

/// <summary>Створення шаблону. Право <c>Template.Edit</c>.</summary>
public sealed class CreateTemplateHandler(
    ITemplateVersionStore templates,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право редагування шаблонів.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює шаблон.</summary>
    /// <param name="code">Код шаблону, унікальний у системі.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<int> HandleAsync(
        string code, IReadOnlyDictionary<string, string> name, CancellationToken ct)
    {
        await ListTemplatesHandler.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var templateId = await templates
            .CreateTemplateAsync(code, name, currentUser.UserId!.Value, clock.UtcNow, ct)
            .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return templateId;
    }
}

/// <summary>Версії шаблону. Право <c>Template.View</c>.</summary>
public sealed class ListTemplateVersionsHandler(
    ITemplateVersionStore templates, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає сторінку версій шаблону.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<TemplateVersionSummary>> HandleAsync(
        int templateId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, ListTemplatesHandler.Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Q-225: та сама перевірка, що вже в ListTemplatesHandler поруч —
        // раніше тут її не було взагалі, і запит з абсурдним лімітом просто
        // мовчки обрізався б до нього, а не відхилявся зрозумілою помилкою.
        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var versions = await templates.ListVersionsAsync(templateId, page, ct).ConfigureAwait(false);

        // ⛔ B-07: неіснуючий шаблон давав `200` з порожньою сторінкою —
        // «версій немає» на адресі, якої не існує. Питаємо лише коли порожньо:
        // шаблон без жодної версії законний, але існувати мусить.
        if (versions.Items.Count == 0
            && await templates.FindTemplateAsync(templateId, ct).ConfigureAwait(false) is null)
        {
            throw new NotFoundException(
                ErrorCodes.TemplateNotFound,
                $"Шаблон {templateId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.template",
                    ["templateId"] = templateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return versions;
    }

    /// <summary>Ліміт версій на ОДИН шаблон у пакетній відповіді (`HandleBatchAsync`).</summary>
    /// <remarks>Той самий одноразовий ліміт, що клієнт раніше передавав окремо кожному запиту.</remarks>
    private const int PerTemplateVersionLimit = 100;

    /// <summary>
    /// Версії ДЕКІЛЬКОХ шаблонів ОДНИМ HTTP-зверненням (`BR-07`). Право <c>Template.View</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Уникає N+1 на РІВНІ HTTP-запитів клієнта: перелік шаблонів
    /// (`/admin/templates`) до цього бив по одному запиту версій на КОЖЕН
    /// рядок переліку (`TemplatesPage.tsx`, `useQueries`) — підтверджений
    /// 2026-09-25 пробіл продуктивності.
    ///
    /// ⚠ Порт (<see cref="ITemplateVersionStore"/>) свідомо БЕЗ нового методу:
    /// цикл нижче й далі робить по одному виклику <see cref="ITemplateVersionStore.
    /// ListVersionsAsync"/> НА ШАБЛОН — тобто на рівні SQL це й далі N запитів,
    /// просто в межах ОДНОГО HTTP-виклику, а не N. Справжній `WHERE TemplateId IN
    /// (...)` одним SQL-запитом вимагає нового методу порту й EF-реалізації
    /// (`Ecr.Infrastructure`) — свідомо залишено як борг (межа файлів задачі не
    /// охоплює <c>Ecr.Application/Ports</c> і <c>Ecr.Infrastructure</c>).
    ///
    /// ⚠ Виклики — ПОСЛІДОВНІ, не `Task.WhenAll`: <c>EcrDbContext</c> не є
    /// потокобезпечним для одночасних операцій у межах одного scoped-екземпляра,
    /// і паралельні виклики впали б винятком «A second operation was started on
    /// this context before a previous operation completed».
    ///
    /// ⛔ На відміну від <see cref="HandleAsync"/>, невідомий <paramref
    /// name="templateIds"/> НЕ дає `404`: пакетний запит адресує МНОЖИНУ
    /// шаблонів, і семантика «чого немає — те просто відсутнє в результаті»
    /// той самий патерн, що вже в <c>ListMethodologiesHandler</c>
    /// (`GET /api/v1/methodologies?ids=`, `RD-06`). Повтори в
    /// <paramref name="templateIds"/> звужуються до одного виклику на
    /// шаблон.
    /// </remarks>
    /// <param name="templateIds">Шаблони, чиї версії цікавлять.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<TemplateVersionsForTemplate>> HandleBatchAsync(
        IReadOnlyList<int> templateIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(templateIds);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, ListTemplatesHandler.Permission, ct)
            .ConfigureAwait(false);

        var result = new List<TemplateVersionsForTemplate>(templateIds.Count);
        var seen = new HashSet<int>();

        foreach (var templateId in templateIds)
        {
            if (!seen.Add(templateId))
            {
                continue;
            }

            var page = await templates
                .ListVersionsAsync(templateId, new CursorRequest(PerTemplateVersionLimit), ct)
                .ConfigureAwait(false);

            result.Add(new TemplateVersionsForTemplate(templateId, page.Items));
        }

        return result;
    }
}

/// <summary>Версії одного шаблону в межах пакетної відповіді (`ListTemplateVersionsHandler.HandleBatchAsync`, `BR-07`).</summary>
/// <param name="TemplateId">Шаблон, якому належать версії.</param>
/// <param name="Versions">Версії шаблону, у тому самому порядку, що й <see cref="ListTemplateVersionsHandler.HandleAsync"/>.</param>
public sealed record TemplateVersionsForTemplate(int TemplateId, IReadOnlyList<TemplateVersionSummary> Versions);
