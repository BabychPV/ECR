import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { PermitsPage } from '@/pages/admin/PermitsPage';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { FixtureTypes } from '../../types/__tests__/typesFixture';
import { FixturePermits } from './permitFixture';

/**
 * Доступність переліку дозволів (PM-07), майстра «New permit» і діалогів життєвого циклу в обох темах. Тексти
 * справжні, із `09-seed.sql`: ключ без тексту дав би `⟦ключ⟧`, і тест на це падає.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `aria-label` з `ActionIcon` меню рядка — axe червоніє на кнопці
 * без імені; прибрати `<legend>` у `TypePick` — червоніє група радіо без підпису.
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
  useSession: () => ({
    data: { permissions: ['Registry.View', 'Registry.EditData', 'Permit.EditType'], isSimulation: false },
  }),
}));

function stub(permits: readonly unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const items = String(input).startsWith('/api/v1/permit-types') ? FixtureTypes : permits;

      return new Response(JSON.stringify({ items, nextCursor: null, totalCount: items.length }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

/**
 * ⚠ Єдине свідоме виключення: Mantine кладе в `role="menu"` службовий `<div tabindex=-1 data-autofocus>`
 * (початковий фокус), і axe скаржиться на нього правилом `aria-required-children` у кожному відкритому
 * `Menu` продукту. Виключення вузьке: лише це правило, лише на вузлах `role="menu"`, і лише коли ВСІ інші діти
 * меню справді `menuitem` (перевіряється нижче). Усе решта лишається блокуючим.
 */
async function expectClean(root: HTMLElement): Promise<void> {
  expect(root.textContent).not.toContain('⟦');
  const violations = (await findViolations(root)).filter(
    (violation) => !(violation.id === 'aria-required-children' && violation.nodes.every((html) => html.includes('role="menu"'))),
  );
  expect(violations, describeViolations(violations)).toHaveLength(0);

  for (const menu of root.ownerDocument.querySelectorAll('[role="menu"]')) {
    const foreign = [...menu.children].filter(
      (child) => child.getAttribute('role') !== 'menuitem' && child.getAttribute('data-autofocus') !== 'true',
    );
    expect(foreign, 'у меню лише пункти й службовий елемент фокуса Mantine').toHaveLength(0);
  }
}

const text = (key: string): string => Catalog[key] ?? '';

describe('Перелік дозволів — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: перелік з даними й порожній стан без порушень і без ⟦ключів⟧`, async () => {
      stub(FixturePermits);
      const filled = render(
        <Shell colorScheme={colorScheme}>
          <PermitsPage />
        </Shell>,
      );
      await screen.findByText('KZ06VCZ14825472');
      await expectClean(filled.container);
      filled.unmount();

      stub([]);
      const empty = render(
        <Shell colorScheme={colorScheme}>
          <PermitsPage />
        </Shell>,
      );
      await screen.findByText(text('permits.empty.title'));
      await expectClean(empty.container);
    });

    it(`${colorScheme}: відкрите меню дій рядка без порушень`, async () => {
      stub(FixturePermits);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitsPage />
        </Shell>,
      );
      await screen.findByText('KZ06VCZ14825472');
      const row = within(screen.getByRole('table')).getAllByRole('row')[3]!;
      fireEvent.click(within(row).getByRole('button', { name: /Actions for permit/ }));
      await screen.findAllByRole('menuitem');

      await expectClean(document.body);
    });

    it(`${colorScheme}: майстер — типи, крок «Details» з помилкою і підсумок — без порушень`, async () => {
      stub(FixturePermits);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitsPage />
        </Shell>,
      );
      await screen.findByText('KZ06VCZ14825472');
      fireEvent.click(screen.getByRole('button', { name: text('permits.empty.new') }));
      // Майстер - ледачий чанк: у навантаженому CI 1 с findBy за замовчуванням замало (run 39337728834 і локальний 19/20).
      const lazy = { timeout: 10_000 };
      const dialog = await screen.findByRole('dialog', {}, lazy);
      await screen.findByTestId('permit-type-pick', {}, lazy);
      await expectClean(document.body);

      // PA-U-08: тип не передвибрано, без явного вибору «Next» стоїть на кроці «Type» (банер «type required»).
      const published = within(screen.getByTestId('permit-type-pick'))
        .getAllByRole('radio')
        .find((radio) => !(radio as HTMLInputElement).disabled);
      expect(published).toBeDefined();
      fireEvent.click(published!);
      fireEvent.click(screen.getByTestId('wizard-next'));
      // Ледачий чанк кроку «Details»: запас понад типові 1 с findBy.
      const number = await within(screen.getByRole('dialog')).findByRole('textbox', { name: /Permit number/ }, lazy);
      await screen.findByText(text('permits.list.wizard.validFrom'), {}, lazy);
      fireEvent.click(screen.getByTestId('wizard-next'));
      await screen.findByTestId('wizard-step-error', {}, lazy);
      await expectClean(document.body);

      fireEvent.change(number, { target: { value: 'KZ-77' } });
      fireEvent.click(screen.getByTestId('wizard-next'));
      await waitFor(() => expect(screen.getByTestId('wizard-summary')).toBeTruthy(), lazy);
      expect(dialog.textContent).not.toContain('⟦');
      await expectClean(document.body);
    });

    it(`${colorScheme}: діалоги активації та завершення без порушень`, async () => {
      stub(FixturePermits);
      render(
        <Shell colorScheme={colorScheme}>
          <PermitsPage />
        </Shell>,
      );
      await screen.findByText('KZ06VCZ14825472');

      const rows = within(screen.getByRole('table')).getAllByRole('row');
      fireEvent.click(within(rows[3]!).getByRole('button', { name: /Actions for permit/ }));
      fireEvent.click(await screen.findByRole('menuitem', { name: text('permits.list.action.activate') }));
      await screen.findByRole('dialog');
      await expectClean(document.body);
      fireEvent.click(screen.getByRole('button', { name: text('common.cancel') }));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

      fireEvent.click(within(rows[1]!).getByRole('button', { name: /Actions for permit/ }));
      fireEvent.click(await screen.findByRole('menuitem', { name: text('permits.list.action.end') }));
      await screen.findByRole('dialog');
      await expectClean(document.body);
    });
  }
});
