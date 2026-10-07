import { describe as suite, it, expect, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { describe as report, findViolations } from '@/test/a11y';
import { loadCatalog } from '@/shared/i18n';
import { routes } from '@/app/routes';
import { Catalog, FullAccessPermissions, ScanShell, Themes, createScanClient, settlePage } from '@/test/__tests__/a11yFixtures';
import { ConsistencyIssuesPage } from '../ConsistencyIssuesPage';

/**
 * Доступність журналу узгодженості ЗІ смугою й відкритою шторкою (UI-20) в
 * обох темах.
 *
 * ⚠ Шторка — у порталі, поза `container`: сканується `document.body`.
 *
 * ⚠ Мутаційний доказ (локально): кнопка знахідки без тексту
 * (`{issue.message}` → `''`) → `button-name` critical, обидві теми червоні.
 */
const findings = [1, 2, 3].map((id) => ({
  id,
  detectedAt: '2026-10-05T21:00:00Z',
  severity: id,
  ruleCode: `BROKEN_FK_${String(id)}`,
  entityType: 'doc.TableRow',
  entityId: 4000 + id,
  message: `Row ${String(4000 + id)} refers to a table instance that does not exist.`,
  resolvedAt: null,
  resolvedByUserId: null,
}));

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
          permissions: FullAccessPermissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }
      if (url.includes('/api/v1/consistency/issues')) return json({ items: findings, nextCursor: null, totalCount: null });

      return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('Доступність шторки знахідки (%s)', (colorScheme) => {
  it('ФВ-14.16: /admin/consistency?panel=issue-2 не має порушень critical і serious', async () => {
    mockServer();
    await loadCatalog('en', 'private');
    await loadCatalog('en', 'public');

    const client = createScanClient();
    const route = {
      path: routes.adminConsistency.path,
      entry: '/admin/consistency?panel=issue-2',
      content: () => screen.findByRole('dialog', undefined, { timeout: 10_000 }),
    };
    render(
      <ScanShell colorScheme={colorScheme} client={client} route={route}>
        <ConsistencyIssuesPage />
      </ScanShell>,
    );

    await settlePage(client, route);
    await screen.findAllByText('doc.TableRow · 4002', undefined, { timeout: 10_000 });
    const violations = await findViolations(document.body);

    expect(violations, report(violations)).toHaveLength(0);
  });
});
