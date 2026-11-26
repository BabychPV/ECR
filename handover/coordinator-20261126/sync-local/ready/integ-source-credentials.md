# Картка: source-credentials Джерело PI SQL Client / SQL — вхід або Windows, або логіном і паролем
Статус: planned
Гілка: lane/integ-source-credentials
Пачка: після 29.11
Ризик: R3 (міграція ext.DataSource, контракт /data-sources, секрет у БД, безпека)
Рішення: P-1126-SRCCRED — людина 26.11 (скрини New connection PiSqlClient + «PI SQL Client ODBC Login» WIN-ABAANR1OQJE\Administrator, «Test connection succeeded»): «мені також потрібно мати підключення по юзеру та паролю чи можу я ввести в строку?» → варіант «б» (поля в ECR) і «потрібно і вин та по користувачу вхід»
Дизайн: .sync-local\DESIGN-source-credentials.md (rev 2 26.11 після незалежного огляду REVIEW-source-credentials.md, 24/24 закрито; Q-13/15/16 і Q-6 — відповіді людини/координатора; AC-0..AC-15 у §14 DESIGN замінюють таблицю нижче; бриф коду — після 29.11 17:00)

## 1. Що і навіщо
- Зараз: Endpoint/Secondary endpoint не приймають облікових даних (`RequireNoCredentials`, `Ecr.Application\Integration\DataSourceHandlers.cs:694`, рішення Q15-06: сховища секретів немає, пароль у рядку пішов би в ext.DataSource, бекапи і кожну відповідь GET). Працює лише Integrated Security обліковкою служби ECR.
- Людині потрібні ОБИДВА режими: **Windows** (як зараз, Integrated Security=SSPI) і **користувач + пароль** (PI AF у робочій групі, напр. WIN-ABAANR1OQJE\Administrator).
- Прецедент у коді: `SmtpSettings.PasswordProtected` + `ReplacePassword` (DataProtection, write-only) — повторити той самий підхід.
- Споживачі: Sources → New connection / Edit / Test connection; збір PI SQL Client і Sql (DiscoverAsync, колекція), секундарна адреса.
- Умова зупинки: будь-що, що повертає пароль у відповідь API / журнал / аудит; потреба в зміні DataProtection-ключів live.

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-0 | live: Sources → New connection (PiSqlClient), «Authentication: User name and password», AF Server WIN-ABAANR1OQJE / ECR_01_Air, логін Administrator + пароль → Test connection «Connected»; режим «Windows» для FLERT і далі «Connected» | live: кроки людини | |
| AC-1 | форма: перемикач «Windows (обліковка служби ECR)» / «User name and password»; поля User name, Password (write-only: після збереження — «•••• set», «Change password», «Clear») для PiSqlClient і Sql | vitest + e2e | |
| AC-2 | пароль у БД лише зашифрованим (DataProtection, окреме purpose); GET/список/аудит/журнал ніколи не містять пароля (і не містять довжини); заміна пароля — окремою дією з аудитом «DataSourceCredentialChanged» без значення | тест API + тест журналу | |
| AC-3 | збір і Test connection підставляють логін/пароль у рядок драйвера лише в пам'яті в момент з'єднання (PI SQL Client: UID/PWD, Sql: User ID/Password); для Secondary endpoint — ті самі облікові дані | тест адаптера | |
| AC-4 | `RequireNoCredentials` лишається: облікові дані в самій адресі й далі відхиляються (підказка веде на поля логіна) | тест | |
| AC-5 | Windows-режим поводиться як зараз (FLERT, наявні джерела — без змін після міграції) | тести регресії + live | |
| AC-R | наявні джерела після міграції = Windows; жодного розшифрування без потреби | тест міграції | |

## 3. Вплив (заповнити в DESIGN)
- Міграція ext.DataSource (+AuthMode, +UserName, +PasswordProtected varbinary) — одна в польоті; контракт openapi/schema.d.ts; сід текстів UI; права — лише адміністратор джерел; DataProtection-ключі live мають бути постійні (перевірити live-env/AllowUnprotectedKeys=true на стенді!).
