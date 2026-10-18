import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { PermitTypesPage } from '@/pages/admin/PermitTypesPage';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { FixtureTypes } from './typesFixture';

/**
 * Доступність переліку типів пермітів і майстра «New permit type» (PM-08a) в обох темах. Тексти — справжні, із
 * `09-seed.sql`: ключ без тексту дав би `⟦ключ⟧`, і тест на це падає.
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
  useSession: () => ({ data: { permissions: ['Registry.View', 'Registry.EditDefinition'], isSimulation: false } }),
}));

function stubList(items: readonly unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response(JSON.stringify({ items, nextCursor: null, totalCount: items.length }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    ),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Типи пермітів — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: перелік з даними без блокуючих порушень і без ⟦ключів⟧`, async () => {
      stubList(FixtureTypes);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitTypesPage />
        </Shell>,
      );
      await screen.findByText('Air emission permit');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: порожній стан без порушень`, async () => {
      stubList([]);
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitTypesPage />
        </Shell>,
      );
      await screen.findByText(Catalog['permits.types.empty.title'] ?? '');

      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: майстер — крок Basics з помилкою поля і підсумок — без порушень`, async () => {
      stubList(FixtureTypes);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitTypesPage />
        </Shell>,
      );
      await screen.findByText('Air emission permit');
      fireEvent.click(screen.getByRole('button', { name: Catalog['permits.types.new'] ?? '' }));
      const dialog = await screen.findByRole('dialog');

      fireEvent.click(await screen.findByTestId('wizard-next'));
      await screen.findByTestId('wizard-step-error');
      expect(dialog.textContent).not.toContain('⟦');
      let violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);

      fireEvent.change(screen.getByRole('textbox', { name: /Name/ }), { target: { value: 'Noise permit' } });
      fireEvent.click(screen.getByTestId('wizard-next'));
      await waitFor(() => expect(screen.getByTestId('wizard-summary')).toBeTruthy());
      expect(dialog.textContent).not.toContain('⟦');
      violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
