// src/Ecr.Application/Documents/DocumentVisibility.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>
/// Документ, якого користувач не бачить, для нього НЕ ІСНУЄ: відповідь та
/// сама, що й на неіснуючий, — <c>404 ECR-DOC-0404</c> (B-08, UX-прохід,
/// четвертий раунд).
/// </summary>
/// <remarks>
/// ⛔ Що відтворили аналітики: оператор без гранта отримував на
/// <c>GET /documents/9</c> — <c>404</c> (<c>GetDocumentHandler</c>), а на
/// <c>…/9/tables</c>, <c>…/header</c>, <c>…/validation</c> — <c>403</c>
/// «no access to document 9: NoGrant», тоді як неіснуючий документ давав
/// <c>404</c>. Різниця між двома відповідями сама є відомістю: за нею
/// перебором ідентифікаторів видно, які документи існують у чужих проєктах.
///
/// ⚠ Відповідь бере ТОЙ САМИЙ ключ, що й відсутній документ
/// (<c>AccessDecisionService.ProjectIdAsync</c>, <c>CreateRowHandler</c>,
/// <c>PatchCellsHandler</c>), — тобто тіло теж не відрізняється.
///
/// ⚠ Лише ВИДИМІСТЬ. Документ, який користувач бачить, але не може змінити
/// (грант <c>Read</c> без <c>Write</c>), і далі отримує <c>403</c> із причиною:
/// там приховувати нічого, а причина потрібна людині.
/// </remarks>
public static class DocumentVisibility
{
    /// <summary>Вимагає, щоб документ був видимий користувачеві.</summary>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — документа немає або він невидимий.</exception>
    public static async Task RequireVisibleAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw NotFound(documentId);
        }
    }

    /// <summary>
    /// Вимагає видимості документа І проєктного права в його проєкті
    /// (ФВ-6.14).
    /// </summary>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="permission">Проєктне право дії.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — документа немає або він невидимий.</exception>
    /// <exception cref="AccessDeniedException"><c>ECR-AUTH-0403</c> — права в проєкті документа немає.</exception>
    /// <remarks>
    /// ⚠ Видимий документ без права — <c>403</c>, як і було для права з
    /// ролі без області: приховувати тут нічого, документ людина бачить.
    /// Проєкт питається лише тоді, коли права немає глобально, — власник
    /// ролі без області не платить зайвим запитом.
    /// </remarks>
    public static async Task RequireVisibleAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId, string permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(profile);

        await RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        if (PermissionCheck.IsGranted(profile, permission))
        {
            return;
        }

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw NotFound(documentId);

        PermissionCheck.RequireIn(profile, permission, projectId);
    }

    /// <summary>
    /// Аркуш, якого читач не бачить, для нього НЕ ІСНУЄ: ТА САМА відповідь, що й на аркуш поза складом
    /// документа (<c>404 sheetNotInDocument</c>), — до будь-якої перевірки стану чи гранта. Інакше
    /// відмова «аркуш у стані Submitted» чи «рівень Read» розповідала б про аркуш, якого немає
    /// в читача (оракул стану).
    /// </summary>
    /// <param name="documents">Склад документа.</param>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш, над яким діють.</param>
    /// <param name="period">Період дії (межі ролі, звуженої періодами).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — аркуша немає в складі або він схований від читача.</exception>
    /// <remarks>
    /// ⚠ Читач без обмежень нижче проєкту не платить нічого: поведінка й кількість запитів — як були
    /// (<see cref="DocumentSheetVisibility.HasRestrictions"/>).
    /// </remarks>
    public static async Task RequireSheetVisibleAsync(
        IDocumentStore documents, IAccessDecisionService access, AccessProfile profile, long documentId,
        int sheetDefId, Ecr.Domain.ValueObjects.PeriodKey period, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(profile);

        if (!DocumentSheetVisibility.HasRestrictions(profile))
        {
            return;
        }

        if (!await documents.HasSheetAsync(documentId, sheetDefId, ct).ConfigureAwait(false)
            || !(await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false))
                .InPeriod(period).CanReadSheet(sheetDefId))
        {
            throw SheetNotInDocument(documentId, sheetDefId);
        }
    }

    /// <summary>
    /// Таблиця, якої читач не бачить (Deny на таблицю чи її аркуш), для нього НЕ ІСНУЄ: ТА САМА відповідь,
    /// що й на читання зрізу (<c>404 ECR-DOC-0404 tableInstance</c>), — до будь-якої відмови про режим
    /// рядків, версії чи ключі. Інакше 409 «Table "X" has RowMode = Fixed…» називав би схований ресурс (D-6).
    /// </summary>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="documentId">Документ екземпляра.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="tableDefId">Таблиця в структурі версії.</param>
    /// <param name="periodKey">Період екземпляра.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — таблиця схована від читача.</exception>
    /// <remarks>
    /// ⚠ Читач без обмежень нижче проєкту не платить нічого (<see cref="DocumentSheetVisibility.HasRestrictions"/>).
    /// </remarks>
    public static async Task RequireTableVisibleAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId, long tableInstanceId,
        int tableDefId, int periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(profile);

        if (!DocumentSheetVisibility.HasRestrictions(profile))
        {
            return;
        }

        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        var key = new Ecr.Domain.ValueObjects.PeriodKey(periodKey);

        if (!(key.IsValid ? scope.InPeriod(key) : scope).CanReadTable(tableDefId))
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Екземпляра таблиці {tableInstanceId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.tableInstance",
                    ["tableInstanceId"] = tableInstanceId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Відповідь «аркуша немає в складі документа» — однакова для відсутнього й схованого.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    public static NotFoundException SheetNotInDocument(long documentId, int sheetDefId)
        => new(
            "ECR-DOC-0404",
            $"Аркуша {sheetDefId} немає в складі документа {documentId}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0404.sheetNotInDocument",
                ["sheetDefId"] = sheetDefId.ToString(CultureInfo.InvariantCulture),
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>Відповідь «документа немає» — однакова для відсутнього й невидимого.</summary>
    /// <param name="documentId">Документ.</param>
    public static NotFoundException NotFound(long documentId)
        => new(
            ErrorCodes.DocumentNotFound,
            $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });
}
