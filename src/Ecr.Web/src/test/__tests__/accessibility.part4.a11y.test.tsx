import { describe as suite, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { JSX } from 'react';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import {
  RouteShell,
  Shell,
  Themes,
  createScanClient,
  registerA11yFetchMock,
  settleQueries,
} from '@/test/__tests__/a11yFixtures';
import { TemplateCardPage } from '@/pages/admin/TemplateCardPage';
import { AuditPage } from '@/pages/admin/AuditPage';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';
import { UnitsPage } from '@/pages/admin/UnitsPage';
import { UiStringsPage } from '@/pages/admin/UiStringsPage';
import { HealthPage } from '@/pages/admin/HealthPage';
import { MyGroupsPage } from '@/pages/MyGroupsPage';
import { KitchenSinkPage } from '@/pages/KitchenSinkPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 4 із 4
 * (`Q-270`). Контекст і причина розбиття — див. `part1` цього ж набору.
 *
 * ⚠ У цій частині — `/_kitchen-sink`: один із трьох найповільніших маршрутів
 * заміру 2026-09-07 (205 с самостійно), навмисно розподілений окремо від
 * `/admin/expressions` (`part1`) і `/admin/registries` (`part2`).
 */
const Pages: [string, () => JSX.Element][] = [
  ['/admin/audit', AuditPage],
  ['/admin/consistency', ConsistencyIssuesPage],
  ['/admin/units', UnitsPage],
  ['/admin/ui-strings', UiStringsPage],
  ['/admin/health', HealthPage],
  ['/my-groups', MyGroupsPage],
  ['/_kitchen-sink', KitchenSinkPage],
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

/**
 * Картка шаблону (`UI-09`) — окремим блоком, а не рядком у `Pages` вище.
 *
 * ⛔ Причина не в оформленні: це перший маршрут набору, чий зміст цілком
 * залежить від `useParams().id`. Під голим `Shell` (`MemoryRouter` на `/`)
 * параметра немає, запит вимкнено, і axe сканував би скелет — перевірка
 * виглядала б повною й не була б нею. `RouteShell` дає той самий провайдер,
 * ту саму заглушку мережі й ТОЙ САМИЙ каталог, але на справжній адресі.
 */
const TemplateCardPath = '/admin/templates/:id';
const TemplateCardEntry = '/admin/templates/1';

suite.each(Themes)('Доступність картки шаблону (%s)', (colorScheme) => {
  it(`ФВ-14.16: ${TemplateCardPath} не має порушень critical і serious`, async () => {
    const client = createScanClient();
    const { container } = render(
      <RouteShell
        colorScheme={colorScheme}
        path={TemplateCardPath}
        entry={TemplateCardEntry}
        client={client}
      >
        <TemplateCardPage />
      </RouteShell>,
    );

    await screen.findByRole('heading', { name: 'Stationary sources' });
    await settleQueries(client);

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  });
});

suite('Технічні ключі на картці шаблону', () => {
  it(`ФВ-14.9: ${TemplateCardPath} показує людський текст, а не ключі`, async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const client = createScanClient();
    const { container } = render(
      <RouteShell
        colorScheme="light"
        path={TemplateCardPath}
        entry={TemplateCardEntry}
        client={client}
      >
        <TemplateCardPage />
      </RouteShell>,
    );

    // ⚠ Чекаємо на ДАНІ, а не на рендер: ключі шапки й переліку пар
    // з'являються лише після відповіді, і перевірка до неї дивилася б на
    // скелет — тобто мовчки нічого не перевіряла б.
    await screen.findByRole('heading', { name: 'Stationary sources' });

    // ⚠ Заголовок доводить лише прихід картки, не сесії: елементи під правом
    // (`/api/v1/me`) могли ще не намалюватися. Див. `settleQueries`.
    await settleQueries(client);

    const hits = findKeyLikeText(container);

    expect(hits, describeHits(hits)).toHaveLength(0);
  });
});
