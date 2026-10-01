import { describe as suite, it, expect, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { describe as report, findViolations } from '@/test/a11y';
import { loadCatalog } from '@/shared/i18n';
import { routes } from '@/app/routes';
import { ScanShell, Themes, createScanClient, settlePage } from '@/test/__tests__/a11yFixtures';
import { RegistryDataPage } from '../RegistryDataPage';
import { mockServer } from './fixtures';

/**
 * Доступність редактора даних довідника (`ФВ-8.12`, §8.9) в обох темах.
 *
 * ⚠ Сканується екран З ДАНИМИ (сітка з рядками й комірками `Lookup`), а не скелет: `content`
 * чекає на `role="grid"`, інакше «порушень немає» було б правдою і для порожнього екрана.
 *
 * ⚠ Мутаційний доказ (2026-09-30): `role="gridcel"` на комірках → `aria-roles` critical, обидві теми червоні.
 */
afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('Доступність даних довідника (%s)', (colorScheme) => {
  it('ФВ-14.16: /admin/registries/:code/entries не має порушень critical і serious', async () => {
    mockServer();
    await loadCatalog('en', 'private');
    await loadCatalog('en', 'public');

    const client = createScanClient();
    const route = {
      path: routes.adminRegistryData.path,
      entry: '/admin/registries/STREAM_CASE/entries',
      content: () => screen.findByRole('grid', undefined, { timeout: 10_000 }),
    };
    const { container } = render(
      <ScanShell colorScheme={colorScheme} client={client} route={route}>
        <RegistryDataPage />
      </ScanShell>,
    );

    await settlePage(client, route);
    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  });
});
