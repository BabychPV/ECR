// src/Ecr.Application/Integration/CollectionFailure.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Integration;

/// <summary>
/// Відмова джерела в АВТЕНТИФІКАЦІЇ як окремий вид збою збору (<c>H-20</c>).
/// </summary>
/// <remarks>
/// ⛔ Це наш дефект, а не питання до контуру замовника. Недоступність джерела —
/// затримка: діапазон лишається непокритим, наздоганяння візьме його наступного
/// разу, і прогін чесно завершується «частково». <c>401</c>/<c>403</c> так себе
/// не поводить: облікові дані самі не полагодяться, наздоганяння стукатиме в
/// ті самі двері щоночі, а прогін щоразу рапортуватиме успіх із нулем рядків.
/// Систему, налаштовану неправильно, від справної відрізнити було б неможливо.
/// <para>
/// ⚠ Формулювання відмови живе ТУТ, а не в двох місцях. Пише його збирач
/// (<c>Ecr.Adapters.PiAf</c>), читає зведення обслуговування
/// (<c>Ecr.Infrastructure</c>); проєкти одне одного не бачать, і без спільного
/// місця «як виглядає відмова в автентифікації» існувало б двома
/// формулюваннями, які розійшлися б на першій правці — рівно так, як це вже
/// сталося з визначенням прогалини (<see cref="GapFinder"/>).
/// </para>
/// </remarks>
public static class CollectionFailure
{
    /// <summary>
    /// Незмінна частина тексту відмови — за нею зведення її і впізнає́.
    /// </summary>
    /// <remarks>
    /// ⚠ Ознака в ТЕКСТІ, а не окрема колонка, свідомо: <c>itg.CollectionRun</c>
    /// не має поля під вид відмови, і заводити його зміною схеми заради одного
    /// біта дорожче, ніж коштує сама ознака. Щойно таке поле з'явиться, обидва
    /// боки перейдуть на нього одночасно — бо обидва дивляться сюди.
    /// </remarks>
    public const string AuthenticationMarker = "відмовило в автентифікації";

    /// <summary>Статус прогону, який отримує відмова в автентифікації.</summary>
    /// <remarks>
    /// ⛔ Саме <c>Failed</c>, а не <c>Degraded</c>. <c>Degraded</c> означає
    /// «дані будуть, просто пізніше»; тут даних не буде ніколи, доки людина не
    /// втрутиться.
    /// </remarks>
    public const string FailedStatus = "Failed";

    /// <summary>Текст відмови для журналу прогону і для зведення.</summary>
    /// <param name="sourceCode">Код сутності або джерела — те, що шукатиме людина.</param>
    /// <returns>Рядок для <c>itg.CollectionRun.ErrorMessage</c>.</returns>
    /// <remarks>
    /// ⚠ Джерело названо ПОІМЕННО. «Збір завершився помилкою» у зведенні з
    /// двадцяти джерел не каже, куди йти; код каже.
    /// </remarks>
    public static string AuthenticationRefused(string sourceCode)
        => $"Джерело {sourceCode} {AuthenticationMarker}: повторний запит із тими самими "
           + "обліковими даними нічого не змінить, потрібне втручання адміністратора.";

    /// <summary>Ключ каталогу причини «джерело відмовило в автентифікації» (аудит U12).</summary>
    public const string AuthenticationRefusedKey = "jobs.collectionAuthRefused";

    /// <summary>
    /// Причина для <c>itg.CollectionRun.ErrorMessage</c> — конверт
    /// (<see cref="JobProgressMessageEnvelope"/>, <c>Q-326</c>), а не речення (U12).
    /// </summary>
    /// <param name="sourceCode">Код сутності джерела.</param>
    /// <param name="detail">Текст відмови від адаптера (дані джерела, як є); <c>null</c> — немає.</param>
    /// <returns>Закодований конверт.</returns>
    /// <remarks>
    /// ⚠ Журнал прогону читають мовою ЧИТАЧА (шухляда прогону, зведення), а мова
    /// в момент запису невідома. Сам виняток і негайний алерт і далі несуть
    /// <see cref="AuthenticationRefused"/> — текстом.
    /// </remarks>
    public static string AuthenticationRefusedReason(string sourceCode, string? detail)
        => JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            AuthenticationRefusedKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sourceCode"] = sourceCode,
                ["detail"] = detail ?? string.Empty,
            }));

    /// <summary>Чи це відмова в автентифікації.</summary>
    /// <param name="errorMessage">Текст із <c>itg.CollectionRun.ErrorMessage</c>.</param>
    /// <returns><c>true</c> — джерело відмовило в автентифікації.</returns>
    /// <remarks>
    /// ⛔ Два формати, і обидва живі: з U12 — конверт із ключем
    /// <see cref="AuthenticationRefusedKey"/>; ДО U12 — речення з
    /// <see cref="AuthenticationMarker"/>. Старі рядки лишаються в
    /// <c>itg.CollectionRun</c> і мусять і далі давати власний рядок зведення.
    /// </remarks>
    public static bool IsAuthenticationRefusal(string? errorMessage)
    {
        if (errorMessage is null)
        {
            return false;
        }

        if (JobProgressMessageCodec.TryDecode(errorMessage, out var envelope))
        {
            for (var level = envelope; level is not null; level = level.Inner)
            {
                if (string.Equals(level.Key, AuthenticationRefusedKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        return errorMessage.Contains(AuthenticationMarker, StringComparison.Ordinal);
    }

    /// <summary>Тема негайного алерта про відмову в автентифікації (<c>D-125</c>).</summary>
    /// <param name="sourceCode">Код сутності джерела.</param>
    /// <returns>Тема листа.</returns>
    public static string AlertSubject(string sourceCode)
        => $"ECR: джерело {sourceCode} {AuthenticationMarker}";

    /// <summary>Код події черги сповіщень для такої відмови.</summary>
    /// <remarks>
    /// Окремий код, а не спільний <c>maintenance.failures</c>: цю подію
    /// відправляють НЕГАЙНО, а не зі зведенням, і в черзі вона має бути
    /// відрізнена від решти хоча б для того, щоб її можна було порахувати.
    /// </remarks>
    public const string AlertEventCode = "collection.authentication-refused";
}
