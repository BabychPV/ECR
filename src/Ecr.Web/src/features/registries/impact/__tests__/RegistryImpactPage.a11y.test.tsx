import { afterEach, describe as suite, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { describe as report, findViolations } from '@/test/a11y';
import { RouteShell, Themes, createScanClient, settleQueries } from '@/test/__tests__/a11yFixtures';
import { routes } from '@/app/routes';
import { RegistryImpactPage } from '../RegistryImpactPage';

/** WCAG 2.1 AA сторінки «Вплив правки довідника» (RT-25): перелік з позначкою `truncated` і кнопкою. */

function stubServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.endsWith('/api/v1/me')
        ? {
            userId: 1,
            userName: 'admin',
            language: 'en',
            permissions: ['Registry.View', 'Calculation.Recalculate'],
            isSimulation: false,
            denies: [],
            grants: {},
            mustChangePassword: false,
            simulatedForUserId: null,
          }
        : {
            items: [
              { documentId: 5, businessKey: 'DOC-5', periodKey: 202609, periodState: 'Open', via: ['methodology:M1'] },
              { documentId: 6, businessKey: 'DOC-6', periodKey: 202609, periodState: 'Grace', via: ['methodology:M2'] },
            ],
            total: 2,
            truncated: true,
          };

      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('Доступність сторінки впливу правки довідника (%s)', (colorScheme) => {
  it('RT-25: перелік без порушень critical і serious', async () => {
    stubServer();
    const client = createScanClient();
    const { container } = render(
      <RouteShell
        colorScheme={colorScheme}
        client={client}
        path={routes.adminRegistryImpact.path}
        entry="/admin/registries/SUBST/impact"
      >
        <RegistryImpactPage />
      </RouteShell>,
    );

    await screen.findByText('DOC-6', undefined, { timeout: 10_000 });
    await settleQueries(client);

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  });
});
