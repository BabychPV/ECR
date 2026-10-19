import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { PermitBindingPage } from '@/pages/admin/PermitBindingPage';
import { PermitBindingsPage } from '@/pages/admin/PermitBindingsPage';
import { Catalog, RouteShell, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { binding, envelope, json, template, version } from './bindingsFixture';

/**
 * Доступність переліку й екрана прив'язки в обох темах. Тексти - справжні, із `09-seed.sql`: ключ без тексту дав би
 * `⟦ключ⟧`, і тест на це падає.
 */
// ⛔ `Catalog` читається під час виклику `t`, а не в фабриці: див. `PermitDefTable.a11y.test.tsx`.
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Template.View', 'Registry.View'], isSimulation: false } }),
}));

function stand(withBindings: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/templates?limit=100')) {
        return json({ items: [template(7, 'GEN07', 'General template')], nextCursor: null, totalCount: 1 });
      }
      if (url.includes('/api/v1/templates/versions?')) {
        return json([{ templateId: 7, versions: [version(70, 'Published'), version(71, 'Draft', '1.1.0.0')] }]);
      }
      if (/\/api\/v1\/templates\/7$/.test(url)) {
        return json({ id: 7, code: 'GEN07', nameL10n: { values: { en: 'General template' } }, isActive: true });
      }
      if (/template-versions\/70\/permit-bindings$/.test(url)) {
        return json(
          withBindings
            ? envelope(binding('B1'), binding('B2', { mode: 'Formulas', selectionRule: 'FromHeaderLookup', isActive: false }))
            : envelope(),
        );
      }
      if (/permit-bindings\/B1$/.test(url)) return json(binding('B1'));

      return json(envelope());
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Прив’язки аркушів — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: перелік з даними без блокуючих порушень і без ⟦ключів⟧`, async () => {
      stand(true);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitBindingsPage />
        </Shell>,
      );
      await screen.findByRole('link', { name: 'TBL_B1' });
      await screen.findByText(/Checked 1 of 1 templates/);

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: порожній перелік без порушень`, async () => {
      stand(false);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitBindingsPage />
        </Shell>,
      );
      await screen.findByText(Catalog['permits.bindingsList.empty.title'] ?? '');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: екран прив’язки з таблицею колонок без порушень`, async () => {
      stand(true);
      const { container } = render(
        <RouteShell
          colorScheme={colorScheme}
          path="/admin/permits/bindings/:id"
          entry="/admin/permits/bindings/B1?template=7&version=70"
        >
          <PermitBindingPage />
        </RouteShell>,
      );
      await screen.findByRole('table');
      await screen.findByText('General template');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
