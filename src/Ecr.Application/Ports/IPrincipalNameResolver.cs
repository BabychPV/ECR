namespace Ecr.Application.Ports;

/// <summary>
/// Переклад між іменем групи (<c>ДОМЕН\Група</c>, <c>МАШИНА\Група</c>,
/// <c>BUILTIN\…</c>) і її SID.
/// </summary>
/// <remarks>
/// ⚠ Обидва методи відповідають <c>null</c>, а не кидають: каталог може бути
/// недоступний, а на не-Windows перекладу немає взагалі. Зберігається завжди
/// SID, тож нерезолвлене ім'я — це порожня колонка на екрані, а не відмова.
/// </remarks>
public interface IPrincipalNameResolver
{
    /// <summary>SID за іменем облікового запису; <c>null</c> — не резолвиться.</summary>
    /// <param name="accountName">Ім'я у формі <c>ДОМЕН\Група</c>.</param>
    public string? ResolveSid(string accountName);

    /// <summary>Ім'я за SID; <c>null</c> — не резолвиться.</summary>
    /// <param name="sid">SID у формі <c>S-1-…</c>.</param>
    public string? ResolveName(string sid);
}
