using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D14-08/R-01: зонд кроку 7 у режимі HTTPS (<c>Invoke-PinnedHttpsProbe</c>) приймає лише сертифікат із
/// заданим відбитком і читає код та тіло відповіді.
/// </summary>
/// <remarks>
/// ⛔ Предмет — справжня функція скрипта проти ЖИВОГО TLS-сервера (<see cref="SslStream"/> зі справжнім
/// самопідписаним сертифікатом на петлі): пришпилення й читання відповіді — саме те, що неможливо
/// перевірити на підмінених об'єктах. Скрипт-обгортка й вивід — лише ASCII (див. <see cref="DeployScriptHarness"/>);
/// кирилицю тіла перевіряє довжина в символах.
/// </remarks>
public sealed class DeployPinnedHttpsProbeTests
{
    private const string ResponseBody = "{\"s\":\"Д\"}";

    /// <remarks>
    /// Мутація (прогнано): у <c>Invoke-PinnedHttpsProbe</c> прибрати порівняння відбитка (завжди
    /// <c>CertificateMatches = $true</c>) → <c>p.bad.matches</c> червоний: зонд «довів би», що Kestrel віддає
    /// заданий сертифікат, коли він віддає чужий. Друга: читати тіло як ASCII → <c>p.ok.bodylen</c> червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public async Task Зонд_приймає_лише_сертифікат_із_заданим_відбитком_і_читає_відповідь()
    {
        using var certificate = NewCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // ⛔ Порт «нікого немає» ТРИМАЄТЬСЯ до кінця прогону: сокет прив'язаний, але не слухає, тож
        // з'єднання на нього — гарантована відмова. Колись порт брався й одразу відпускався, і в
        // паралельному прогоні його міг зайняти будь-який сусідній слухач.
        using var closed = ReserveClosedPort(out var closedPort);
        var serving = Serve(listener, certificate, connections: 2);

        var body = $$"""
            $ok = Invoke-PinnedHttpsProbe -Port {{port}} -Path '/health/ready' -Thumbprint '{{certificate.Thumbprint}}' -TimeoutSeconds 60
            "p.ok.status=$($ok.StatusCode)"
            "p.ok.matches=$($ok.CertificateMatches)"
            "p.ok.bodylen=$($ok.Body.Length)"
            $bad = Invoke-PinnedHttpsProbe -Port {{port}} -Path '/health/ready' -Thumbprint '{{new string('F', 40)}}' -TimeoutSeconds 60
            "p.bad.status=$($bad.StatusCode)"
            "p.bad.matches=$($bad.CertificateMatches)"
            "p.bad.hasbody=$([bool] $bad.Body)"
            $none = Invoke-PinnedHttpsProbe -Port {{closedPort}} -Path '/health/live' -Thumbprint '{{certificate.Thumbprint}}' -TimeoutSeconds 2
            "p.none.status=$($none.StatusCode)"
            "p.none.matches=$([string] $none.CertificateMatches)"
            """;

        var results = await Task.Run(() => DeployScriptHarness.Run(["Invoke-PinnedHttpsProbe"], body));
        await serving;

        // ⚠ Повідомлення несе весь вивід: без нього збій під навантаженням читався б лише як «очікувалось X».
        var dump = string.Join("; ", results.Select(p => $"{p.Key}={p.Value}"));
        void Expect(string key, string expected)
            => Assert.True(
                string.Equals(expected, DeployScriptHarness.Value(results, key), StringComparison.Ordinal),
                $"{key}: очікувалось «{expected}». Вивід: {dump}");

        Expect("p.ok.status", "503");
        Expect("p.ok.matches", "True");
        Expect("p.ok.bodylen", ResponseBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Expect("p.bad.matches", "False");
        Expect("p.bad.status", "0");
        Expect("p.bad.hasbody", "False");

        Expect("p.none.status", "0");
        Expect("p.none.matches", string.Empty);
    }

    /// <remarks>
    /// ⛔ Корінь флейку CL-1: сервер жив у <c>Task.Run</c> з асинхронними викликами, тобто кожен його
    /// крок (прийом, рукостискання, читання) чекав вільного потоку ПУЛУ процесу тестів. Сусідні
    /// тести цієї збірки блокують потоки пулу синхронно (<see cref="DeployScriptHarness.Run"/> чекає
    /// <c>pwsh</c> до 2 хв, той самий <c>Task.Run</c> тут), і під CPU ~100 % пул доростає повільно:
    /// рукостискання не починалося за <c>-TimeoutSeconds</c> зонда, і він чесно рапортував
    /// <c>StatusCode = 0</c> — збій тесту, а не скрипта. Тепер сервер — окремий потік
    /// (<see cref="TaskCreationOptions.LongRunning"/>) із синхронним вводом-виводом: від пулу він не
    /// залежить узагалі.
    /// </remarks>
    private static Task Serve(TcpListener listener, X509Certificate2 certificate, int connections) => Task.Factory.StartNew(
        () =>
        {
            for (var i = 0; i < connections; i++)
            {
                using var client = listener.AcceptTcpClient();
                try
                {
                    using var ssl = new SslStream(client.GetStream());
                    ssl.AuthenticateAsServer(certificate, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);

                    var seen = new StringBuilder();
                    var buffer = new byte[1024];
                    while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = ssl.Read(buffer);
                        if (read == 0)
                        {
                            break;
                        }

                        seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    if (seen.Length > 0)
                    {
                        var payload = Encoding.UTF8.GetBytes(ResponseBody);
                        var head = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 503 Service Unavailable\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                        ssl.Write(head);
                        ssl.Write(payload);
                        ssl.Flush();
                    }
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException or ObjectDisposedException)
                {
                    // Клієнт, що відхилив чужий сертифікат, обриває з'єднання — це очікувано.
                }
            }
        },
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    private static Socket ReserveClosedPort(out int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        return socket;
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        using var ephemeral = request.CreateSelfSigned(start, start.AddDays(30));

        // Через PFX: ефемерний ключ Windows не годиться для SslStream.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, "t"), "t");
    }
}
