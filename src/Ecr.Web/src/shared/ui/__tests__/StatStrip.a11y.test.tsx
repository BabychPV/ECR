import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { ListPage } from '@/shared/ui/ListPage';
import type { StatStripItems } from '@/shared/ui/StatStrip';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Доступність смуги показників і шаблону переліку в ОБОХ темах (`ФВ-14.16`,
 * `D-127`).
 *
 * ⛔ Окремий файл із суфіксом `.a11y.`, бо `axe` живе у своєму прогоні
 * (`vitest.a11y.config.ts`): базовий конфіг такі файли виключає, щоб `npm
 * test` не тягнув `axe` на кожен запуск. Тобто ця перевірка йде в гейтах
 * `a11y (dark)` і `a11y (light)`, а не в `client`.
 *
 * ⚠ Перевіряється саме шаблон із СМУГОЮ всередині, а не смуга окремо: `L4`
 * дозволяє чотири кнопки-тумблери поспіль, і питання «чи має кожна з них
 * доступне ім'я і чи не вкладені вони одна в одну» має сенс лише в тій
 * розмітці, у якій вони стоять на екрані.
 */

const four: StatStripItems = [
  { id: 'running', label: 'running', value: 3, hint: 'Filter the list: running' },
  { id: 'queued', label: 'queued', value: 12 },
  { id: 'filled', label: 'sheets filled', value: 48, of: 60 },
  { id: 'issues', label: 'validation errors', value: 7, tone: 'danger' },
];

afterEach(cleanup);

describe('StatStrip і ListPage — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: смуга з чотирьох показників, кожен — тумблер', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <ListPage
          header={{ title: 'Jobs', meta: 'Background work of this project' }}
          stats={{ label: 'Summary', items: four, active: 'issues', onSelect: () => {} }}
          filters={<div>filters</div>}
          table={<div>table</div>}
          detail={{ panelId: 'J-1', title: 'J-1', closeLabel: 'Close details' }}
        />
      </Shell>,
    );

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('тема %s: смуга без обробника — самі цифри', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <ListPage
          header={{ title: 'Documents' }}
          stats={{ label: 'Campaign summary', items: four }}
          table={<div>table</div>}
        />
      </Shell>,
    );

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
