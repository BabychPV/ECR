import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState, type JSX } from 'react';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { Modal } from '@mantine/core';
import { renderWithMantine } from '@/test/render';
import { StrictDateInput } from '@/shared/dates/StrictDateInput';

/**
 * PA3-U-04: Esc у відкритому календарі закриває ЛИШЕ календар; діалог лишається. Другий Esc (календаря вже нема) закриває діалог.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати блок `data-mantine-stop-propagation` у `StrictDateInput` - перший Esc
 * закриває і діалог, тест червоний.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  language: () => 'en',
}));

afterEach(() => cleanup());

function Harness(): JSX.Element {
  const [open, setOpen] = useState(true);

  return (
    <Modal opened={open} onClose={() => setOpen(false)} title="Row" transitionProps={{ duration: 0 }}>
      <StrictDateInput label="Date" value={new Date('2026-10-05T00:00:00')} onChange={() => undefined} />
    </Modal>
  );
}

const calendarOpen = (): boolean => document.querySelector('[data-dates-dropdown]') !== null;

describe('StrictDateInput: Esc у модалці', () => {
  it('перший Esc закриває календар, діалог лишається; другий закриває діалог', async () => {
    renderWithMantine(<Harness />);
    const input = await screen.findByLabelText('Date');
    input.focus();
    fireEvent.focus(input);
    fireEvent.click(input);
    await waitFor(() => expect(calendarOpen()).toBe(true));
    fireEvent.keyDown(input, { key: 'Escape' });
    await waitFor(() => expect(calendarOpen()).toBe(false));
    // Дати window-слухачу модалки відпрацювати, а мітці - зійти.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole('dialog')).not.toBeNull();

    fireEvent.keyDown(input, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });
});
