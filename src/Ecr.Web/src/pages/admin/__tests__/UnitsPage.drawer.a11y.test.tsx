import { describe as suite, it, expect, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { describe as report, findViolations } from '@/test/a11y';
import { loadCatalog } from '@/shared/i18n';
import { routes } from '@/app/routes';
import { Catalog, FullAccessPermissions, ScanShell, Themes, createScanClient, settlePage } from '@/test/__tests__/a11yFixtures';
import { UnitsPage } from '../UnitsPage';

/**
 * Доступність довідника одиниць З ВІДКРИТОЮ шторкою (UI-21) в обох темах.
 *
 * ⚠ Шторка — у порталі, поза `container`: сканується `document.body`, інакше
 * «порушень немає» було б правдою про сам перелік, а не про шторку.
 *
 * ⚠ Мутаційний доказ (локально): кнопка коду без тексту (`{unit.code}` →
 * `''`) → `button-name` critical, обидві теми червоні.
 */
const units = [
  { id: 1, code: 'kg', dimensionId: 1, factorToBase: '1.0000000000', offsetToBase: '0.0000000000', dimensionCode: 'Mass' },
  { id: 2, code: 't', dimensionId: 1, factorToBase: '1000.0000000000', offsetToBase: '0.0000000000', dimensionCode: 'Mass' },
  { id: 4, code: 'degC', dimensionId: 2, factorToBase: '1.0000000000', offsetToBase: '273.1500000000', dimensionCode: 'Temperature' },
];

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: [...FullAccessPermissions, 'Uom.EditCatalog'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }
      if (url.endsWith('/api/v1/units/2/usage')) {
        return json({ total: 1, items: [{ kind: 'templateColumn', id: '7', label: 'Fuel.Mass', route: null }] });
      }
      if (url.endsWith('/api/v1/units/2')) {
        return json({ ...units[1], isBase: false, rowVersion: 'AAAAAAAAB9E=', symbolL10n: { en: 't' }, nameL10n: { en: 'Tonne' } });
      }
      if (url.endsWith('/api/v1/units')) return json(units);

      return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('Доступність шторки одиниці (%s)', (colorScheme) => {
  it('ФВ-14.16: /admin/units?panel=unit-2 не має порушень critical і serious', async () => {
    mockServer();
    await loadCatalog('en', 'private');
    await loadCatalog('en', 'public');

    const client = createScanClient();
    const route = {
      path: routes.adminUnits.path,
      entry: '/admin/units?panel=unit-2',
      content: async () => {
        await screen.findByText('Tonne', undefined, { timeout: 10_000 });
        return screen.findByText('Fuel.Mass', undefined, { timeout: 10_000 });
      },
    };
    render(
      <ScanShell colorScheme={colorScheme} client={client} route={route}>
        <UnitsPage />
      </ScanShell>,
    );

    await settlePage(client, route);
    const violations = await findViolations(document.body);

    expect(violations, report(violations)).toHaveLength(0);
  });
});
