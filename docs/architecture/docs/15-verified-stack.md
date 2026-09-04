# Перевірений стек: ліцензії та можливості

Перевірено за першоджерелами **2026-09-02** (два проходи). Мета — щоб питання
«а чи можна це використовувати» і «а чи вміє воно те, що нам треба»
не поверталося на етапі розробки.

> ⚠ **Правило проєкту:** жодна залежність не додається без запису в цій таблиці.
> Перевірка ліцензій — окремий крок у CI (див. §7).

Легенда стовпця «Перевірено»:
* ✅ **fetched** — звірено з першоджерелом у цьому документі (посилання в §9);
* ✅ **known** — загальновідома стабільна ліцензія (Apache/MIT/BSD у великих проєктів),
  повторна перевірка при першому `dotnet add package` / `npm i` через CI-політику §7.

---

## 1. Критичні знахідки — те, що змінило рішення

### 🔴 1.1 .NET 8 виходить з підтримки 10 листопада 2026

.NET 8 і .NET 9 досягають End of Support **10.11.2026**. Проєкт, що стартує
у вересні 2026, **не має йти на .NET 8**. Цільова платформа — **.NET 10 LTS**
(підтримка до листопада 2028). *Виправлення першої редакції ТЗ.*

### 🔴 1.2 AG Grid Community функціонально не покриває вимогу

Fill Handle, range selection і повноцінний clipboard — **Enterprise**.
Community має лише базове копіювання. Або платити (~$1000/розробник/рік),
або інший компонент.

### 🔴 1.3 Glide Data Grid — остання публікація ~3 роки тому

MIT і функціонально підходить (copy/paste, fill handle, multi-select), **але
остання версія 6.0.3 опублікована близько трьох років тому**. Для системи
з горизонтом життя 10+ років це ризик: React-екосистема за три роки
змінюється істотно.

**Наслідок:** з основного кандидата переведено в **запасний**.

### 🟢 1.4 RevoGrid — MIT, активно супроводжується, Excel-подібний

Open-core, **MIT** для ядра; **остання версія 4.21.8 опублікована 6 днів тому**;
CD-конвеєр збирає обгортки для React / Angular / Vue / Svelte. Заявлено:
Excel-подібна навігація і редагування, безшовне copy/paste з Excel і Google Sheets,
віртуалізація великих наборів.

**Наслідок:** **основний кандидат**. Прототип на Етапі 0 — на RevoGrid,
із Glide Data Grid як запасним. AG Grid Enterprise — крайній варіант.

> ⚠ «Open-core» означає, що частина функцій може бути у платних плагінах.
> На прототипі перевірити, що потрібні нам fill handle / range selection /
> clipboard — у MIT-ядрі, а не в Pro-плагінах.

### 🔴 1.5 EFCore.BulkExtensions — платна для компаній від $1M виручки

Dual license (cFOSS) з 2023: безкоштовна лише для приватного використання,
некомерційних організацій і компаній з виручкою до $1 млн. **Для замовника —
платна.** Замінено на `SqlBulkCopy` (MIT, у складі .NET) + `ExecuteUpdate`/
`ExecuteDelete` + raw SQL для архівації. Окрема бібліотека не потрібна.

### 🔴 1.6 NBomber — комерційна для організацій з версії 5

NBomber v5+ і NBomber Studio **безкоштовні лише для особистого використання**;
для організацій — комерційна ліцензія від $99/користувач/місяць. v4 лишається
під Apache 2.0, але це застаріла гілка.

**Наслідок:** для навантажувального тестування — **Apache JMeter** (Apache 2.0)
або **k6** (див. нижче). *Виправлення документа 14-performance.md.*

### 🟠 1.7 Redis — AGPLv3 / SSPL / RSALv2

У 2024 Redis перейшов з BSD на SSPL/RSALv2; у 2025 (Redis 8) додав **AGPLv3**.
Внутрішнє використання немодифікованого сервера юридично прийнятне, але
**наша ж CI-політика забороняє AGPL**, і багато корпоративних політик ІБ — теж.

**Наслідок — три варіанти за зростанням складності:**

