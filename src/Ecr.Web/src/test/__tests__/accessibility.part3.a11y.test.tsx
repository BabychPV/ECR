import { describe as suite, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import type { JSX } from 'react';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import { Shell, Themes, registerA11yFetchMock } from '@/test/__tests__/a11yFixtures';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { MappingPreviewPage } from '@/pages/admin/MappingPreviewPage';
import { JobsPage } from '@/pages/admin/JobsPage';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 3 із 4
 * (`Q-254`). Контекст і причина розбиття — див. `part1` цього ж набору.
 *
 * У цій частині немає жодного з трьох найповільніших маршрутів заміру
 * 2026-09-07 (вони розподілені по `part1`, `part2` і `part4`) — тому вона
 * очікувано найлегша з чотирьох; це не проблема балансування, а те, що
 * важких маршрутів усього три на 24.
 */
const Pages: [string, () => JSX.Element][] = [
  ['/admin/security', SecurityPage],
  ['/admin/periods', PeriodsPage],
  ['/admin/sources', SourcesPage],
  ['/admin/mapping', MappingPreviewPage],
  ['/admin/jobs', JobsPage],
  ['/admin/snapshots', SnapshotsPage],
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
