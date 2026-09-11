import { describe as suite, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import type { JSX } from 'react';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import { Shell, Themes, registerA11yFetchMock } from '@/test/__tests__/a11yFixtures';
import { AuditPage } from '@/pages/admin/AuditPage';
import { UnitsPage } from '@/pages/admin/UnitsPage';
import { UiStringsPage } from '@/pages/admin/UiStringsPage';
import { HealthPage } from '@/pages/admin/HealthPage';
import { MyGroupsPage } from '@/pages/MyGroupsPage';
import { KitchenSinkPage } from '@/pages/KitchenSinkPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 4 із 4
 * (`Q-254`). Контекст і причина розбиття — див. `part1` цього ж набору.
 *
 * ⚠ У цій частині — `/_kitchen-sink`: один із трьох найповільніших маршрутів
 * заміру 2026-09-07 (205 с самостійно), навмисно розподілений окремо від
 * `/admin/expressions` (`part1`) і `/admin/registries` (`part2`).
 */
const Pages: [string, () => JSX.Element][] = [
  ['/admin/audit', AuditPage],
  ['/admin/units', UnitsPage],
  ['/admin/ui-strings', UiStringsPage],
  ['/admin/health', HealthPage],
  ['/my-groups', MyGroupsPage],
  ['/_kitchen-sink', KitchenSinkPage],
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
