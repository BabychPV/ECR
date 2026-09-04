# 05a — Скелет: рішення, проєкти, конфігурація

> Частина [`05-skeleton.md`](05-skeleton.md). Формат опису файлу — там же, §2.
> Усі версії пакетів — гіпотеза (`04-environment.md`); розбіжність виправляється
> як `BOOTSTRAP-FIX`.

---

### `global.json`
MODULE: solution | STAGE: 0
SCOPE: фіксує мажорну лінію SDK, щоб збірка не «поїхала» на іншій машині.
NOT IN SCOPE: точна patch-версія — `rollForward` дозволяє свіжішу.

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

---

### `Directory.Build.props`
MODULE: solution | STAGE: 0
SCOPE: наскрізні налаштування компіляції для всіх проєктів.
NOT IN SCOPE: посилання на пакети — вони в `Directory.Packages.props`.

```xml
<Project>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- Попередження = помилки. Дисципліна дешевша за розбір «чому воно так» через рік. -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors></WarningsNotAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>

    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <!-- CS1591: не вимагаємо XML-doc на кожному члені в тестах і tools -->
    <NoWarn>$(NoWarn);CS1591</NoWarn>

    <InvariantGlobalization>true</InvariantGlobalization>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <Optimize>true</Optimize>
    <DebugType>portable</DebugType>
  </PropertyGroup>

</Project>
```

---

### `Directory.Packages.props`
MODULE: solution | STAGE: 0
SCOPE: центральне керування версіями пакетів. Версія оголошується **тут і
тільки тут**; у `.csproj` — лише `<PackageReference Include="..." />` без `Version`.
NOT IN SCOPE: додавання нових пакетів без запису в `04-environment.md` (`D-12`).

```xml
<Project>

  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>

  <ItemGroup Label="EF Core і БД">
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Data.SqlClient" Version="6.0.1" />
  </ItemGroup>

  <ItemGroup Label="Хостинг, DI, кеш">
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Caching.Memory" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Caching.SqlServer" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup Label="ASP.NET Core">
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.0" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.Negotiate" Version="10.0.0" />
    <PackageVersion Include="Microsoft.AspNetCore.Diagnostics.HealthChecks" Version="10.0.0" />
    <PackageVersion Include="Scalar.AspNetCore" Version="2.0.0" />
  </ItemGroup>

  <ItemGroup Label="Домен і обчислення">
    <!-- NCalc — ОБЧИСЛЮВАЧ, не парсер нашої мови (D-19). Парсер власний. -->
    <PackageVersion Include="NCalcSync" Version="5.4.0" />
    <PackageVersion Include="ClosedXML" Version="0.104.2" />
    <!--
      ⛔ Microsoft.CodeAnalysis.CSharp.Scripting НЕ підключений (D-105, D-113).
      Рівень 2 (виконання C# на сервері) у першому релізі не будується, а
      залежність під нереалізовану функцію суперечить D-12. Додається одним
      рядком, коли рівень 2 буде ухвалено разом із дозволом ІБ (K-1).
    -->
  </ItemGroup>

  <ItemGroup Label="Фонові задачі — Apache-2.0, без ліцензійного питання до ІБ">
    <PackageVersion Include="Quartz" Version="3.13.1" />
    <PackageVersion Include="Quartz.Extensions.Hosting" Version="3.13.1" />
    <PackageVersion Include="Quartz.Serialization.Json" Version="3.13.1" />
  </ItemGroup>

  <ItemGroup Label="Тести">
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="Testcontainers.MsSql" Version="4.0.0" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageVersion Include="coverlet.collector" Version="6.0.2" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
  </ItemGroup>

  <!--
    ⛔ ЗАБОРОНЕНІ ПАКЕТИ (D-12, docs/15-verified-stack.md).
    Додавання будь-якого з них ламає збірку через тест ліцензій:
      FluentAssertions 8+  — комерційна ліцензія Xceed
      EFCore.BulkExtensions — платна для комерційного використання
      EPPlus 5+            — noncommercial
      HyperFormula         — GPLv3
      NBomber 5+           — комерційна
      будь-який Redis-сервер — AGPL/SSPL
  -->

</Project>
```

