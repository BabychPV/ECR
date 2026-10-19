// src/Ecr.Application/Permits/Bindings/PermitBindingQueryHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Складання DTO прив'язки - одне на читання й на збереження.</summary>
internal static class PermitBindingMapper
{
    /// <summary>Складає DTO.</summary>
    /// <param name="view">Прив'язка з кодами.</param>
    internal static PermitBindingDto Map(PermitBindingView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var binding = view.Binding;
        return new PermitBindingDto(
            binding.Id,
            binding.Code,
            binding.SheetTableDefId,
            view.SheetTableCode,
            view.PermitTypeCode,
            view.PermitTableCode,
            binding.Mode.ToString(),
            binding.SelectionRule.ToString(),
            binding.HeaderFieldDefId,
            view.HeaderFieldCode,
            binding.IsActive,
            Convert.ToBase64String(binding.RowVersion),
            [.. view.Columns.Select(c => new PermitBindingColumnDto(
                c.Role.ToString(),
                c.PermitFieldCode,
                c.SheetColumnDefId,
                view.SheetColumnCodes.GetValueOrDefault(
                    c.SheetColumnDefId, c.SheetColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                c.Ordinal))]);
    }
}

/// <summary>Прив'язки версії шаблону. Право <c>Template.View</c>.</summary>
/// <remarks>
/// ⚠ Порожня відповідь - норма: механізм опційний, і шаблон без прив'язок працює так само. Тому відповідь - конверт
/// із <c>isEditable</c>, а не голий масив.
/// </remarks>
public sealed class ListPermitBindingsHandler(
    IRepository<TemplateVersion, int> versions,
    IPermitBindingStore bindings,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання прив'язок (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Читає прив'язки версії.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Ecr.Application.Errors.NotFoundException">Версії немає.</exception>
    public async Task<PermitBindingsDto> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.VersionNotFound(templateVersionId);

        var items = await bindings.ListAsync(templateVersionId, ct).ConfigureAwait(false);

        return new PermitBindingsDto(!version.IsStructurallyFrozen, [.. items.Select(PermitBindingMapper.Map)]);
    }
}

/// <summary>Одна прив'язка версії за кодом. Право <c>Template.View</c>.</summary>
public sealed class GetPermitBindingHandler(
    IRepository<TemplateVersion, int> versions,
    IPermitBindingStore bindings,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання прив'язок (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Читає прив'язку.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Ecr.Application.Errors.NotFoundException">Версії чи прив'язки немає.</exception>
    public async Task<PermitBindingDto> HandleAsync(int templateVersionId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        _ = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.VersionNotFound(templateVersionId);

        var view = await bindings.GetAsync(templateVersionId, code, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.NotFound(code, templateVersionId);

        return PermitBindingMapper.Map(view);
    }
}
