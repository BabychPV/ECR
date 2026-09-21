import { describe as suite, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { JSX } from 'react';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import { routes } from '@/app/routes';
import {
  RegistryDefinitionName,
  ScanShell,
  Themes,
  createScanClient,
  registerA11yFetchMock,
  settlePage,
  type ParamRoute,
} from '@/test/__tests__/a11yFixtures';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { TableRelationsPage } from '@/pages/admin/TableRelationsPage';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 2 із 4
 * (`Q-270`). Контекст і причина розбиття — див. `part1` цього ж набору.
 *
 * ⚠ У цій частині — `/admin/registries`: один із трьох найповільніших
 * маршрутів заміру 2026-09-07 (189 с у повному прогоні), навмисно
 * розподілений окремо від `/admin/expressions` (`part1`) і `/_kitchen-sink`
 * (`part4`).
 */
/*
 * ⛔ П'ять із шести маршрутів тут — З ПАРАМЕТРОМ, і третій елемент рядка
 * ({@link ParamRoute}) не прикраса. До нього сторінки рендерилися під голим
 * `Shell` на `/`: `useParams()` порожній, запит або вимкнений
 * (`TableRelationsPage`, `MethodologyVersionsPage` — `enabled: known`), або
 * летить на `NaN` і отримує порожню відповідь. Axe і сторож ключів сканували
 * `EmptyState` — і були зелені, бо на порожньому екрані порушувати нічого.
 * `content` доводить протилежне: без даних на екрані тест падає, а не мовчить.
 */
const Wait = { timeout: 10_000 };

const Pages: [string, () => JSX.Element, ParamRoute?][] = [
  [
    '/admin/templates/1/versions/1',
    TemplateVersionPage,
    {
      path: routes.adminTemplateVersion.path,
      entry: '/admin/templates/1/versions/1',
      content: () => screen.findByText('General sheet', undefined, Wait),
    },
  ],
  [
    '/admin/templates/1/versions/1/relations',
    TableRelationsPage,
    {
      path: routes.adminTemplateVersionRelations.path,
      entry: '/admin/templates/1/versions/1/relations',
      content: () => screen.findByText('ROLLUP-T1', undefined, Wait),
    },
  ],
  ['/admin/registries', RegistriesPage],
  [
    '/admin/registries/:code/definition',
    RegistryConstructorPage,
    {
      path: routes.adminRegistryDefinition.path,
      entry: '/admin/registries/FUEL/definition',
      content: () => screen.findByText(RegistryDefinitionName, undefined, Wait),
    },
  ],
  ['/admin/methodologies', MethodologiesPage],
  [
    '/admin/methodologies/1/versions',
    MethodologyVersionsPage,
    {
      path: routes.adminMethodologyVersions.path,
      entry: '/admin/methodologies/1/versions',
      content: () => screen.findByText('2026.1', undefined, Wait),
    },
  ],
];

registerA11yFetchMock();

suite.each(Themes)('Доступність маршрутів (%s)', (colorScheme) => {
  it.each(Pages)('ФВ-14.16: %s не має порушень critical і serious', async (_path, Page, route) => {
    const client = createScanClient();
    const { container } = render(
      <ScanShell colorScheme={colorScheme} client={client} route={route}>
        <Page />
      </ScanShell>,
    );

    // ⚠ Той самий сліпий кут, що й у ФВ-14.9: без очікування axe бачив лише
    // те, що малюється до першої відповіді (див. `settleQueries`).
    await settlePage(client, route);

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  });
});

suite('Технічні ключі на екрані', () => {
  it.each(Pages)('ФВ-14.9: %s показує людський текст, а не ключі', async (_path, Page, route) => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const client = createScanClient();
    const { container } = render(
      <ScanShell colorScheme="light" client={client} route={route}>
        <Page />
      </ScanShell>,
    );

    // ⚠ Див. `settleQueries`: без очікування сторож бачив лише те, що
    // малюється до першої відповіді. Неактивні вкладки з `keepMounted={false}`
    // не рендеряться й так — їхній вміст сторож не бачить за визначенням.
    await settlePage(client, route);

    const hits = findKeyLikeText(container);

    expect(hits, describeHits(hits)).toHaveLength(0);
  });
});
