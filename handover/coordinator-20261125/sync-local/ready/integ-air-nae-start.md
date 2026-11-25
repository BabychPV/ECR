# Лист готовності: lane/integ-air-nae-start

- [x] Гілка / lane: `lane/integ-air-nae-start` (запушена)
- [x] Пункт: release-blocker живої копії — AIR без NAE_TREE (AIRSYS02)
- [x] Хеш tip: `95214c1ce`
- [x] База: `lane/integ-air-params-rc2` `b40b863f9` + cherry-pick комітів nae-tree-rc2:
  `a85372c33` (контракт), `9e5078a2f`, `e033b574a`, `86ee465aa`, `b25c7364a`, `fa590b16f`, `47d5ff4ff`,
  `eea74a7ca` (DI: NaeTreeSettings один раз, нижче; AirLimitsSettings з Region/Area) + мій `95214c1ce`.
  **Заміщує `air-params-rc2` і `nae-tree-rc2` для RC.**
- [x] Лінійна: merge-комітів 0 (`git rev-list --merges b40b863f9..95214c1ce` → 0)
- [x] Мутацій у кінчику немає
- [x] Sql: ні (мій коміт)
- [x] EF-міграція: ні (автор 0 «system» у `aud.StructureChange.ChangedByUserId` і `cfg.RegistryDef.DefinitionUpdatedByUserId` — FK на sec.User немає)
- [x] Контракт: так — несуть коміти NAE (`a85372c33`); мій коміт контракт не змінює
- [x] shared/ui: ні (мій коміт); клієнт — лише в комітах NAE
- [x] package*.json / lock / Directory.Packages.props: ні
- [x] CLAUDE.md / .github: ні
- [x] Нові ключі UiString: ні
- [x] Нові коди помилок: ні
- [x] Тести (AirTypeStartupTests):
  - новий `Start_without_NAE_TREE_creates_it_empty_as_system_then_provisions_AIR_and_a_second_start_changes_nothing`:
    свіжа база без NAE_TREE → старт створює NAE_TREE (ієрархічний, 5 рівнів, 6 полів, ключ NAE_BUSINESS, 0 записів,
    автор 0) і в тому ж старті AIR має AIR_SOURCES + AIR_SOURCE_SUBSTANCES; другий старт — 0 змін (версія опису,
    рядки журналу, версія AIR); Registry.View → 403 на POST /registries, PUT definition, PUT hierarchy;
    **AF-імпорт у ПОРОЖНІЙ наявний NAE_TREE працює** (applied=true, registryProvisioned=false, 1 SRC);
  - старий тест: NAE_TREE заведено (пласкій, порожній) ДО першого старту → після 4 стартів незмінний, AIR провізиться як раніше;
  - сторож `No_controller_reaches_the_system_provisioner` + чотири системні методи мають бути internal.
- [x] Мутація-доказ (локально, відкочено): `EnsureAsSystemAsync` → public → сторож червоний; повторне збереження рівнів
  наявного довідника в системному шляху → обидва тести старту червоні.
- [x] Таймінгові тести: не зачеплено
- [x] Перевірено локально: `dotnet build Ecr.sln` 0/0 ☑; Architecture 1181/1181 ☑; Api (AirTypeStartup|NaeTree|AirLimits|RegistryHierarchy|RegistryDefinition) 78/78 ☑;
  Application (CreateRegistry|RegistryDefinition|RegistryHierarchy|NaeTree|AirLimits|RegistryDeny) 131/131 ☑
- [x] CI на lane (повний набір, `integ-*`): CI `41533617581` — **success** (gate, server ×10 частин, client, a11y dark/light, windows: worker/msi/msi-install);
  Evidence `41533617646` — **success**; обидва на `95214c1ce`
- Рішення (прийнято координатором 02.11):
  - код довідника — `AirLimits:NaeRegistryCode` (`AirLimitsSettings`), не `NaeTreeSettings` (тести ручного дерева підміняють останній);
  - наявний NAE_TREE (сумісний чи ні) — не чіпається й не перевіряється на старті; AIR іде як раніше;
  - системний шлях: `CreateRegistryHandler.ApplyAsync`, `SaveRegistryDefinitionHandler.SaveForAuthorAsync`,
    `SaveRegistryHierarchyHandler.SaveForAuthorAsync`, `NaeTreeProvisioner.EnsureAsSystemAsync` — internal (Ecr.Api internals не бачить);
    HTTP `HandleAsync` з перевірками прав без змін; порядок старту: NAE (окремий scope/транзакція, відмова = warning) → AIR.
- Ризики: на свіжих стендах/e2e NAE_TREE тепер є з першого старту (порожній); e2e-хелпери це витримують (`ensureNaeTree`, 404→[]).
- Хто зводить: координатор (RC).

- [x] e2e (цільовий, перевірено «Аналізом»): e2e-stand 41537895940 **success** на 95214c1ce, grep «Air limits real|Air params|NAE tree manual|Структура NAE»