| Варіант | Ліцензія | Коли |
|---------|----------|------|
| **Без окремого кеш-сервера**: `IMemoryCache` для незмінних метадат + `IDistributedCache` з SQL Server-провайдером для `AccessProfile` | у складі .NET | **за замовчуванням** — для 100 користувачів достатньо |
| **Microsoft Garnet** — RESP-сумісний сервер на .NET | **MIT** | якщо потрібен справжній кеш-сервер; природний вибір для Windows/IIS-контуру |
| **Valkey** — форк Redis 7.2 під Linux Foundation | BSD-3 | якщо є Linux-інфраструктура і досвід із Redis |

Клієнт `StackExchange.Redis` (MIT) працює з усіма трьома.

### 🟠 1.8 Swashbuckle більше не типовий вибір для .NET 10

Microsoft прибрав Swashbuckle з шаблону `webapi` ще у .NET 9; **.NET 10
генерує OpenAPI 3.1 нативно** через `Microsoft.AspNetCore.OpenApi`.
Swashbuckle досі підтримується, але для нового проєкту сенсу в ньому мало.

**Наслідок:** генерація документа — вбудований `Microsoft.AspNetCore.OpenApi`;
інтерактивний UI — **Scalar.AspNetCore** (MIT). *Виправлення ТЗ §2.1.*

### 🟢 1.9 PI SQL DAS (RTQP Engine) не потребує Kerberos-делегування

RTQP Engine не вимагає налаштування Kerberos Delegation, має спрощену схему,
працює швидше. Обмеження: **тільки читання**, asset-centric (PI Points через
AF-атрибути), клієнт — PI SQL Client (ODBC). Запис — лише через PI Web API.

---

## 2. Backend

| Компонент | Ліцензія | Перевірено | Примітка |
|-----------|----------|------------|----------|
| **.NET 10 (LTS)** + ASP.NET Core | MIT | ✅ fetched | до 11.2028 |
| **EF Core 10** | MIT | ✅ fetched | |
| **Microsoft.Data.SqlClient** (`SqlBulkCopy`) | MIT | ✅ known | основний засіб масових операцій |
| **Microsoft.AspNetCore.OpenApi** | MIT | ✅ fetched | нативна генерація OpenAPI 3.1 |
| **Scalar.AspNetCore** | MIT | ✅ fetched | UI для OpenAPI |
| `Microsoft.AspNetCore.Authentication.Negotiate` | MIT | ✅ known | Kerberos/NTLM |
| `Microsoft.AspNetCore.Identity` (`PasswordHasher<T>`) | MIT | ✅ known | PBKDF2 |
| `Konscious.Security.Cryptography.Argon2` | MIT | ✅ known | альтернатива PBKDF2 |
| `Otp.NET` | MIT | ✅ known | TOTP для 2FA |
| **ClosedXML** | MIT | ✅ fetched | 100% безкоштовна для комерційного використання |
| **NCalc** | MIT | ✅ fetched | |
| **Hangfire.Core** | **LGPL** | ✅ fetched | ⚠ §6 — використання дозволене, форки — ні; потребує підтвердження ІБ |
| Quartz.NET *(альтернатива Hangfire)* | Apache 2.0 | ✅ known | без дашборда |
| Serilog + sinks | Apache 2.0 | ✅ known | |
| Polly | BSD-3 | ✅ known | |
| FluentValidation | Apache 2.0 | ✅ known | |
| Mapster | MIT | ✅ known | |
| OpenTelemetry .NET | Apache 2.0 | ✅ known | |
| `IDistributedCache` SQL Server provider | MIT | ✅ known | у складі ASP.NET Core |
| **Microsoft Garnet** *(якщо потрібен кеш-сервер)* | MIT | ✅ fetched | RESP-сумісний, .NET |
| `StackExchange.Redis` | MIT | ✅ known | клієнт для Garnet/Valkey |
| **DataObjects.Net** *(розглянуто, не обрано)* | MIT з v6 | ✅ fetched | аргументи проти — ТЗ §5.4-A |

### Тестування

