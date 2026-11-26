# Картка (заплановано): integ-ms-formulas-api-retire — виведення формульного авторингу з сервера

Статус: planned
Гілка: lane/integ-ms-formulas-api-retire
Старт — **після вікна b12**, коли виконано ОБИДВІ умови:
1. на live 0 версій методологій з `Engine = Formulas` (P-1124-2 перетворює їх на скрипт із паритетом):
   `SELECT COUNT(*) FROM calc.MethodologyVersion WHERE Engine = 0` → 0;
2. збирачі стендів дають скрипт, а не формули: `tools/land/Build-LandTemplate.ps1` (імпорт пакета AF + публікація V1)
   і `tools/hse400/Build-Hse400Template.ps1` (версія Formulas + формули/константи/правило/тести) переведено на
   скрипт (або на перенесення P-1124-2 одразу після імпорту).

Походження: P-1124-1 (lane `lane/integ-ms-only-editor`, людина 24.11 «має бути тільки наш новий редакор», «без боргів»).
У P-1124-1 формульний UI прибрано, сервер відхиляє новий формульний авторинг за ключем
`Calculations:FormulaEngineAuthoring:Enabled` (у продукті false), дії без жодного споживача
(`constants/{code}/usage`, `rule-coverage`) прибрано з сервера. Решта формульних дій лишилася ЛИШЕ для інструментів
збирання — це і є предмет картки.

## Що прибрати (одним зміненням контракту)
Дії `ServerOnlyActions` у `tests/Ecr.Architecture.Tests/EndpointCoverageTests.cs` (блок P-1124-1):
- `GET|PUT|DELETE …/versions/{vid}/formulas[/{code}]`
- `GET|PUT …/versions/{vid}/constants[/{code}]`
- `GET|PUT|DELETE …/versions/{vid}/categories[/{code}]`
- `GET|PUT …/versions/{vid}/rules[/{code}]`
- `GET|PUT|DELETE …/versions/{vid}/category-rule`
- `GET|PUT …/versions/{vid}/outputs[/{code}]`
- `POST /api/v1/methodologies/import`

Разом: обробники, порти/сховища лише для них, `ImportMethodologyPackageHandler` і планувальник пакета, ключ
`Calculations:FormulaEngineAuthoring:Enabled` (appsettings, Program.cs, EcrApiFactory/SecondNodeApiFactory,
tools/e2e-stand.ps1, перевірки в Build-LandTemplate/Build-Hse400Template), `FormulaEngineAuthoringApiTests`,
рядки `02-contracts.md` §9, `openapi.snapshot.json` + `schema.d.ts` (останнім кроком), ключі сіду лише цих дій
(у «Прибрані ключі»).

## Що НЕ входить
- Рантайм формульного рушія (`GenericCalculationModule`, `IFormulaEngine`) — лише коли історичні періоди теж
  рахуються скриптом (окреме рішення людини: версії V1 з 2024-01-01 — історія).
- e2e `calculationIssues` / `calculationMissingInputs` будують заготовку формулами — переписати на скрипт у цій же
  картці (інакше вони тримають ключ на стенді e2e).
- Сценарні/API-тести на формульних заготовках (EcrApiFactory вмикає ключ) — перевести на скрипт або на прямий запис
  у базу; оцінка — ~15 файлів API/Scenarios.

## DoD
EndpointCoverage без блоку P-1124-1; контракт без формульних дій; збирачі Land/HSE400 зелені на скрипті; e2e обох
спеків на скрипті; Architecture + повний CI integ-*.
