import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { IdleWarningDialog } from '../IdleSignOut';

/**
 * D-471: попередження перед виходом — `alertdialog`, фокус на «Stay signed in», видимий відлік, Escape = лишитися.
 *
 * ⛔ Червоні без фіксу: роль `dialog` (Mantine `Modal`) замість `alertdialog` — падає перший; без фокуса на
 * кнопці — падає «фокус»; без обробки Escape — падає другий.
 */
describe('IdleWarningDialog', () => {
  it('alertdialog із назвою, описом і відліком; фокус на «Stay signed in»', async () => {
    renderWithMantine(<IdleWarningDialog remainingMs={45_000} onStay={() => {}} />);

    const dialog = screen.getByRole('alertdialog');
    expect(dialog.getAttribute('aria-modal')).toBe('true');

    const title = document.getElementById(dialog.getAttribute('aria-labelledby') ?? '');
    const text = document.getElementById(dialog.getAttribute('aria-describedby') ?? '');
    expect(title?.textContent ?? '').not.toBe('');
    expect(text?.textContent ?? '').not.toBe('');

    expect(screen.getByRole('timer').textContent).toBe('0:45');
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button')));
  });

  it('«Stay signed in» і Escape — відповідь «я тут»', () => {
    const onStay = vi.fn();
    renderWithMantine(<IdleWarningDialog remainingMs={10_000} onStay={onStay} />);

    fireEvent.click(screen.getByRole('button'));
    fireEvent.keyDown(screen.getByRole('alertdialog'), { key: 'Escape' });
    expect(onStay).toHaveBeenCalledTimes(2);
  });
});
