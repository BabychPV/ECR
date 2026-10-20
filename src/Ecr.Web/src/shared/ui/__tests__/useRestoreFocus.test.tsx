import { StrictMode, useState, type JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import { ConfirmModal } from '../ConfirmModal';

/**
 * PA-U-03: модалка, що монтується вже відкритою (`?dialog=`, лінивий чанк), після Esc повертає фокус на
 * кнопку-виклик, а не на `BODY`.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `useRestoreFocus(opened)` з `ConfirmModal` — фокус після
 * Esc лишається на `BODY`, обидва тести червоні.
 */
function Host({ mountOnly }: { readonly mountOnly: boolean }): JSX.Element {
  const [open, setOpen] = useState(false);

  return (
    <MantineProvider theme={testTheme}>
      <button type="button" onClick={() => setOpen(true)}>
        Trigger
      </button>
      {mountOnly ? (
        // Як `?dialog=`: діалог існує в дереві лише відкритим.
        open && <ConfirmModal opened title="Sure?" verb="Do" onConfirm={() => undefined} onClose={() => setOpen(false)} />
      ) : (
        <ConfirmModal opened={open} title="Sure?" verb="Do" onConfirm={() => undefined} onClose={() => setOpen(false)} />
      )}
    </MantineProvider>
  );
}

afterEach(() => cleanup());

describe('useRestoreFocus', () => {
  for (const [mountOnly, strict] of [
    [true, false],
    [false, false],
    [true, true], // dev-режим: StrictMode монтує ефекти двічі (так працює стенд e2e)
  ] as const) {
    it(`Esc повертає фокус на кнопку-виклик (${mountOnly ? 'змонтовано відкритим' : 'opened перемикається'}${strict ? ', StrictMode' : ''})`, async () => {
      render(strict ? <StrictMode><Host mountOnly={mountOnly} /></StrictMode> : <Host mountOnly={mountOnly} />);
      const trigger = screen.getByRole('button', { name: 'Trigger' });
      trigger.focus();
      fireEvent.click(trigger);
      const dialog = await screen.findByRole('dialog', { name: 'Sure?' });
      await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

      fireEvent.keyDown(dialog, { key: 'Escape' });

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(trigger));
    });
  }
});
