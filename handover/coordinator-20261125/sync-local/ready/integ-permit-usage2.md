# Лист готовності: lane/integ-permit-usage2 (usedInDocumentCount типу дозволу)

- Гілка: `lane/integ-permit-usage2`
- База: `origin/main` = `8360525a6`
- Tip / контрактний коміт: `8346cb0c3`; перед ним cherry-pick лінії PERMUSE: ab3014d74 (=22a3a4dfe), edb394d1d (=f3b9e54ec), 84dbf2c6a (=cb61fce69) — без конфліктів
- Лінійна: merge-комітів 0; мутацій у кінчику немає
- Що: `usedInDocumentCount` у `PermitTypeListItemDto` (GET /permit-types) і `PermitTypeUsageDto` (GET /permit-types/{code}/usage) — у скількох різних документах обрано бодай один дозвіл типу (шапка чи комірка); лише проєкти з Document.View читача (як PermitListItem); видалені дозволи/рядки не рахуються. Порт: `IPermitStore.CountTypeUsedInDocumentsAsync`.
- Sql: ні (05-rpt-views.sql — лише з cherry-pick f3b9e54ec); EF-міграція: ні
- Контракт: так — openapi.snapshot.json + schema.d.ts перегенеровано останнім кроком, повтор байт-в-байт; клієнтські фікстури типів (5 файлів тестів) отримали поле
- shared/ui / lock / CLAUDE.md / .github / UiString / коди помилок: ні
- Тести: PermitUsagesHttpTests +1 (`Тип_дозволу_віддає_у_скількох_видимих_документах…`: writer 1 на обох ендпоінтах, outsider без Document.View — 0)
- Мутація: прибрати фінальний `.Distinct()` у CountTypeUsedInDocumentsAsync → Expected 1, Actual 2; повернено
- CI: CI 41410736839 ✓ success, Evidence 41410737018 ✓ success (head 8346cb0c3)
- Локально: build ☑, Architecture 1151/1151 (без StatusFromGit), Application Permit* 131, Infrastructure PermitType* 37, Api PermitUsages/OpenApi/PermitType* 13, vitest features/permits 1040/1040, tsc ☑
- UI сторінки типу поле ще не показує (лише контракт) — клієнтська lane окремо
- Опційне «Document.Delete для e2e-адміна»: пропущено — не тривіально (фікстура e2e не має очевидного місця на ≤20 рядків)
- Хто зводить: координатор
