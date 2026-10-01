# AN-7: linked server (OPENQUERY) для PiSqlClient - розглянуто, відкладено

Linked server (OPENQUERY) для PiSqlClient розглянуто, відкладено; будувати лише за явною вимогою замовника (умови: ODBC до RTQP з сервера ECR неможливий; DBA замовника володіє AFServFlert і login mapping; ІБ погодила RPC OUT). Штатний транспорт - прямий ODBC (D-46/D-245).

Повний звіт: `.sync-local\reports\an7-linked-server-2026-10-01.md` (поза git; стислий підсумок нижче). Дослідження, код не змінено.

## Підсумок порівняння

| Критерій | Прямий ODBC (чинний) | Linked server + OPENQUERY |
|---|---|---|
| SSRF | Адреса в `Endpoint`; захист D-245 | Потрібен allowlist імен linked server |
| Параметри | Типізовані `OdbcParameter` | OPENQUERY без параметрів: конкатенація, ризик ін'єкції |
| Права | Мінімальне читання | Ширші: RPC OUT / mapping |
| Таймаути | `CommandTimeout`, ретраї (Q-251) | Глобальні для інстансу |
| Експлуатація | Конфіг у `DataSource` | Налаштовує DBA замовника, не мігрує з БД |

## Рекомендація

Прямий ODBC лишається єдиним штатним транспортом (D-46, D-245, сторож `ForeignDatabaseAccessTests`). Linked server - лише необов'язковий fallback P2, не будувати до появи вимоги.

## План fallback (без коду)

1. Конфіг `PiSqlClient:LinkedServer:Enabled=false`, `AllowedNames=[...]` - allowlist імен.
2. Ім'я лише точним збігом з allowlist, `QUOTENAME`; інше - 422 з новим кодом за правилами реєстрації кодів.
3. Запити лише з шаблонів з фіксованими `*_V`; літерали з жорсткою валідацією; довільного SQL з UI немає.
4. Окремий SQL-логін з мінімальними правами; RPC OUT і Ad Hoc Distributed Queries вимкнені, OPENROWSET заборонено.
5. DBA замовника створює linked server і login mapping; картка здоров'я робить `SELECT 1` через OPENQUERY.
6. Окремий `CommandTimeout`, ретраї як у Q-251.
7. Тести: allowlist, ін'єкція в імені, шаблон, відсутність OPENROWSET.
8. Зафіксувати новим `D-nn` (дефолт - вимкнено), узгодити з ІБ.

## Відкриті питання

1. Чи справді в замовника ODBC до RTQP з сервера ECR неможливий?
2. Хто володіє `AFServFlert` на проді і з яким login mapping?
3. Чи погоджується ІБ на RPC OUT?
