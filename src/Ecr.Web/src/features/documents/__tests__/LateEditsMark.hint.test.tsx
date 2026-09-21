import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { LateEditsMark } from '@/features/documents/LateEditsMark';
import { testTheme } from '@/test/render';

/**
 * Позначка «пізні правки» пояснює себе з клавіатури (`BE-09b`).
 *
 * ⛔ Раніше це був голий текстовий бейдж: що саме означає «пізні», не казало
 * ніщо, а з фокуса — тим паче. Тут перевіряється не наявність `<Hint>`, а те,
 * що бачить людина: Tab доходить до бейджа, фокус відкриває `role="tooltip"`
 * з поясненням, і те саме пояснення — опис бейджа для читалки.
 *
 * ⚠ Рядки — ключами в `⟦…⟧`: каталогу в цьому тесті немає.
 */
describe('LateEditsMark: підказка доступна з фокуса', () => {
  it('Tab доводить до бейджа, фокус відкриває підказку, опис — те саме пояснення', async () => {
    render(
      <MantineProvider theme={testTheme}>
        <button type="button">before</button>
        <LateEditsMark />
      </MantineProvider>,
    );

    const hint = '⟦documents.lateEditsHint⟧';
    const badge = screen.getByText('⟦documents.lateEdits⟧').closest('[data-late-edits]');
    expect(badge).not.toBeNull();

    const user = userEvent.setup();
    await user.tab();
    await user.tab();
    expect(document.activeElement).toBe(badge);

    expect((await screen.findByRole('tooltip')).textContent).toBe(hint);
    expect(screen.getAllByRole('generic', { description: hint })).toContain(badge);
  });
});
