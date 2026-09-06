using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Functions;

namespace Ecr.Application.Expressions;

/// <summary>
/// Усе, що редактор виразів мусить знати про мову, одним запитом
/// (<c>ФВ-9.15a</c>): склад функцій діалекту і символи, на які можна
/// послатися — <c>CST.</c>, <c>!</c>, <c>@</c>, <c>HDR.</c>.
/// </summary>
/// <remarks>
/// ⛔ Склад мови віддає СЕРВЕР, а не зашитий перелік у клієнті. Зашитий означав
/// би другу правду: додали функцію тут — редактор перестав би її знати, і
/// виглядало б це як помилка в тексті користувача, а не як застарілий клієнт.
///
/// ⚠ Один запит на всі чотири префікси, а не чотири. Автодоповнення викликають
/// на кожну крапку і кожен <c>@</c>; окремий похід на сервер за кожним
/// означав би, що перелік з'являється через мережу — тобто із затримкою, після
/// якої користувач уже дописав ім'я руками.
/// </remarks>
public sealed class GetExpressionMetadataHandler(
    IRepository<TemplateVersion, int> versions,
    IMethodologyStore methodologies,
    IUnitCatalog unitCatalog,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перелік символів мови (<c>02-contracts.md</c> §9).</summary>
    /// <remarks>
    /// ⛔ Як і перевірка виразу: <c>Calculation.View</c> як база, плюс
    /// <c>Template.View</c>, коли передано версію шаблону — перелік полів шапки
    /// є частиною структури.
    /// </remarks>
    public const string Permission = "Calculation.View";

    /// <summary>Повертає склад мови для діалекту й контексту.</summary>
    /// <param name="dialect">Діалект (<c>D-113</c>).</param>
    /// <param name="templateVersionId">Версія шаблону — джерело полів <c>HDR.</c>.</param>
    /// <param name="methodologyVersionId">Версія методології — джерело <c>CST.</c>, <c>!</c>, <c>@</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Функції діалекту і символи контексту.</returns>
    public async Task<ExpressionMetadataDto> HandleAsync(
        ExpressionDialect dialect,
        int? templateVersionId,
        int? methodologyVersionId,
        CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var registry = new FunctionRegistry();

        // ⚠ Порядок за іменем, а не за порядком оголошення: перелік читає
        // людина, яка шукає функцію, а не той, хто його писав.
        var functions = FunctionRegistry.Names(dialect)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => Function(name, registry))
            .ToList();

        var catalogue = await unitCatalog.GetAsync(ct).ConfigureAwait(false);
        var unitById = catalogue.Units.Values.ToDictionary(u => u.Id, u => u.Code);

        var headers = templateVersionId is { } versionId
            ? await HeadersAsync(versionId, ct).ConfigureAwait(false)
            : [];

        var symbols = methodologyVersionId is { } methodologyId
            ? await methodologies.GetSymbolsAsync(methodologyId, ct).ConfigureAwait(false)
            : new MethodologySymbols([], [], []);

        return new ExpressionMetadataDto(
            functions,
            [.. symbols.Constants.Select(s => Symbol(s, unitById))],
            [.. symbols.Formulas.Select(s => Symbol(s, unitById))],
            [.. symbols.Arguments.Select(s => Symbol(s, unitById))],
            headers);
    }

    /// <summary>Поля шапки документа — <c>HDR.</c> (<c>02b</c> §3.3 п. 4).</summary>
    /// <remarks>
    /// ⚠ Фільтр саме такий, як у вимозі: діловий ключ або поле області. Решта
    /// колонок шапкою не є, і підказати їх означало б навчити писати
    /// посилання, яке не резолвиться.
    /// </remarks>
    private async Task<IReadOnlyList<ExpressionSymbolDto>> HeadersAsync(
        int templateVersionId, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Template.View", ct)
            .ConfigureAwait(false);

        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        return
        [
            .. PublishChecks.Snapshot(version).ColumnsById.Values
                .Where(c => !c.IsDeleted && (c.IsBusinessKey || c.IsScopeField))
                .Select(c => c.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.Ordinal)
                .Take(MaxHeaders)
                .Select(code => new ExpressionSymbolDto(code, null, null)),
        ];
    }

    private static ExpressionFunctionDto Function(string name, FunctionRegistry registry)
    {
        var signature = registry.GetSignature(name);

        return new ExpressionFunctionDto(
            name,
            signature?.MinArgs ?? 0,
            signature?.MaxArgs,
            signature?.AcceptsRange ?? false,
            signature?.ResultType.ToString());
    }

    private static ExpressionSymbolDto Symbol(
        MethodologySymbol symbol, Dictionary<int, string> unitById)
        => new(
            symbol.Name,
            symbol.UnitId is { } id && unitById.TryGetValue(id, out var code) ? code : null,
            symbol.Note);

    /// <summary>Стеля полів шапки — межа запиту, не захист.</summary>
    private const int MaxHeaders = 1_000;
}

/// <summary>Склад мови виразів для діалекту й контексту.</summary>
/// <param name="Functions">Функції діалекту з сигнатурами.</param>
/// <param name="Constants">Константи методології — префікс <c>CST.</c>.</param>
/// <param name="Formulas">Формули тієї самої версії — префікс <c>!</c>.</param>
/// <param name="Arguments">Поля рядка джерела — префікс <c>@</c>.</param>
/// <param name="Headers">Поля шапки документа — префікс <c>HDR.</c>.</param>
public sealed record ExpressionMetadataDto(
    IReadOnlyList<ExpressionFunctionDto> Functions,
    IReadOnlyList<ExpressionSymbolDto> Constants,
    IReadOnlyList<ExpressionSymbolDto> Formulas,
    IReadOnlyList<ExpressionSymbolDto> Arguments,
    IReadOnlyList<ExpressionSymbolDto> Headers);

/// <summary>Функція діалекту та її сигнатура.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="MinArgs">Мінімум аргументів.</param>
/// <param name="MaxArgs">Максимум; <c>null</c> — необмежено (агрегати).</param>
/// <param name="AcceptsRange">Чи приймає діапазон рядків замість скалярів.</param>
/// <param name="ResultType">Тип результату; <c>null</c> — сигнатури немає.</param>
public sealed record ExpressionFunctionDto(
    string Name,
    int MinArgs,
    int? MaxArgs,
    bool AcceptsRange,
    string? ResultType);

/// <summary>Символ, на який може посилатися вираз.</summary>
/// <param name="Name">Ім'я без префікса.</param>
/// <param name="Unit">Позначення одиниці; <c>null</c> — безрозмірний.</param>
/// <param name="Note">Коротке пояснення: категорія, тип даних.</param>
public sealed record ExpressionSymbolDto(string Name, string? Unit, string? Note);
