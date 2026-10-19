import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { PermitListsPage } from '@/pages/admin/PermitListsPage';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { column, permitType, table } from '../../typeEditor/__tests__/fixtures';
import { makeType } from '../../types/__tests__/typesFixture';

/**
 * Доступність екрана «Lists» (PM-08d) в обох темах. Тексти — справжні, із `09-seed.sql`: ключ без тексту
 * дав би `⟦ключ⟧`, і тест на це падає (так ловиться забутий рядок сіду).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Permit.EditType'], isSimulation: false } }),
}));

const air = permitType({
  code: 'Air',
  nameL10n: { en: 'Air permit' },
  headerFields: [],
  tables: [
    table('Emission', [
      column('Substance', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'SUBSTANCES' }),
      column('Method', { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['Direct', 'Calc'] }),
      column('Kind', { dataType: 'Lookup', listScope: 'SamePermitTable', siblingTableCode: 'Sources' }),
    ]),
  ],
});

function stub(types: readonly unknown[]): void {
  const json = (body: unknown): Response =>
    new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/entries')) return json([{ id: 1, code: 'NOX', display: 'Nitrogen oxides', parentEntryId: null, validFrom: null, validTo: null }]);
      if (url.includes('/api/v1/registries')) {
        return json([{ code: 'SUBSTANCES', nameL10n: { values: { en: 'Substances' } }, entryCount: 42, fields: [] }]);
      }
      if (url.includes('/api/v1/permit-types/')) return json(air);

      return json({ items: types, nextCursor: null, totalCount: types.length });
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Списки вибору пермітів — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: перелік з даними без блокуючих порушень і без ⟦ключів⟧`, async () => {
      stub([makeType('Air')]);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitListsPage />
        </Shell>,
      );
      await screen.findByText('Substances');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: порожній стан без порушень`, async () => {
      stub([]);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitListsPage />
        </Shell>,
      );
      await screen.findByText(Catalog['permits.lists.empty.title'] ?? '');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
