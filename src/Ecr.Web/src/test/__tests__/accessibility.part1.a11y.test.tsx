import { describe as suite, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import type { JSX } from 'react';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import { Shell, Themes, registerA11yFetchMock } from '@/test/__tests__/a11yFixtures';
import { LoginPage } from '@/pages/LoginPage';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { DocumentPage } from '@/pages/DocumentPage';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';
import { ExpressionsPage } from '@/pages/admin/ExpressionsPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 1 із 4
 * (`Q-254`).
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

    const { container } = render(
      <Shell colorScheme="light">
        <Page />
      </Shell>,
    );

    const hits = findKeyLikeText(container);

    expect(hits, describeHits(hits)).toHaveLength(0);
  });
});
