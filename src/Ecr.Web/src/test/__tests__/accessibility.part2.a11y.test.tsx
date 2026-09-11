import { describe as suite, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import type { JSX } from 'react';
import { describe as report, findViolations } from '@/test/a11y';
import { describeHits, findKeyLikeText } from '@/test/keyLikeText';
import { loadCatalog } from '@/shared/i18n';
import { Shell, Themes, registerA11yFetchMock } from '@/test/__tests__/a11yFixtures';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { TableRelationsPage } from '@/pages/admin/TableRelationsPage';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { MethodologyVersionsPage } from '@/pages/admin/MethodologyVersionsPage';

/**
 * WCAG 2.1 AA на кожному маршруті (`ФВ-14.16`, `D-127`) — частина 2 із 4
 * (`Q-254`). Контекст і причина розбиття — див. `part1` цього ж набору.
 *
 * ⚠ У цій частині — `/admin/registries`: один із трьох найповільніших
 * маршрутів заміру 2026-09-07 (189 с у повному прогоні), навмисно
 * розподілений окремо від `/admin/expressions` (`part1`) і `/_kitchen-sink`
 * (`part4`).
 */
const Pages: [string, () => JSX.Element][] = [
  ['/admin/templates/1/versions/1', TemplateVersionPage],
  ['/admin/templates/1/versions/1/relations', TableRelationsPage],
  ['/admin/registries', RegistriesPage],
  ['/admin/registries/:code/definition', RegistryConstructorPage],
  ['/admin/methodologies', MethodologiesPage],
  ['/admin/methodologies/1/versions', MethodologyVersionsPage],
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
