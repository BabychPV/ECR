// src/Ecr.Application/Templates/TableDefHandlers.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Записує таблицю аркуша версії-чернетки: другий вертикальний зріз
/// авторства структури шаблону через API (<c>W5.1</c>), той самий патерн, що
/// й <see cref="SaveSheetDefHandler"/> (<c>W5.0</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього зрізу таблицю (як і аркуш) створював лише <c>Ecr.DataGen</c>
/// або тести. <c>SaveSheetDefHandler</c> — еталон, і ця пара повторює його
/// форму буквально: та сама адресація за кодом, та сама послідовність
/// перевірок, та сама явна інвалідація кешу метаданих.
///
/// ⚠ <c>PUT</c> за кодом, а не <c>POST</c> — з тієї самої причини, що й у
/// <see cref="SaveSheetDefHandler"/> (<c>D2-147</c>): адресу задає викликач,
/// створення й зміна — одна ідемпотентна дія.
///
/// ⛔ Таблиця адресується у двох рівнях: <c>{sheetCode}/tables/{code}</c>, а
/// не голим <c>{code}</c> версії. Код таблиці унікальний лише В МЕЖАХ аркуша
/// (<see cref="SheetDef.AddTable"/> перевіряє дублікат саме там), тож без коду
/// аркуша в адресі дві таблиці з однаковим кодом на різних аркушах не можна
/// було б розрізнити маршрутом — довелося б перебирати всю версію, шукаючи,
/// якому аркушу належить кожен збіг.
///
/// ⛔ Читає й пише через <see cref="ITemplateVersionStore.GetWithStructureAsync"/>
/// і явно скидає <see cref="IMetadataCache"/> після запису — той самий доказ,
/// що й у <see cref="SaveSheetDefHandler"/>: кеш ключується презентаційною
/// ревізією, яку структурний запис чернетки не піднімає, тож без
/// <see cref="IMetadataCache.InvalidateAsync"/> прогрітий <c>GET
/// …/structure</c> віддав би знімок без щойно доданої чи зміненої таблиці.
/// </remarks>
public sealed class SaveTableDefHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює таблицю на аркуші.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="sheetCode">Код аркуша, якому належить таблиця.</param>
    /// <param name="code">Код таблиці.</param>
    /// <param name="command">Налаштування таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Збережена таблиця.</returns>
    /// <exception cref="NotFoundException">Версії або аркуша немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Версія структурно заморожена (<c>ECR-TMPL-0409</c>).
    /// </exception>
    public async Task<TableDto> HandleAsync(
        int templateVersionId, string sheetCode, string code, SaveTableDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        // ⛔ Той самий відстежуваний граф, що й у `SaveSheetDefHandler`: порожній
        // `IRepository.FindAsync` без `Include` віддав би `Sheets` (і, отже,
        // `Tables`) порожніми, і перевірка коду на дублікат не побачила б
        // жодної наявної таблиці.
        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⛔ ПЕРШИМ ділом і доменом — так само, як у `SaveSheetDefHandler`.
        version.EnsureStructurallyMutable();

        var sheet = version.Sheets.FirstOrDefault(
            s => string.Equals(s.Code, sheetCode, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Аркуша «{sheetCode}» у версії {templateVersionId} немає.");

        var ecrCode = EcrCode.Create(code);
        var name = new LocalizedText(new Dictionary<string, string>(command.NameL10n, StringComparer.OrdinalIgnoreCase));

        var existing = sheet.Tables.FirstOrDefault(
            t => string.Equals(t.Code, ecrCode.Value, StringComparison.Ordinal));

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Класифікація йде і для СТВОРЕННЯ теж — той самий довід, що й у
        // `SaveSheetDefHandler`: «додано» без класу виглядало б в аудиті так
        // само, як зміна, яку ніхто не класифікував.
        //
        // ⚠ Представницьке поле для класифікації ЗМІНИ — `RowMode`, за тим
        // самим принципом, що й `IsMandatory` у `SaveSheetDefHandler`: жодне
        // з полів команди не є ідентичністю чи презентацією (`Code` у тілі
        // взагалі відсутній — його задає URL), тож обране поле однаково
        // потрапляє у «решту» `ChangeClassifier.Classify` і класифікує зміну
        // ЦІЛОЮ командою, а не одним полем.
        var change = existing is null
            ? classifier.ClassifyAddition(nameof(TableDef))
            : classifier.Classify(nameof(TableDef), nameof(TableDef.RowMode), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Зміна");

        var oldJson = existing is null ? null : Describe(existing);

        // ⛔ Q-244: «запис → аудит → SaveChanges» — одним замиканням
        // `IUnitOfWork.ExecuteInTransactionAsync`, коміт рівно один,
        // наприкінці (той самий клас дефекту, що Q-243).
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            if (existing is null)
            {
                // ⚠ Ordinal, якщо не переданий явно, — за наявними таблицями ЦЬОГО
                // аркуша: нова таблиця стає останньою в порядку показу саме на
                // ньому, а не в межах усієї версії (та сама логіка, що й
                // `SaveSheetDefHandler` для аркушів версії).
                var ordinal = command.Ordinal
                    ?? (sheet.Tables.Count == 0 ? 0 : sheet.Tables.Max(t => t.Ordinal) + 1);

                existing = new TableDef(sheet.Id, ecrCode, name, ordinal, command.LayoutKind, command.RowMode);
                existing.SetMaxDynamicRows(command.MaxDynamicRows);

                sheet.AddTable(existing);

                // ⛔ Запис ПЕРЕД аудитом, і лише для створення — той самий довід,
                // що й у `SaveSheetDefHandler`: аудит несе `EntityId`, якого у
                // щойно доданої сутності ще немає до `SaveChanges`.
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            else
            {
                existing.Rename(name);
                existing.SetLayout(command.LayoutKind);

                // ⚠ Порядок значущий: `RowMode` міняється ПЕРЕД `MaxDynamicRows`,
                // бо саме від фінального `RowMode` залежить, чи взагалі дозволена
                // стеля (`SetMaxDynamicRows` кидає `ECR-TMPL-0422`, якщо ні). Якби
                // виклики стояли навпаки, перевірка бачила б ще СТАРИЙ режим.
                existing.SetRowMode(command.RowMode);
                existing.SetMaxDynamicRows(command.MaxDynamicRows);

                if (command.Ordinal is { } ordinal)
                {
                    existing.Reorder(ordinal);
                }
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(TableDef), existing.Id,
                    change, oldJson is null ? "Create" : "Update",
                    oldJson, Describe(existing), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ Див. коментар класу: без цього виклику `GET …/structure`,
        // прочитаний хоч раз до цієї правки, віддавав би знімок без щойно
        // доданої чи зміненої таблиці, доки версію не опублікують.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ `existing` завжди присвоєно всередині щойно завершеного замикання
        // — той самий довід, що в `SaveColumnDefHandler`.
        return Map(existing!);
    }

    /// <summary>Складає DTO таблиці для відповіді.</summary>
    /// <remarks>
    /// ⚠ <c>Columns</c> і <c>Rows</c> — порожні переліки. Цей ендпоінт керує
    /// лише таблицею; колонки й рядки на ній заводять наступні зрізи
    /// (<c>ColumnDef</c>, <c>RowDef</c>), і показувати тут застарілий склад
    /// означало б тримати другий шлях їх читання поруч із <c>GET
    /// …/structure</c> — той самий довід, що й у
    /// <see cref="SaveSheetDefHandler.Map"/> для <c>Tables</c> аркуша.
    /// </remarks>
    internal static TableDto Map(TableDef table)
        => new(
            table.Id, table.Code, table.NameL10n, table.Ordinal,
            table.LayoutKind, table.RowMode, table.MaxDynamicRows,
            [], []);

    /// <summary>Стан таблиці для аудиту.</summary>
    /// <param name="table">Таблиця.</param>
    /// <returns>JSON із полями, які взагалі можуть змінитися.</returns>
    internal static string Describe(TableDef table)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            table.Code,
            NameL10n = table.NameL10n.Values,
            table.Ordinal,
            table.LayoutKind,
            table.RowMode,
            table.MaxDynamicRows,
        });
}

/// <summary>Налаштування таблиці, що приходять із форми.</summary>
/// <param name="NameL10n">Назва таблиці мовами каталогу.</param>
/// <param name="Ordinal"><c>null</c> — нова таблиця стає останньою на аркуші за порядком.</param>
/// <param name="LayoutKind">Розкладка: як періоди лягають на структуру.</param>
/// <param name="RowMode">Спосіб формування рядків.</param>
/// <param name="MaxDynamicRows">Стеля кількості рядків, якщо таблиця приймає додані користувачем; <c>null</c> — без стелі.</param>
public sealed record SaveTableDefCommand(
    IReadOnlyDictionary<string, string> NameL10n,
    int? Ordinal,
    TableLayoutKind LayoutKind,
    TableRowMode RowMode,
    int? MaxDynamicRows);

/// <summary>
/// Прибирає таблицю з аркуша версії-чернетки — м'яко (<c>ФВ-7.6</c>), той
/// самий патерн, що й <see cref="DeleteSheetDefHandler"/>.
/// </summary>
/// <remarks>
/// ⛔ Таблиця не зникає фізично: на неї можуть посилатися формули, правила
/// валідації й зв'язки між таблицями (<c>ФВ-2.12</c>) навіть у чернетці.
/// <see cref="TableDef.SoftDelete"/> лишає запис, щоб такі посилання
/// лишалися видимими, а не провалювалися у порожнечу.
/// </remarks>
public sealed class DeleteTableDefHandler(
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Видаляє таблицю.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="sheetCode">Код аркуша, якому належить таблиця.</param>
    /// <param name="code">Код таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії, аркуша або таблиці немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена.</exception>
    public async Task HandleAsync(int templateVersionId, string sheetCode, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(ErrorCodes.Unauthorized, "Сесія не містить користувача.");

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);

        version.EnsureStructurallyMutable();

        var sheet = version.Sheets.FirstOrDefault(
            s => string.Equals(s.Code, sheetCode, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Аркуша «{sheetCode}» у версії {templateVersionId} немає.");

        var table = sheet.Tables.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Таблиці «{code}» на аркуші «{sheetCode}» немає.");

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(TableDef), hasDocuments);

        SaveTableRelationHandler.RejectBreaking(change, code, hasDocuments, "Видалення");

        // ⛔ Q-244: аудит і `SoftDelete`/`SaveChanges` тепер одна транзакція.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(TableDef), table.Id,
                    change, "Delete",
                    SaveTableDefHandler.Describe(table), NewJson: null, ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            table.SoftDelete(userId, clock.UtcNow);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
