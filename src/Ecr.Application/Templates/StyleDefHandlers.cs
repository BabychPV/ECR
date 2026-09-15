// src/Ecr.Application/Templates/StyleDefHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Записує стиль версії-чернетки: заведення чи зміна оформлення колонки/
/// таблиці/рядка (директива `docs/build/directive-registry-lookup-and-cell-style.md`,
/// Частина B, PR B1).
/// </summary>
/// <remarks>
/// ⛔ B.0 директиви: CRUD-ендпоінта для <see cref="StyleDef"/> не було
/// взагалі — модель (`StyleDef.cs`) і споживач (`StyleMapper.cs`, Excel-
/// експорт) уже існували, бракувало точки входу, якою автор шаблону міг би
/// стиль ЗАВЕСТИ. Цей обробник — та точка входу.
///
/// ⚠ Upsert за кодом, той самий патерн, що <see cref="SaveColumnDefHandler"/>:
/// адміністративний екран не знає заздалегідь, чи стиль із цим кодом уже є —
/// перший `PUT` заводить, кожен наступний із тим самим кодом змінює.
///
/// ⛔ Аудит структурних змін (`IAuditWriter.WriteStructureChangeAsync`,
/// `ChangeClassifier`) НЕ підключено навмисно: стиль впливає лише на
/// ПОДАННЯ (`StyleMapper.cs`, `DocumentGrid.tsx` — жодне не читає значення
/// комірки), а не на самі дані чи їх структуру, тому цей запис — поза межами
/// того, що класифікатор і аудит структурних змін сьогодні розрізняють
/// (Breaking/Guarded відносно ІСНУЮЧИХ ДОКУМЕНТІВ — стиль документів не
/// зачіпає ніяк). Розширювати аудит на презентаційні поля — окреме рішення,
/// не мовчазний побічний ефект цього PR.
/// </remarks>
public sealed class SaveStyleDefHandler(
    IStyleCatalog styles,
    ITemplateVersionStore store,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії (`02-contracts.md` §9) — те саме, що на колонку.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює або змінює стиль версії.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код стилю.</param>
    /// <param name="command">Оформлення.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">Версія структурно заморожена — <c>ECR-TMPL-0409</c>.</exception>
    public async Task<StyleDefDto> HandleAsync(
        int templateVersionId, string code, SaveStyleDefCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⚠ `GetWithStructureAsync` — не тому, що стиль належить структурі
        // (`StyleDef.TemplateVersionId` — пряме посилання, не через дерево
        // аркушів/таблиць), а тому, що саме цей запит уже несе
        // `EnsureStructurallyMutable()` — ту саму перевірку заморожування,
        // яку інакше довелося б дублювати окремим SELECT.
        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        version.EnsureStructurallyMutable();

        var ecrCode = EcrCode.Create(code);
        var existing = await styles.FindByCodeAsync(templateVersionId, ecrCode.Value, ct).ConfigureAwait(false);

        if (existing is null)
        {
            existing = new StyleDef(templateVersionId, ecrCode);
            styles.AddDefinition(existing);
        }

        existing.SetAppearance(
            command.FontName, command.FontSize, command.IsBold, command.IsItalic,
            command.ForegroundArgb, command.BackgroundArgb, command.BorderJson,
            command.HorizontalAlign, command.VerticalAlign, command.WrapText, command.NumberFormat);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return Map(existing);
    }

    /// <summary>Складає DTO стилю для відповіді.</summary>
    internal static StyleDefDto Map(StyleDef style)
        => new(
            style.Id, style.Code, style.FontName, style.FontSize, style.IsBold, style.IsItalic,
            style.ForegroundArgb, style.BackgroundArgb, style.BorderJson,
            style.HorizontalAlign, style.VerticalAlign, style.WrapText, style.NumberFormat);
}

/// <summary>
/// Перелік стилів версії — для екрана конструктора шаблону (вибір наявного
/// стилю для повторного використання, а не лише заведення нового).
/// </summary>
public sealed class ListStyleDefsHandler(
    IStyleCatalog styles,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право читання структури — те саме, що на `GET …/structure`.</summary>
    public const string Permission = "Template.View";

    /// <summary>Усі стилі версії.</summary>
    public async Task<IReadOnlyList<StyleDefDto>> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var all = await styles.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        return [.. all.Values.OrderBy(s => s.Code, StringComparer.Ordinal).Select(SaveStyleDefHandler.Map)];
    }
}

/// <summary>Оформлення стилю, що приходить із форми (директива, Частина B).</summary>
/// <param name="FontName"><c>null</c>/порожнє — шрифт теми Excel-книги за замовчуванням.</param>
/// <param name="FontSize"><c>null</c> — розмір теми за замовчуванням.</param>
/// <param name="IsBold">Жирний.</param>
/// <param name="IsItalic">Курсив.</param>
/// <param name="ForegroundArgb">Колір тексту, ARGB; <c>null</c> — колір теми.</param>
/// <param name="BackgroundArgb">Колір заливки, ARGB; <c>null</c> — без заливки.</param>
/// <param name="BorderJson">
/// Межі за стороною: <c>{"top":1,"right":1,"bottom":2,"left":1}</c>, товщина
/// 0..3 (`StyleMapper.ApplyBorders`); <c>null</c>/порожнє — без рамки.
/// </param>
/// <param name="HorizontalAlign">0 Left, 1 Center, 2 Right, 3 Justify.</param>
/// <param name="VerticalAlign">0 Top, 1 Center, 2 Bottom.</param>
/// <param name="WrapText">Перенос тексту в комірці.</param>
/// <param name="NumberFormat">Формат числа Excel (напр. <c>0.00</c>); <c>null</c> — формат теми.</param>
public sealed record SaveStyleDefCommand(
    string? FontName,
    decimal? FontSize,
    bool IsBold,
    bool IsItalic,
    int? ForegroundArgb,
    int? BackgroundArgb,
    string? BorderJson,
    byte? HorizontalAlign,
    byte? VerticalAlign,
    bool WrapText,
    string? NumberFormat);

/// <summary>Стиль у відповіді на запис/перелік.</summary>
public sealed record StyleDefDto(
    int Id,
    string Code,
    string? FontName,
    decimal? FontSize,
    bool IsBold,
    bool IsItalic,
    int? ForegroundArgb,
    int? BackgroundArgb,
    string? BorderJson,
    byte? HorizontalAlign,
    byte? VerticalAlign,
    bool WrapText,
    string? NumberFormat);
