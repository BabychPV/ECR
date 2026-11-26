# Лист готовності гілки

- [x] Гілка / lane: `lane/integ-install-docs-gen`
- [x] Пункт WORK-QUEUE (ID): немає рядка (слово людини 02.11: «інструкція встановлення має генеруватися й підтримуватися автоматично»)
- [x] Хеш tip: `3a746977e36d4a72188ea13f37e4cfc2c627097b`
- [x] База (origin/main, від якого гілка): `ddf5f2fc3652a0e0e18d78f256ad8cda7331fa85` (merge PR #688; гілка вирізана від `main`, не від `dev/integration`)
- [x] Лінійна: merge-комітів 0, один коміт
- [x] Мутацій у кінчику немає (докази червоного зроблено локально й повернуто `git checkout`)
- [x] Sql: ні
- [x] EF-міграція: ні
- [x] Контракт openapi.snapshot.json / schema.d.ts: ні
- [x] shared/ui: ні
- [x] package*.json / lock / Directory.Packages.props: ні (лише читається)
- [x] CLAUDE.md / .github: ні (`.github/workflows/ci.yml` лише читається інструментом; крок CI — нижче для Аудиту-2)
- [x] Нові ключі UiString: ні
- [x] Нові коди помилок: ні
- [x] Тести: `tests/Ecr.Architecture.Tests/GeneratedInstallDocsTests.cs` (запускає `tools/gen-install-docs.ps1 -Check -SkipGrants` через pwsh/powershell + 3 перевірки наявності блоків)
- [x] Мутація-доказ: (1) правка рядка в блоці `generated:sql-order` у `11-install-guide.md` -> `GeneratedInstallDocsTests` червоний (1 fail із 4); (2) перестановка двох скриптів у `$scripts` `deploy-ecr.ps1` -> `-Check` exit 1 з обома переліками і підказкою. Обидві зміни повернуто.
- [x] Таймінгові тести: не застосовно
- [x] CI на lane: CI run 41512351678 = success, Evidence run 41512351745 = success; 8 гейтів: build / test / honesty-guard / server / client / a11y (dark) / a11y (light) / pr-source
- [x] Перевірено локально: build ☑  Ecr.Architecture.Tests повністю 1184/1184 ☑  vitest — не зачеплено
- [x] Ризики: (а) тест вимагає `pwsh` (Linux-раннер ubuntu-latest має) або `powershell` у PATH — інакше червоний, не skip; (б) розбір `EcrConfigurationValidation` — регулярні вирази: якщо ключ задано виразом, якого інструмент не розкриває (не літерал/константа/конкатенація/інтерполяція), `-Check` червоніє з назвою ключа — тоді доповнити інструмент; (в) у таблиці конфігурації «Значення» бере коментар лише з того ж рядка (коментарі над групами ключів вводили б в оману); (г) блок `generated:grants` як і раніше перевіряє `ServiceAccountGrantsRunbookTests` (у -Check тесту передано -SkipGrants)
- [x] Хто зводить: Аудит-2 / інтегратор

## Що генерується

| Блок | Документ | Джерело |
|---|---|---|
| `generated:grants` (як було) | runbook, install-guide, QUICKSTART | `ServiceAccountGrants` (TestKit) |
| `generated:config` | `docs/admin/operations-runbook.md` (після §2) | `EcrConfigurationValidation.cs` + `appsettings.json` |
| `generated:sql-order` | `docs/build/11-install-guide.md` (після §2.2) | `$scripts` у `setup-dev-db.ps1` та `deploy-ecr.ps1` (розбіжність = помилка) |
| `generated:prereqs` | `docs/build/11-install-guide.md` (кінець §0) | `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `deploy-ecr.ps1`, `build-msi.ps1`, `Package.wxs`, `ci.yml` |

Команди: `powershell -File tools/gen-install-docs.ps1` (переписати), `... -Check` (звірка, код 1 при розбіжності), `... -From <ref> -To <ref>` (нотатки оновлення у STDOUT, нічого не пише — для описів RC / release-notes; вивід не комітити).

## Крок CI для Аудиту-2 (додати в `.github/workflows/ci.yml`, job `test` або `build`; зараз сторож і так іде в `Ecr.Architecture.Tests`, цей крок — швидкий ранній сигнал і зручний вивід, необов'язковий)

```yaml
      - name: Install docs are in sync with sources
        shell: pwsh
        run: ./tools/gen-install-docs.ps1 -Check -SkipGrants
```

(Для нотаток оновлення в RC-описі: `pwsh ./tools/gen-install-docs.ps1 -From origin/main -To HEAD >> $GITHUB_STEP_SUMMARY` — лише STDOUT, файлів не змінює; потрібен `fetch-depth: 0`.)
