# Лист готовності: lane/integ-live-status-304

- Гілка / lane: `lane/integ-live-status-304` (стек на 3370977c0 — PR #685, live-status UI; за вказівкою координатора)
- Пункт: регресія e2e «Live refresh … hidden sheet answers 404» на PR #685 («marker answers: 200,200,304»)
- Хеш tip: `89baae3763ed62dbbd01762f9f0e28f4d79281e2`
- База: `3370977c0` (над `03871aa4b` #680 і `93b8f438c`)
- Лінійна: так, 1 новий коміт; мутацій у кінчику немає
- Sql / EF-міграція / контракт / shared/ui / lock / CLAUDE.md / .github: ні
- Нові ключі UiString / коди помилок: ні

## Причина
Не кеш браузера і не middleware: `Cache-Control: no-store` доходить (тепер перевіряється і на 304). Клієнт
`93b8f438c` НАВМИСНО шле `If-None-Match` з останнім ETag (`pollSheetChanges` → `apiFetchIfChanged`); запис у
сховану від оператора таблицю не змінює його ETag → 304 = «нічого нового». Спек чекав лише 200.
Сервер (304 лише на явний збіг, після перевірки доступу) і клієнт (304 — no-op, опитування триває) коректні;
код продукту не змінювався.

## Зміни (тести)
- e2e `tableChatLiveRefresh.spec.ts`: опитування видимого аркуша 200|304 (перше — 200); схований аркуш питається
  з ETag видимого в `If-None-Match` → 404; 422 без змін.
- Api `SheetChangesApiTests.Схований_аркуш_з_If_None_Match_404_без_заголовка_200_зі_збігом_304`.
  Мутація (304 на будь-який If-None-Match до обробника) → червоне.
- vitest `SheetLiveStatus.test.tsx`: серія 304 без банерів, той самий ETag, наступний перехід ловиться.
  Мутація (304 як фінальна помилка) → червоні обидва тести 304.

## Перевірено локально
build Ecr.sln 0; Architecture 1176/1176; Api SheetChangesApiTests 4/4; vitest liveRefresh 17/17; tsc -b 0; eslint 0.

## CI / e2e
- CI (push, lane/integ-* = повний): run 41486190730 — success (server-part ×10, client, a11y dark/light, windows/msi — усі зелені)
- Evidence: run 41486190908 — success
- e2e-stand grep «Live refresh»: run 41487941080 — success
- e2e-stand grep «Live status»: run 41487947351 — success
  (перші 41486225916/41486231987 впали на checkout: у `ref` передано короткий SHA — не дефект)

## Хто зводить
Координатор: один RC з цієї lane (містить #685).