---

### `Ecr.sln`
MODULE: solution | STAGE: 0
SCOPE: склад рішення.
NOT IN SCOPE: `Ecr.Web` — це npm-проєкт, у `.sln` він не входить.

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.11.35222.181
MinimumVisualStudioVersion = 10.0.40219.1
Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{11111111-1111-1111-1111-111111111111}"
EndProject
Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "tests", "tests", "{22222222-2222-2222-2222-222222222222}"
EndProject
Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "tools", "tools", "{33333333-3333-3333-3333-333333333333}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Domain", "src\Ecr.Domain\Ecr.Domain.csproj", "{A0000001-0000-0000-0000-000000000001}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Application", "src\Ecr.Application\Ecr.Application.csproj", "{A0000002-0000-0000-0000-000000000002}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Expressions", "src\Ecr.Expressions\Ecr.Expressions.csproj", "{A0000003-0000-0000-0000-000000000003}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Infrastructure", "src\Ecr.Infrastructure\Ecr.Infrastructure.csproj", "{A0000004-0000-0000-0000-000000000004}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Calculations", "src\Ecr.Calculations\Ecr.Calculations.csproj", "{A0000005-0000-0000-0000-000000000005}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Adapters.Excel", "src\Ecr.Adapters.Excel\Ecr.Adapters.Excel.csproj", "{A0000006-0000-0000-0000-000000000006}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Adapters.PiAf", "src\Ecr.Adapters.PiAf\Ecr.Adapters.PiAf.csproj", "{A0000007-0000-0000-0000-000000000007}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Api", "src\Ecr.Api\Ecr.Api.csproj", "{A0000008-0000-0000-0000-000000000008}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.TestKit", "tests\Ecr.TestKit\Ecr.TestKit.csproj", "{B0000000-0000-0000-0000-000000000000}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Domain.Tests", "tests\Ecr.Domain.Tests\Ecr.Domain.Tests.csproj", "{B0000001-0000-0000-0000-000000000001}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Application.Tests", "tests\Ecr.Application.Tests\Ecr.Application.Tests.csproj", "{B0000002-0000-0000-0000-000000000002}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Expressions.Tests", "tests\Ecr.Expressions.Tests\Ecr.Expressions.Tests.csproj", "{B0000003-0000-0000-0000-000000000003}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Calculations.Tests", "tests\Ecr.Calculations.Tests\Ecr.Calculations.Tests.csproj", "{B0000004-0000-0000-0000-000000000004}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Infrastructure.Tests", "tests\Ecr.Infrastructure.Tests\Ecr.Infrastructure.Tests.csproj", "{B0000005-0000-0000-0000-000000000005}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Api.Tests", "tests\Ecr.Api.Tests\Ecr.Api.Tests.csproj", "{B0000006-0000-0000-0000-000000000006}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Architecture.Tests", "tests\Ecr.Architecture.Tests\Ecr.Architecture.Tests.csproj", "{B0000007-0000-0000-0000-000000000007}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Bootstrap.Excel", "tools\Ecr.Bootstrap.Excel\Ecr.Bootstrap.Excel.csproj", "{C0000001-0000-0000-0000-000000000001}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.DataGen", "tools\Ecr.DataGen\Ecr.DataGen.csproj", "{C0000002-0000-0000-0000-000000000002}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Ecr.Migration.PiAf", "tools\Ecr.Migration.PiAf\Ecr.Migration.PiAf.csproj", "{C0000003-0000-0000-0000-000000000003}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{A0000001-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000001-0000-0000-0000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000001-0000-0000-0000-000000000001}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000001-0000-0000-0000-000000000001}.Release|Any CPU.Build.0 = Release|Any CPU
		{A0000002-0000-0000-0000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000002-0000-0000-0000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000002-0000-0000-0000-000000000002}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000002-0000-0000-0000-000000000002}.Release|Any CPU.Build.0 = Release|Any CPU
		{A0000003-0000-0000-0000-000000000003}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000003-0000-0000-0000-000000000003}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000003-0000-0000-0000-000000000003}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000003-0000-0000-0000-000000000003}.Release|Any CPU.Build.0 = Release|Any CPU
		{A0000004-0000-0000-0000-000000000004}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000004-0000-0000-0000-000000000004}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000004-0000-0000-0000-000000000004}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000004-0000-0000-0000-000000000004}.Release|Any CPU.Build.0 = Release|Any CPU
		{A0000005-0000-0000-0000-000000000005}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000005-0000-0000-0000-000000000005}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000005-0000-0000-0000-000000000005}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000005-0000-0000-0000-000000000005}.Release|Any CPU.Build.0 = Release|Any CPU
		{A0000006-0000-0000-0000-000000000006}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000006-0000-0000-0000-000000000006}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000006-0000-0000-0000-000000000006}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000006-0000-0000-0000-000000000006}.Release|Any CPU.Build.0 = Release|Any CPU
		{A0000007-0000-0000-0000-000000000007}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000007-0000-0000-0000-000000000007}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000007-0000-0000-0000-000000000007}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000007-0000-0000-0000-000000000007}.Release|Any CPU.Build.0 = Release|Any CPU
		{A0000008-0000-0000-0000-000000000008}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{A0000008-0000-0000-0000-000000000008}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{A0000008-0000-0000-0000-000000000008}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{A0000008-0000-0000-0000-000000000008}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000000-0000-0000-0000-000000000000}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000000-0000-0000-0000-000000000000}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000000-0000-0000-0000-000000000000}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000000-0000-0000-0000-000000000000}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000001-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000001-0000-0000-0000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000001-0000-0000-0000-000000000001}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000001-0000-0000-0000-000000000001}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000002-0000-0000-0000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000002-0000-0000-0000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000002-0000-0000-0000-000000000002}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000002-0000-0000-0000-000000000002}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000003-0000-0000-0000-000000000003}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000003-0000-0000-0000-000000000003}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000003-0000-0000-0000-000000000003}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000003-0000-0000-0000-000000000003}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000004-0000-0000-0000-000000000004}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000004-0000-0000-0000-000000000004}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000004-0000-0000-0000-000000000004}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000004-0000-0000-0000-000000000004}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000005-0000-0000-0000-000000000005}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000005-0000-0000-0000-000000000005}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000005-0000-0000-0000-000000000005}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000005-0000-0000-0000-000000000005}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000006-0000-0000-0000-000000000006}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000006-0000-0000-0000-000000000006}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000006-0000-0000-0000-000000000006}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000006-0000-0000-0000-000000000006}.Release|Any CPU.Build.0 = Release|Any CPU
		{B0000007-0000-0000-0000-000000000007}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{B0000007-0000-0000-0000-000000000007}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{B0000007-0000-0000-0000-000000000007}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{B0000007-0000-0000-0000-000000000007}.Release|Any CPU.Build.0 = Release|Any CPU
		{C0000001-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{C0000001-0000-0000-0000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{C0000001-0000-0000-0000-000000000001}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{C0000001-0000-0000-0000-000000000001}.Release|Any CPU.Build.0 = Release|Any CPU
		{C0000002-0000-0000-0000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{C0000002-0000-0000-0000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{C0000002-0000-0000-0000-000000000002}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{C0000002-0000-0000-0000-000000000002}.Release|Any CPU.Build.0 = Release|Any CPU
		{C0000003-0000-0000-0000-000000000003}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{C0000003-0000-0000-0000-000000000003}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{C0000003-0000-0000-0000-000000000003}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{C0000003-0000-0000-0000-000000000003}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(NestedProjects) = preSolution
		{A0000001-0000-0000-0000-000000000001} = {11111111-1111-1111-1111-111111111111}
		{A0000002-0000-0000-0000-000000000002} = {11111111-1111-1111-1111-111111111111}
		{A0000003-0000-0000-0000-000000000003} = {11111111-1111-1111-1111-111111111111}
		{A0000004-0000-0000-0000-000000000004} = {11111111-1111-1111-1111-111111111111}
		{A0000005-0000-0000-0000-000000000005} = {11111111-1111-1111-1111-111111111111}
		{A0000006-0000-0000-0000-000000000006} = {11111111-1111-1111-1111-111111111111}
		{A0000007-0000-0000-0000-000000000007} = {11111111-1111-1111-1111-111111111111}
		{A0000008-0000-0000-0000-000000000008} = {11111111-1111-1111-1111-111111111111}
		{B0000000-0000-0000-0000-000000000000} = {22222222-2222-2222-2222-222222222222}
		{B0000001-0000-0000-0000-000000000001} = {22222222-2222-2222-2222-222222222222}
		{B0000002-0000-0000-0000-000000000002} = {22222222-2222-2222-2222-222222222222}
		{B0000003-0000-0000-0000-000000000003} = {22222222-2222-2222-2222-222222222222}
		{B0000004-0000-0000-0000-000000000004} = {22222222-2222-2222-2222-222222222222}
		{B0000005-0000-0000-0000-000000000005} = {22222222-2222-2222-2222-222222222222}
		{B0000006-0000-0000-0000-000000000006} = {22222222-2222-2222-2222-222222222222}
		{B0000007-0000-0000-0000-000000000007} = {22222222-2222-2222-2222-222222222222}
		{C0000001-0000-0000-0000-000000000001} = {33333333-3333-3333-3333-333333333333}
		{C0000002-0000-0000-0000-000000000002} = {33333333-3333-3333-3333-333333333333}
		{C0000003-0000-0000-0000-000000000003} = {33333333-3333-3333-3333-333333333333}
	EndGlobalSection
EndGlobal
```

---

### `src/Ecr.Domain/Ecr.Domain.csproj`
MODULE: domain | STAGE: 0
SCOPE: доменний шар. **Жодних зовнішніх залежностей** — це перевіряється
архітектурним тестом і є головною межею всієї системи.
NOT IN SCOPE: EF Core, ASP.NET, будь-який пакет, крім BCL.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Domain</RootNamespace>
  </PropertyGroup>

  <!--
    ⛔ ItemGroup із PackageReference тут не повинно з'явитися НІКОЛИ.
    Домен не знає ані про EF Core, ані про ASP.NET, ані про PI AF.
    Порушення ловить Ecr.Architecture.Tests.
  -->

</Project>
```

---

### `src/Ecr.Application/Ecr.Application.csproj`
MODULE: application | STAGE: 0
SCOPE: use-cases і порти.
NOT IN SCOPE: EF Core, `Microsoft.Data.SqlClient`, будь-який адаптер.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Application</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ecr.Domain\Ecr.Domain.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Лише абстракції: реалізації живуть в Infrastructure -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" />
  </ItemGroup>

</Project>
```

---

### `src/Ecr.Expressions/Ecr.Expressions.csproj`
MODULE: expressions | STAGE: 0
SCOPE: власний лексер, парсер, граф залежностей; NCalc — лише як обчислювач
арифметики після резолвінгу посилань.
NOT IN SCOPE: доступ до БД, знання про документи.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Expressions</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ecr.Domain\Ecr.Domain.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="NCalcSync" />
  </ItemGroup>

</Project>
```

---

### `src/Ecr.Infrastructure/Ecr.Infrastructure.csproj`
MODULE: infrastructure | STAGE: 0
SCOPE: EF Core, репозиторії, кеш, планувальник, SQL-скрипти.
NOT IN SCOPE: бізнес-правила — вони в `Ecr.Domain`/`Ecr.Application`.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Infrastructure</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\Ecr.Application\Ecr.Application.csproj" />
    <ProjectReference Include="..\Ecr.Expressions\Ecr.Expressions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="Microsoft.Extensions.Caching.SqlServer" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Quartz" />
    <PackageReference Include="Quartz.Extensions.Hosting" />
    <PackageReference Include="Quartz.Serialization.Json" />
  </ItemGroup>

  <ItemGroup>
    <!-- SQL-скрипти йдуть у вихід: їх виконує SQL Agent, а не застосунок (D-66) -->
    <None Update="Persistence\Sql\*.sql" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

---

### `src/Ecr.Calculations/Ecr.Calculations.csproj`
MODULE: calculations | STAGE: 0
SCOPE: рушій розрахунків, generic-модуль рівня 1.
NOT IN SCOPE: скрипти рівня 2 — вони за окремим рішенням ІБ (`K-1`).

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Calculations</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\Ecr.Application\Ecr.Application.csproj" />
    <ProjectReference Include="..\Ecr.Expressions\Ecr.Expressions.csproj" />
  </ItemGroup>

  <!--
    Microsoft.CodeAnalysis.CSharp.Scripting свідомо НЕ підключений:
    рівень 2 драбини вмикається лише після дозволу ІБ (K-1). Пакет
    додається разом із реалізацією, не наперед.
  -->

</Project>
```

---

### `src/Ecr.Adapters.Excel/Ecr.Adapters.Excel.csproj`
MODULE: adapters | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Adapters.Excel</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\Ecr.Application\Ecr.Application.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ClosedXML" />
  </ItemGroup>

</Project>
```

---

### `src/Ecr.Adapters.PiAf/Ecr.Adapters.PiAf.csproj`
MODULE: adapters | STAGE: 0
SCOPE: читання з PI AF двома транспортами.
NOT IN SCOPE: **запис у AF** — його немає взагалі (`D-44`).

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Ecr.Adapters.PiAf</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\Ecr.Application\Ecr.Application.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- PI SQL Client — ODBC; System.Data.Odbc входить у BCL -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

</Project>
```

---

### `src/Ecr.Api/Ecr.Api.csproj`
MODULE: api | STAGE: 0

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <RootNamespace>Ecr.Api</RootNamespace>
    <UserSecretsId>ecr-web-api</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ecr.Domain\Ecr.Domain.csproj" />
    <ProjectReference Include="..\Ecr.Application\Ecr.Application.csproj" />
    <ProjectReference Include="..\Ecr.Expressions\Ecr.Expressions.csproj" />
    <ProjectReference Include="..\Ecr.Infrastructure\Ecr.Infrastructure.csproj" />
    <ProjectReference Include="..\Ecr.Calculations\Ecr.Calculations.csproj" />
    <ProjectReference Include="..\Ecr.Adapters.Excel\Ecr.Adapters.Excel.csproj" />
    <ProjectReference Include="..\Ecr.Adapters.PiAf\Ecr.Adapters.PiAf.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.Negotiate" />
    <PackageReference Include="Scalar.AspNetCore" />
  </ItemGroup>

</Project>
```

---

### `src/Ecr.Api/appsettings.json`
MODULE: api | STAGE: 0
SCOPE: **тільки** рядки підключення і технічні параметри інфраструктури.
NOT IN SCOPE: політика паролів, offsets періодів, ролі, мапінги, адреси
джерел — усе це в БД (`B01` §6.1, ФВ-2.14). Інакше DEV/TEST/PROD розповзаються,
і «чому на тесті інша поведінка» стає щоденним питанням.

```json
{
  "ConnectionStrings": {
    "Ecr": ""
  },
  "Schema": {
    "StartupMode": "Validate"
  },
  "Database": {
    "EditionMode": "Auto",
    "CommandTimeoutSeconds": 60,
    "BulkBatchSize": 50000
  },
  "Cache": {
    "DistributedProvider": "SqlServer",
    "DistributedTableName": "DistributedCache",
    "DistributedSchemaName": "dbo",
    "MetadataSlidingMinutes": 240,
    "AccessProfileSlidingMinutes": 60
  },
  "Jobs": {
    "Provider": "Quartz",
    "WorkerCount": 4,
    "PeriodStateCron": "0 5 0 * * ?",
    "ConsistencyCheckCron": "0 30 2 * * ?",
    "PartitionCheckCron": "0 0 3 1 * ?",
    "MaxParallelRecalculation": 4
  },
  "Auth": {
    "CookieName": "ecr.auth",
    "SlidingHours": 8,
    "RequireHttps": true
  },
  "Api": {
    "DefaultPageSize": 50,
    "MaxPageSize": 500
  },
  "Telemetry": {
    "ServiceName": "ecr-api",
    "OtlpEndpoint": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

---

### `src/Ecr.Api/appsettings.Development.json`
MODULE: api | STAGE: 0

```json
{
  "Schema": {
    "StartupMode": "Migrate"
  },
  "Database": {
    "EditionMode": "Standard"
  },
  "Auth": {
    "RequireHttps": false
  },
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  }
}
```

> `EditionMode = "Standard"` у Development навмисно: бюджет продуктивності має
> витримуватися на **базовій** редакції, а Developer Edition повідомляє
> `EngineEdition = 3` і виглядає як Enterprise (АРХ-7).

---

### `.editorconfig`
MODULE: solution | STAGE: 0

```ini
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
indent_style = space
trim_trailing_whitespace = true

[*.{cs,csx}]
indent_size = 4
dotnet_sort_system_directives_first = true
csharp_using_directive_placement = outside_namespace:warning
csharp_style_namespace_declarations = file_scoped:error
csharp_prefer_braces = true:warning
dotnet_style_require_accessibility_modifiers = always:warning

# Забороняємо мовчазне ігнорування async
dotnet_diagnostic.CS4014.severity = error
dotnet_diagnostic.CS1998.severity = warning

# Nullable
dotnet_diagnostic.CS8600.severity = error
dotnet_diagnostic.CS8602.severity = error
dotnet_diagnostic.CS8603.severity = error
dotnet_diagnostic.CS8618.severity = error

# XML-doc на публічних членах (українською — 08-workflow §9)
dotnet_diagnostic.CS1591.severity = warning

[*.{json,yml,yaml}]
indent_size = 2

[*.{ts,tsx,js,jsx,css,scss,html}]
indent_size = 2

[*.sql]
indent_size = 4

[*.md]
trim_trailing_whitespace = false
```

---

### `.gitignore`
MODULE: solution | STAGE: 0

```gitignore
# .NET
bin/
obj/
[Dd]ebug/
[Rr]elease/
*.user
*.suo
.vs/
artifacts/
TestResults/
coverage*.xml
*.coverage

# Node
node_modules/
dist/
.vite/
*.tsbuildinfo

# Середовище
.env
.env.local
appsettings.Local.json
secrets.json

# ОС та IDE
.DS_Store
Thumbs.db
.idea/
*.swp

# ⚠ Реальні дані НІКОЛИ не потрапляють у репозиторій:
# на ПК-2 вони є, але це не привід їх комітити.
data/
*.xlsm
*.bak
*.dacpac
!docs/**/*.md
```

---

### `README.md`
MODULE: solution | STAGE: 0
SCOPE: точка входу для людини, яка відкрила репозиторій.
NOT IN SCOPE: дублювання документації — лише вказівники.

```markdown
# ECR Web

Універсальний інструмент структурованої звітності. Замінює Excel/VBA-рішення
екологічної звітності (~45 000 рядків VBA) і розрахунковий бекенд на
SQL/CLR/PI AF/SSRS.

## З чого почати

**Уся документація — у `docs/`.**

* **`docs/build/00-START-HERE.md`** — якщо ви розробляєте систему
* `docs/tz/00-README.md` — консолідоване технічне завдання
* `docs/tz/10-decisions.md` — реєстр рішень (джерело істини)

## Збірка

```bash
dotnet restore Ecr.sln
dotnet build Ecr.sln
dotnet test Ecr.sln --filter "Category!=Integration"
```

Повний перелік команд — `docs/build/09-commands.md`.

## Ліцензійна дисципліна

Нова залежність додається **тільки** після перевірки ліцензії за першоджерелом
і запису в `docs/15-verified-stack.md`. Заборонені пакети перелічені в
`Directory.Packages.props` і в `docs/build/04-environment.md` §3.3; CI падає на
будь-якому з них.
```
