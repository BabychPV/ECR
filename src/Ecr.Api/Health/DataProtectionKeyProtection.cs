namespace Ecr.Api.Health;

/// <summary>
/// Чим захищені ключі кільця DataProtection — для показу в <c>/health/db</c>.
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не читання конфігурації всередині перевірки. Причина не
/// стильова: <c>/health/db</c> має відповідати на питання «що застосунок
/// зробив», а не «що в нього написано в налаштуваннях». Ці два факти
/// розходяться рівно в найцікавішому випадку — відбиток заданий, сертифікат
/// узятий, — і перевірка, яка дивиться в конфігурацію, показала б «захищено»
/// навіть тоді, коли реєстрація впала б до незахищеного режиму.
///
/// ⚠ Значення створює <c>AuthenticationSetup.AddEcrDataProtection</c> — те
/// саме місце, де ухвалюється рішення. Іншого джерела цього факту немає.
/// </remarks>
/// <param name="IsProtected">Чи шифруються ключі сертифікатом.</param>
/// <param name="CertificateThumbprint">
/// Відбиток сертифіката, коли <paramref name="IsProtected"/> істинне.
/// </param>
public sealed record DataProtectionKeyProtection(bool IsProtected, string? CertificateThumbprint)
{
    /// <summary>Режим HTTP без сертифіката: ключі в таблиці відкрито.</summary>
    public static DataProtectionKeyProtection Unprotected { get; } = new(IsProtected: false, null);

    /// <summary>Ключі шифруються сертифікатом із <c>LocalMachine\My</c>.</summary>
    public static DataProtectionKeyProtection ProtectedBy(string thumbprint)
        => new(IsProtected: true, thumbprint);
}
