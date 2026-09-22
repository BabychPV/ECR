import { describe as suite, it, expect } from 'vitest';
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
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { MappingPreviewPage } from '@/pages/admin/MappingPreviewPage';
import { JobsPage } from '@/pages/admin/JobsPage';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { NotificationsPage } from '@/pages/admin/NotificationsPage';
import { CampaignOverviewPage } from '@/pages/admin/CampaignOverviewPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 3 із 4
 * (`Q-270`). Контекст і причина розбиття — див. `part1` цього ж набору.
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

  // ⚠ Новий маршрут (`BE-33`) додано саме сюди: ця частина найлегша, і жодного
  // з трьох найповільніших маршрутів у ній немає.
  ['/admin/notifications', NotificationsPage],

  // ⚠ Огляд кампанії (`BE-22`) — теж сюди, з тієї самої причини. Фікстура
  // обрізана стелею (`a11yFixtures.tsx`, `CampaignSummaryFixture`), тож axe
  // сканує і банер усічення, і смугу, і перелік відстаючих.
  ['/admin/campaign', CampaignOverviewPage],
];

registerA11yFetchMock();

suite.each(Themes)('Доступність маршрутів (%s)', (colorScheme) => {
  it.each(Pages)('ФВ-14.16: %s не має порушень critical і serious', async (_path, Page) => {
    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={colorScheme} client={client}>
        <Page />
      </Shell>,
    );

    // ⚠ Той самий сліпий кут, що й у ФВ-14.9: без очікування axe бачив лише
    // те, що малюється до першої відповіді (див. `settleQueries`).
    await settleQueries(client);

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
