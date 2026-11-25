# Лист готовності — lane/integ-install-docs-regen (fix-forward червоного main)

- Гілка: `lane/integ-install-docs-regen`
- Привід: main 4f244d9b9, CI push 41547332753 червоний — `GeneratedInstallDocsTests` (блок generated:config розійшовся: #691 влито раніше за #696/#697, що додали ключі).
- Хеш tip: `c90092fd1`
- База: `4f244d9b9` (origin/main), один коміт, merge-комітів 0
- Зміна: лише `docs/admin/operations-runbook.md` (+3 рядки: `AirLimits:AreaRegistryCode`, `AirLimits:RegionRegistryCode`, `NaeTree:RegionSourceRegistryCode`), згенеровано `tools/gen-install-docs.ps1`
- Sql: ні; EF-міграція: ні; контракт: ні; shared/ui: ні; lock: ні; CLAUDE.md/.github: ні; UiString: ні; коди помилок: ні
- Перевірено локально: `gen-install-docs.ps1 -Check` — чисто; `Ecr.Architecture.Tests` 1185/1185
- CI на lane: CI 41555543027, Evidence 41555542904 — вердикт дописується нижче
- Примітка: lane/integ-doclist-allversions (#698) конфігурацію не змінює; на базі з цим комітом її блоки не розходяться
- Хто зводить: координатор (RC)
