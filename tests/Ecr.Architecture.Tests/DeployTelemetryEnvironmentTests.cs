using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ФВ-12.7 / НФ-8.6.2: <c>deploy-ecr.ps1 -TelemetryOtlpEndpoint</c> пише змінні <c>ECR_Telemetry__*</c>
/// (їх читає і Api, і дочірній <c>Ecr.Worker</c>); без параметра — нічого. Виконується справжня
/// функція <c>Resolve-TelemetryEnvironment</c> зі скрипта (<see cref="DeployScriptHarness"/>).
/// </summary>
/// <remarks>
/// Мутація (прогнано): у функції прибрати рядок <c>ECR_Telemetry__Enabled</c> → <c>telemetry.on.enabled</c> червоний;
/// дозволити схему не http(s) → <c>telemetry.ftp</c> червоний.
/// </remarks>
public sealed class DeployTelemetryEnvironmentTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Results = new(() => DeployScriptHarness.Run(
        ["Resolve-TelemetryEnvironment"],
        """
        function Show([string] $key, $d) {
            $keys = @($d.Set.Keys) -join ','
            "telemetry.$key.keys=$keys"
            foreach ($k in $d.Set.Keys) { "telemetry.$key.$k=$($d.Set[$k])" }
        }
        Show 'off' (Resolve-TelemetryEnvironment)
        Show 'on' (Resolve-TelemetryEnvironment -OtlpEndpoint 'http://otel.example:4317')
        Show 'proto' (Resolve-TelemetryEnvironment -OtlpEndpoint 'https://otel.example:4318' -OtlpProtocol 'HttpProtobuf')
        try { Resolve-TelemetryEnvironment -OtlpEndpoint 'ftp://otel.example' | Out-Null; 'telemetry.ftp=accepted' } catch { 'telemetry.ftp=thrown' }
        try { Resolve-TelemetryEnvironment -OtlpEndpoint 'otel.example:4317' | Out-Null; 'telemetry.relative=accepted' } catch { 'telemetry.relative=thrown' }
        try { Resolve-TelemetryEnvironment -OtlpProtocol 'Grpc' | Out-Null; 'telemetry.orphanProtocol=accepted' } catch { 'telemetry.orphanProtocol=thrown' }
        """));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("telemetry.off.keys", "")]
    [InlineData("telemetry.on.keys", "ECR_Telemetry__Enabled,ECR_Telemetry__OtlpEndpoint")]
    [InlineData("telemetry.on.ECR_Telemetry__Enabled", "true")]
    [InlineData("telemetry.on.ECR_Telemetry__OtlpEndpoint", "http://otel.example:4317/")]
    [InlineData("telemetry.proto.ECR_Telemetry__OtlpProtocol", "HttpProtobuf")]
    [InlineData("telemetry.ftp", "thrown")]
    [InlineData("telemetry.relative", "thrown")]
    [InlineData("telemetry.orphanProtocol", "thrown")]
    public void Змінні_телеметрії_пишуться_лише_за_явною_адресою(string key, string expected)
    {
        Assert.True(Results.Value.TryGetValue(key, out var value), $"ключа {key} немає у виводі:\n{string.Join('\n', Results.Value)}");
        Assert.Equal(expected, value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Змінні_пишуться_і_в_Api_і_у_воркер_після_ранньої_перевірки_адреси()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var early = script.IndexOf("$telemetryDecision = Resolve-TelemetryEnvironment", StringComparison.Ordinal);
        var msi = script.IndexOf("Start-Process msiexec", StringComparison.Ordinal);
        Assert.True(early > 0 && msi > early, "адресу телеметрії має бути перевірено до msiexec");
        Assert.Contains("@('EcrApi') + $(if ($workerEnabled) { @('EcrWorker') }", script, StringComparison.Ordinal);
    }
}
