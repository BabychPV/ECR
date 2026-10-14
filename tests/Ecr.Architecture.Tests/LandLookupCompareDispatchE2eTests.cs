// tests/Ecr.Architecture.Tests/LandLookupCompareDispatchE2eTests.cs
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LAND-5094: наскрізна перевірка, що <c>Build-LandTemplate.ps1 -Stage LookupCompare</c> ЧЕРЕЗ ДИСПЕТЧЕР скрипта реально
/// змінює вираз. Скрипт запускається цілком (без дот-сорсингу функції етапу) проти фальшивого loopback-API: якщо
/// диспетчер `if ($Stage -contains 'LookupCompare') { Stage-LookupCompare }` не виконується (потрапив у коментар),
/// `-Stage LookupCompare` проходить ValidateSet, але PUT не надсилається - тест червоніє. Значень прода тут немає.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у диспетчері між коментарем і `if` повернути літерал `\n` - обидва тести червоніють.
/// </remarks>
public sealed class LandLookupCompareDispatchE2eTests
{
    private const string No = "No - Нет";
    private const string Colum = "@Land_Measure_ReleaseColdVent";
    private const string Registry = "LAND_COLD2_VENTS_YES_NO";

    private static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Thread _thread;
        private readonly string _status;
        private string _expression = $"if ({Colum} = '{No}', 0, 1)";
        private string _inputJson =
            "{\"documentId\":1,\"periodKey\":\"2026-01\",\"arguments\":[{\"argumentCode\":\"Land_Measure_ReleaseColdVent\",\"value\":null,\"valueString\":\"" + No + "\",\"unitId\":null}]}";

        public List<(string Path, string Body)> Puts { get; } = [];
        public int Port { get; }
        public string Expression { get { lock (Puts) { return _expression; } } }

        public FakeApi(string versionStatus)
        {
            _status = versionStatus;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    return;
                }

                var path = ctx.Request.Url!.AbsolutePath;
                var method = ctx.Request.HttpMethod;
                string reqBody;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    reqBody = reader.ReadToEnd();
                }

                var status = 200;
                var body = "{}";
                lock (Puts)
                {
                    if (method == "PUT")
                    {
                        Puts.Add((path, reqBody));
                        using var doc = JsonDocument.Parse(reqBody);
                        if (path.Contains("/formulas/", StringComparison.Ordinal))
                        {
                            _expression = doc.RootElement.GetProperty("expression").GetString()!;
                        }
                        else if (path.EndsWith("/tests/PARITY_1", StringComparison.Ordinal))
                        {
                            _inputJson = doc.RootElement.GetProperty("inputJson").GetString()!;
                        }
                    }
                    else if (path == "/api/v1/registries")
                    {
                        body = $"[{{\"code\":\"{Registry}\",\"id\":5}}]";
                    }
                    else if (path == $"/api/v1/registries/{Registry}/entries")
                    {
                        body = "[{\"id\":190,\"code\":\"N\",\"display\":\"" + No + "\"},{\"id\":191,\"code\":\"Y\",\"display\":\"Yes - Да\"}]";
                    }
                    else if (path == "/api/v1/methodologies")
                    {
                        body = "[{\"code\":\"ECW_C12_03\",\"id\":7}]";
                    }
                    else if (path == "/api/v1/methodologies/7/versions")
                    {
                        body = $"[{{\"id\":70,\"status\":\"{_status}\"}}]";
                    }
                    else if (path == "/api/v1/methodologies/7/versions/70/formulas")
                    {
                        body = JsonSerializer.Serialize(new[]
                        {
                            new
                            {
                                code = "ECW_EC_tons_XXX",
                                expression = _expression,
                                argumentsCsv = "@Land_Measure_ReleaseColdVent;",
                                resultType = "Number",
                                outputUnitId = 5,
                                scope = (string?)null,
                            },
                        });
                    }
                    else if (path == "/api/v1/methodologies/7/versions/70/tests")
                    {
                        body = JsonSerializer.Serialize(new[]
                        {
                            new { code = "PARITY_1", inputJson = _inputJson, expectedJson = "{}", tolerance = "0.001" },
                        });
                    }
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Close();
            _thread.Join(2000);
        }
    }

    private static (int Exit, string Output) RunScript(FakeApi api, string accountsFile)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var url = $"http://localhost:{api.Port}";
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", BuildScript,
                     "-BaseUrl", url, "-Stage", "LookupCompare", "-OnlyMethodology", "ECW_C12_03",
                     "-ConfirmStand", $"localhost:{api.Port}/EcrLandFake",
                     "-SqlConnection", "Server=localhost;Database=EcrLandFake;Integrated Security=true;TrustServerCertificate=true",
                     "-AccountsFile", accountsFile,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell не завершився за 180 с.");
        }

        return (process.ExitCode, stdout + stderr);
    }

    private static string AccountsFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-lc-accounts-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "land-owner\ttest-only\tLandOwner\n", new UTF8Encoding(false));
        return file;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Диспетчер_скрипта_виконує_етап_і_переписує_вираз_чернетки_повтор_без_PUT()
    {
        var accounts = AccountsFile();
        using var api = new FakeApi("Draft");
        try
        {
            var (exit, output) = RunScript(api, accounts);
            Assert.True(exit == 0, output);
            Assert.Contains("== LookupCompare ==", output, StringComparison.Ordinal);

            var formula = Assert.Single(api.Puts, p => p.Path.Contains("/formulas/", StringComparison.Ordinal));
            Assert.EndsWith("/methodologies/7/versions/70/formulas/ECW_EC_tons_XXX", formula.Path, StringComparison.Ordinal);
            using var body = JsonDocument.Parse(formula.Body);
            Assert.Equal(
                $"if (REGFIELD({Colum}, 'NAME') = '{No}', 0, 1)", body.RootElement.GetProperty("expression").GetString());
            Assert.Single(api.Puts, p => p.Path.EndsWith("/tests/PARITY_1", StringComparison.Ordinal));

            // повтор на вже переписаній чернетці - 0 PUT
            var before = api.Puts.Count;
            var (exit2, output2) = RunScript(api, accounts);
            Assert.True(exit2 == 0, output2);
            Assert.Contains("== LookupCompare ==", output2, StringComparison.Ordinal);
            Assert.Equal(before, api.Puts.Count);
        }
        finally
        {
            File.Delete(accounts);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Диспетчер_скрипта_не_чіпає_опубліковану_версію()
    {
        var accounts = AccountsFile();
        using var api = new FakeApi("Published");
        try
        {
            var (exit, output) = RunScript(api, accounts);
            Assert.True(exit == 0, output);
            Assert.Contains("== LookupCompare ==", output, StringComparison.Ordinal);
            Assert.Empty(api.Puts);
            Assert.Equal($"if ({Colum} = '{No}', 0, 1)", api.Expression);
        }
        finally
        {
            File.Delete(accounts);
        }
    }
}
