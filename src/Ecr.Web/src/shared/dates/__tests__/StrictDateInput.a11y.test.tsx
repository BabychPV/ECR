import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { StrictDateInput } from '@/shared/dates/StrictDateInput';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * P2-N1: відкритий календар із днем-вихідним (2026-10-10 - субота) без блокуючих порушень в обох темах.
 *
 * ⚠ `color-contrast` у jsdom не рахується - числа контрасту доводить `dateInput.contrast.test.ts`; тут перевіряємо, що
 * вихідний день є в календарі (`button[data-weekend]`), а решта правил axe проходить. У браузері axe треба повторити.
 */
afterEach(() => cleanup());

describe('StrictDateInput: календар - доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: відкритий календар із вихідним днем без порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <StrictDateInput label="Valid from" value={new Date('2026-10-10T00:00:00')} onChange={() => undefined} />
        </Shell>,
      );
      const input = screen.getByLabelText('Valid from');
      input.focus();
      fireEvent.focus(input);
      fireEvent.click(input);
      await waitFor(() => expect(document.querySelector('[data-dates-dropdown]')).not.toBeNull());
      expect(document.querySelector('button[data-weekend]')).not.toBeNull();

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
