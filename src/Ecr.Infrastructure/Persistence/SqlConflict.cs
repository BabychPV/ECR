// src/Ecr.Infrastructure/Persistence/SqlConflict.cs
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Виявляє конфлікт унікального ключа/індексу всередині <see cref="DbUpdateException"/>.
/// </summary>
/// <remarks>
/// ⛔ Спільний виніс із <c>RowStore.IsRowKeyConflict</c> (Q-245): та сама умова
/// стояла ОДНИМ примірником на весь клас конфлікту (гонитва двох одночасних
/// вставок на той самий унікальний ключ/код — TOCTOU без атомарності, Q-241),
/// а виявилося, що клас той самий і за межами <c>doc.TableRow</c> — роль
/// (<c>UQ_Role</c>), проєкт (<c>UQ_Project_Code</c>) і запис довідника
/// (<c>UQ_RegistryEntry</c>) падали ГОЛИМ <c>500</c> тим самим шляхом: другий
/// із двох одночасних запитів на той самий код проходить перевірку
/// «дублікат уже існує?», прочитану на початку обробки, і лише власна вставка
/// в БАЗУ ловить справжній дублікат — непіймано.
///
/// 2601 — «Cannot insert duplicate key row... with unique index»; 2627 —
/// «Violation of UNIQUE KEY constraint». SQL Server розрізняє їх залежно від
/// того, чи індекс сам є обмеженням (<c>CONSTRAINT</c>) — тут це не має
/// значення: обидва коди означають РІВНО те саме «дублікат ключа», і
/// залежність від внутрішньої деталі СУБД (яким саме шляхом вона оголосила
/// порушення) зробила б перевірку крихкою до версії сервера.
/// </remarks>
internal static class SqlConflict
{
    /// <summary>
    /// <c>true</c> — виняток стався через порушення унікального
    /// ключа/індексу, а не з іншої причини (обірваний зв'язок, тайм-аут,
    /// невідповідність типу даних тощо), яку краще лишити необробленим
    /// <c>500</c> із <c>CorrelationId</c>, ніж вигадати їй чисту відповідь.
    /// </summary>
    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
        => ex.InnerException is SqlException { Number: 2601 or 2627 };
}
