import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReasonModal } from '@/shared/ui/ReasonModal';

/**
 * CL-22 (B-8): порядок Tab у діалогах дорівнює візуальному - «Close (x)» у правому верхньому куті стоїть у DOM ПЕРЕД
 * кнопками дій внизу («Cancel», «Confirm»), а не після них.
 */
afterEach(cleanup);

describe('ReasonModal: порядок Tab', () => {
  it('хрестик закриття в DOM раніше за «Cancel» і кнопку підтвердження', () => {
    render(
      <MantineProvider>
        <ReasonModal
          opened
          title="Return for edits"
          label="Reason"
          confirmLabel="Return"
          onConfirm={vi.fn()}
          onClose={vi.fn()}
        />
      </MantineProvider>,
    );

    const dialog = screen.getByRole('dialog');
    const buttons = Array.from(dialog.querySelectorAll('button'));
    const close = buttons.find((button) => button.classList.contains('mantine-Modal-close'));
    const cancel = buttons.find((button) => /cancel/i.test(button.textContent ?? ''));
    const confirm = buttons.find((button) => button.textContent === 'Return');

    expect(close, 'у діалозі немає хрестика закриття').toBeDefined();
    expect(cancel, 'у діалозі немає «Cancel»').toBeDefined();
    expect(confirm, 'у діалозі немає кнопки підтвердження').toBeDefined();
    expect(buttons.indexOf(close as HTMLButtonElement)).toBeLessThan(buttons.indexOf(cancel as HTMLButtonElement));
    expect(buttons.indexOf(cancel as HTMLButtonElement)).toBeLessThan(buttons.indexOf(confirm as HTMLButtonElement));
  });
});
