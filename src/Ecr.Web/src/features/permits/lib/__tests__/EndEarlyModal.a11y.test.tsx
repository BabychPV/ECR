import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { EndEarlyModal } from '../EndEarlyModal';

/**
 * KI-04, доступність діалогу «End early» з підказкою мінімальної довжини причини в обох темах. Тексти - справжні, із
 * `09-seed.sql` (ключ без рядка сіду дав би `⟦ключ⟧`, і тест на це падає).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(cleanup);

describe('End early: підказка довжини причини - доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: підказка є й зв'язана з полем, без порушень і без ⟦ключів⟧`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <EndEarlyModal
            opened
            title="End permit early"
            label="Reason"
            description="Sheets stop reading it from today."
            confirmLabel="End permit"
            minLength={10}
            isPending={false}
            error={null}
            onEdit={vi.fn()}
            returnFocusTo={() => null}
            onConfirm={vi.fn()}
            onClose={vi.fn()}
          />
        </Shell>,
      );

      const dialog = await screen.findByRole('dialog');
      expect(dialog.textContent).toContain('Enter at least 10 characters.');
      expect(dialog.textContent).not.toContain('⟦');
      const field = screen.getByRole('textbox', { name: /Reason/ });
      expect(field.getAttribute('aria-describedby')).toMatch(/-description/);

      const violations = await findViolations(dialog);
      expect(violations, describeViolations(violations)).toHaveLength(0);

      fireEvent.change(field, { target: { value: '1234567890' } });
      expect(dialog.textContent).not.toContain('Enter at least');
      // Зникнення підказки оголошує живий регіон (role=status), а не лишається німим; текст - справжній ключ сіду.
      const status = screen.getByRole('status');
      expect(status.textContent).toBe('The reason is long enough.');
      expect(status.getAttribute('aria-live')).toBe('polite');
      const afterViolations = await findViolations(dialog);
      expect(afterViolations, describeViolations(afterViolations)).toHaveLength(0);
    });
  }
});