| Компонент | Ліцензія | Перевірено | Примітка |
|-----------|----------|------------|----------|
| xUnit | Apache 2.0 | ✅ known | |
| Testcontainers for .NET | MIT | ✅ known | SQL Server у Docker для інтеграційних тестів |
| WireMock.Net | Apache 2.0 | ✅ known | мок PI Web API |
| NetArchTest.Rules | MIT | ✅ known | архітектурні правила |
| **Apache JMeter** | Apache 2.0 | ✅ known | **навантажувальне тестування — основний** |
| k6 | **AGPLv3** | ✅ known | ⚠ standalone-інструмент, у продукт не лінкується — юридично ок, але суперечить CI-політиці «без AGPL»; використовувати лише за явним дозволом ІБ |
| Locust | MIT | ✅ known | альтернатива (Python) |

### ⛔ Заборонено

| Компонент | Проблема |
|-----------|----------|
| **EPPlus 5+** | Polyform Noncommercial |
| **EFCore.BulkExtensions** | cFOSS — платна від $1M виручки |
| **NBomber v5+** | комерційна для організацій |
| **Redis** (сервер) | AGPLv3 / SSPL — суперечить політиці; замість — Garnet/Valkey/без кеш-сервера |
| Будь-що під **GPL / AGPL** як залежність | несумісно із закритим застосунком |

---

## 3. Frontend

| Компонент | Ліцензія | Перевірено | Примітка |
|-----------|----------|------------|----------|
| React, TypeScript, Vite | MIT | ✅ known | |
| **Mantine** | MIT | ✅ fetched | |
| **RevoGrid** (`@revolist/react-datagrid`) | **MIT** (open-core) | ✅ fetched | **основний grid**; на прототипі підтвердити, що потрібні функції — у MIT-ядрі |
| Glide Data Grid | MIT | ✅ fetched | **запасний**: функціонально ок, але остання публікація ~3 роки тому |
| **@formulajs/formulajs** | MIT | ✅ fetched | |
| TanStack Query / Router / Virtual | MIT | ✅ known | |
| Zustand, React Hook Form, Zod, dnd-kit | MIT | ✅ known | |
| Tabler Icons | MIT | ✅ known | |
| Recharts / Apache ECharts | MIT / Apache 2.0 | ✅ known | |
| orval / openapi-typescript | MIT | ✅ known | генерація типів з OpenAPI |
| Vitest, Testing Library, Playwright | MIT / Apache 2.0 | ✅ known | |

### ⛔ Заборонено

| Компонент | Проблема |
|-----------|----------|
| **HyperFormula** | GPLv3 або комерційна |
| **Handsontable** | безкоштовна лише для некомерційного використання |
| **AG Grid Enterprise** | платна; лише крайній варіант |
| AG Grid Community | MIT, але **немає fill handle і range selection** |

---

## 4. Формули: реальний обсяг — 11 функцій

Автоматичний аналіз усіх формул шаблону: `VLOOKUP` 429 · `LEFT` 384 · `SUM` 245 ·
`VALUE` 230 · `IFERROR` 228 · `ROUNDDOWN` 5 · `TEXT` 3 · `CHAR` 3 · `CONCATENATE` 2 ·
`TEXTBEFORE` 2 · `TEXTAFTER` 2.

429 `VLOOKUP` — посилання на довідники, які зникають (замінюються
`ColumnDef.LookupRegistryDefId`). Реальних обчислювальних формул — **~250**.

---

## 5. Крос-аркушні залежності — 7 ребер

`7.0` ← `7.` (216, Rollup) · `7a` ← `7.` (191, Mirror) · `7b` ← `7.` (100, Mirror) ·
`7.` → `Configuration` (273) · `8.` → `Configuration` (156) · `7.`/`8.` → `2. Contract` (по 1).
**Решта 18 аркушів незалежні.**

---

## 6. Hangfire: LGPL — що це означає

Hangfire Core під LGPL **може використовуватися в комерційних і пропрієтарних
застосунках**. Комерційна ліцензія потрібна лише для розповсюдження приватних
форків і модифікацій. Ми — внутрішня система, форків не робимо.

**Дія:** підтвердити з ІБ/юристами. Якщо LGPL заборонена політикою — Quartz.NET.

---

## 7. Перевірка ліцензій у CI

