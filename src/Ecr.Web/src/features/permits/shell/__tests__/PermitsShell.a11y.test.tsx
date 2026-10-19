import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { PermitEditorHead, PermitStateStepper } from '../PermitEditorHead';
import { PermitStateBadge } from '../../ui/PermitStateBadge';
import { PermitsPage } from '@/pages/admin/PermitsPage';
import { PermitListsPage } from '@/pages/admin/PermitListsPage';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Доступність каркаса Permits (PM-06) в обох темах: сторінки розділів з порожнім станом і
 * недоступними діями, шапка редактора, лінія станів, бейдж. Тексти — справжні, із `09-seed.sql`.
 */
// ⛔ `Catalog` читається під час виклику `t`, а не в фабриці: див. `PermitDefTable.a11y.test.tsx`.
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

// PM-06b: смуга розділів читає права з профілю; сканується вигляд редактора типів (усі чотири посилання).
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Permit.EditType'] } }),
}));

afterEach(cleanup);

describe('Permits: каркас — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: сторінки-каркаси розділів без блокуючих порушень і без ⟦ключів⟧`, async () => {
      for (const page of [<PermitsPage key="p" />, <PermitListsPage key="l" />]) {
        const { container, unmount } = render(<Shell colorScheme={colorScheme}>{page}</Shell>);
        expect(container.textContent).not.toContain('⟦');
        const violations = await findViolations(container);
        expect(violations, describeViolations(violations)).toHaveLength(0);
        unmount();
      }
    });

    it(`${colorScheme}: шапка редактора з бейджем, станами й діями`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitEditorHead
            back={{ label: 'Back to Permits', href: '/admin/permits' }}
            title="KZ06VCZ14825472"
            mono
            subtitle="Air emission permit · Offshore complex"
            badge={<PermitStateBadge state="Active" />}
            progress={<PermitStateStepper state="Active" />}
            actions={<button type="button">Renew</button>}
          />
        </Shell>,
      );
      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
