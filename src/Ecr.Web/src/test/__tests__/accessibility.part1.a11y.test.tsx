import { describe as suite, it, expect, beforeAll } from 'vitest';
import { render } from '@testing-library/react';
import type { JSX } from 'react';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import {
  Shell,
  Themes,
  createScanClient,
  registerA11yFetchMock,
  settleQueries,
} from '@/test/__tests__/a11yFixtures';
import { LoginPage } from '@/pages/LoginPage';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { DocumentPage } from '@/pages/DocumentPage';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';
import { ExpressionsPage } from '@/pages/admin/ExpressionsPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 1 із 4
 * (`Q-270`).
 *
 * ⛔ Цей файл — МЕХАНІЧНИЙ уламок колишнього єдиного
 * `accessibility.a11y.test.tsx` (24 маршрути × 2 схеми, один послідовний
 * прогін Vitest ~13-14 хв на тему). Розбиття на чотири файли по шість
 * маршрутів дає Vitest файли, які можна виконувати ПАРАЛЕЛЬНО (у межах
 * одного файлу `it.each` і далі йде послідовно — це паралелізм не змінює).
 * Тіла тестів, пороги й перелік перевірених маршрутів — дослівно ті самі,
 * що були в єдиному файлі; спільні приладдя (обгортка, заглушка мережі,
 * каталог рядків, профіль прав) винесені в `@/test/__tests__/a11yFixtures`, щоб
 * чотири копії не розійшлися одна з одною.
 *
 * ⚠ У цій частині — `/admin/expressions`: один із трьох найповільніших
 * маршрутів заміру 2026-09-07 (158-266 с самостійно, `vitest.a11y.config.ts`)
 * — навмисно розподілений по РІЗНИХ частинах (тут, у `part2` —
 * `/admin/registries`, у `part4` — `/_kitchen-sink`), щоб жодні два важкі
 * маршрути не опинилися в одному воркері одночасно й не звели нанівець
 * виграш від паралелізму (`maxWorkers: 2`, `vitest.a11y.config.ts`).
 */
const Pages: [string, () => JSX.Element][] = [
  ['/login', LoginPage],
  ['/change-password', ChangePasswordPage],
  ['/', DocumentsPage],
  ['/documents/1', DocumentPage],
  ['/admin/templates', TemplatesPage],
  ['/admin/expressions', ExpressionsPage],
];

registerA11yFetchMock();

/*
 * ⛔ Модуль сіток обчислюється ТУТ, а не тоді, коли його дотягне ефект
 * `DocumentPage`. Дві різні причини, обидві реальні.
 *
 * ПЛАВАЮЧЕ ПАДІННЯ ГЕЙТА. `DocumentPage` вантажить сітки динамічним
 *    `import()` в ефекті, а прапорець `alive` захищає лише стан —
 *    обчислення модуля скасувати не можна. Якщо воно припаде на момент,
 *    коли vitest уже прибрав середовище файлу, `window` уже видалено з
 *    `globalThis`, і складання Stencil усередині RevoGrid бере запасний
 *    шлях: `var win = typeof window !== "undefined" ? window : {}`. Далі
 *    `appDidLoad` шле `appload` на цей `{}` — і прогін падає з
 *    `TypeError: elm.dispatchEvent is not a function`, при тому що ВСІ
 *    перевірки доступності пройшли. Спіймано двічі 2026-09-18 (обидва рази
 *    саме цей файл, `Tests 48 passed` / `Errors 1 error`), другий раз — на
 *    PR, який не змінював нічого, крім коментаря.
 *
 * ⛔ ЧОГО ЦЕ НЕ ДАЄ, хоч і здавалося, що дає. Гіпотезу «доки імпорт не
 * розв'язано, `axe` перевіряє заглушку» ПЕРЕВІРЕНО І СПРОСТОВАНО: вміст
 * аркуша (`Table 1`) є в контейнері однаково — і з цим очікуванням, і без
 * нього, бо `findViolations` сам асинхронний і встигає. Тобто обсяг
 * перевіреного тут не змінився; змінилося лише те, що модуль більше не
 * обчислюється після кінця файлу.
 *
 * ⚠ ЦЕ НЕ ДОВЕДЕНО ВІДТВОРЕННЯМ. Падіння не вдалося викликати на цій машині
 * жодного разу: 10 повних прогонів, прогін із `maxWorkers: 2` як на раннері,
 * окремий тест із незавершеним `import()`. Правка спирається на МЕХАНІЗМ
 * (запасний шлях `win = {}` неможливий, доки модуль обчислено при живому
 * `window`) і на принцип «тест не має лишати роботу, яку сам почав, у
 * польоті» — а не на зелений прогін, який тут нічого не доводить.
 */
beforeAll(async () => {
  await import('@/features/grid/SheetTables');
});

suite.each(Themes)('Доступність маршрутів (%s)', (colorScheme) => {
  it.each(Pages)('ФВ-14.16: %s не має порушень critical і serious', async (_path, Page) => {
    const { container } = render(
      <Shell colorScheme={colorScheme}>
        <Page />
      </Shell>,
    );

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  });
});

suite('Технічні ключі на екрані', () => {
  it.each(Pages)('ФВ-14.9: %s показує людський текст, а не ключі', async (_path, Page) => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme="light" client={client}>
        <Page />
      </Shell>,
    );

    // ⚠ Див. `settleQueries`: без очікування сторож бачив лише те, що
    // малюється до першої відповіді. Неактивні вкладки з `keepMounted={false}`
    // не рендеряться й так — їхній вміст сторож не бачить за визначенням.
    await settleQueries(client);

    const hits = findKeyLikeText(container);

    expect(hits, describeHits(hits)).toHaveLength(0);
  });
});