```
dotnet-project-licenses      → звіт по NuGet
license-checker              → звіт по npm

ALLOW  : MIT, Apache-2.0, BSD-2/3, ISC, MS-PL, 0BSD, Unlicense
REVIEW : LGPL-*, MPL-2.0                    → ручне рішення + запис у цей документ
DENY   : GPL-*, AGPL-*, SSPL, RSAL, Polyform-*, cFOSS, будь-яка «noncommercial»
```

Звіт — артефакт збірки. Нова залежність без запису тут = червона збірка.

---

## 8. Що перевірити на Етапі 0

| # | Питання | Хто |
|---|---------|-----|
| 1 | RevoGrid: fill handle / range selection / clipboard — у MIT-ядрі чи в Pro-плагінах | frontend |
| 2 | Чи встановлений PI SQL DAS (RTQP) і чи є ліцензія PI | PI-адмін |
| 3 | Чи дозволена LGPL (Hangfire) | ІБ / юристи |
| 4 | Чи дозволений k6 (AGPL, standalone) або обмежуємося JMeter | ІБ |
| 5 | Редакція SQL Server (partitioning + columnstore є в Standard з 2016 SP1) | DBA |
| 6 | SMTP у контурі | інфраструктура |
| 7 | Чи потрібен кеш-сервер узагалі, чи достатньо `IDistributedCache` на SQL | архітектор, за прототипом |

---

## 9. Джерела

**Платформа**
* [.NET 8 and .NET 9 will reach End of Support on November 10, 2026 — .NET Blog](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
* [.NET official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
* [ASP.NET Core Dropped Swagger — What Replaced It in .NET 10](https://codewithmukesh.com/blog/dotnet-swagger-alternatives-openapi/)
* [Scalar vs Swashbuckle vs NSwag in ASP.NET Core 2026](https://codingdroplets.com/scalar-vs-swashbuckle-vs-nswag-in-asp-net-core-which-openapi-tool-should-your-net-team-use-in-2026)

**Grid**
* [RevoGrid — Licensing](https://rv-grid.com/guide/licensing)
* [RevoGrid — LICENSE (MIT)](https://github.com/revolist/revogrid/blob/main/LICENSE)
* [@revolist/react-datagrid — npm](https://www.npmjs.com/package/@revolist/react-datagrid)
* [@glideapps/glide-data-grid — npm](https://www.npmjs.com/package/@glideapps/glide-data-grid)
* [Glide Data Grid — GitHub](https://github.com/glideapps/glide-data-grid)
* [AG Grid — Fill Handle](https://www.ag-grid.com/javascript-grid-range-selection-fill-handle/)
* [AG Grid — Clipboard](https://www.ag-grid.com/javascript-data-grid/clipboard/)

**Бібліотеки**
* [ClosedXML — LICENSE](https://github.com/ClosedXML/ClosedXML/blob/develop/LICENSE)
* [NCalc — LICENSE](https://github.com/ncalc/ncalc/blob/master/LICENSE)
* [Hangfire — Licenses](https://www.hangfire.io/licenses.html)
* [EFCore.BulkExtensions — LICENSE change notice](https://github.com/borisdj/EFCore.BulkExtensions/issues/1079)
* [NBomber — License](https://nbomber.com/docs/getting-started/license/)
* [Mantine UI](https://ui.mantine.dev/)
* [@formulajs/formulajs — npm](https://www.npmjs.com/package/@formulajs/formulajs)
* [DataObjects.Net — GitHub](https://github.com/DataObjects-NET/dataobjects-net)

**Кеш**
* [Redis is now available under the AGPLv3 — Redis](https://redis.io/blog/agplv3/)
* [Understanding Redis Licensing (SSPL, AGPLv3, Dual License)](https://oneuptime.com/blog/post/2026-03-31-redis-licensing-sspl-agplv3-explained/view)
* [Redis vs Valkey After the License Drama](https://sumguy.com/redis-vs-valkey-2026/)

**PI**
* [PI SQL Data Access Server (RTQP Engine) — Introduction](https://docs.aveva.com/bundle/pi-sql-data-access-server-rtqp-engine/page/1016057.html)
* [PI OLEDB Enterprise support and RTQP Engine](https://docs.aveva.com/bundle/pi-sql-data-access-server-rtqp-engine/page/1215021.html)
