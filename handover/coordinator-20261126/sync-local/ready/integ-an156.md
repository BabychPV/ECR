# Лист готовності — lane/integ-an156 (AN-156, на main a52945f9c)

- Гілка: `lane/integ-an156`
- Пункт: AN-156 (холодний старт)
- Хеш tip: `a9f12c230`
- База: `a52945f9c` (origin/main після #698); лінійна, merge-комітів 0
- Коміти:
  1. `b0c11a0fd` — cherry-pick -x `c90092fd1` (lane/integ-install-docs-regen: регенерація generated:config, лікує червоний main). Без нього CI цієї гілки впав би на GeneratedInstallDocsTests. Цю lane можна брати ЗАМІСТЬ integ-install-docs-regen.
  2. `a9f12c230` — журнал тривалості кроків старту (порядок кроків НЕ змінено) + звіт `docs/build/perf/AN-156-cold-start.md` із рішеннями координатора 03.11 (паралельні 4a/5/6 відхилено; D2/D3 — ні; D1 — пізніше; D7 — на MSI під час видачі)
- Sql: ні; EF-міграція: ні; контракт: ні; shared/ui: ні; lock: ні; CLAUDE.md/.github: ні; UiString: ні; коди помилок: ні
- Перевірено локально: build ✓; Ecr.Architecture.Tests 1185/1185; Ecr.Api.Tests ~Startup 116/116
- CI на lane: CI 41557687171 — attempt 2 **success** (attempt 1: api-1 упав на гонці model-binding record, не пов'язаній зі зміною — фікс у lane/integ-approve-record-race); Evidence 41557687145 **success**; обидва на a9f12c230
- Пробна lane/an156-cold-start (паралельні кроки) — НЕ зводити
- Хто зводить: координатор (RC)
