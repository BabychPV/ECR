import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import { ConfirmModal } from '../ConfirmModal';
import { ReasonModal } from '../ReasonModal';

/**
 * P3-F2: фокус після закриття модалки повертає ЛИШЕ `useRestoreFocus`; власний `returnFocus` Mantine вимкнено
 * (`returnFocus={false}`), бо він пізніше фокусував би кнопку меню, що вже зникла / ще згасає (гонка).
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `returnFocus={false}` з `ConfirmModal` або `ReasonModal` - відповідний тест
 * червоний (кнопку-виклик фокусують двічі: хук і Mantine).
 */
function Host({ kind }: { readonly kind: 'confirm' | 'reason' }): JSX.Element {
  const [open, setOpen] = useState(false);

  return (
    <MantineProvider theme={testTheme}>
      <button type="button" onClick={() => setOpen(true)}>
        Trigger
      </button>
      {kind === 'confirm' ? (
        <ConfirmModal opened={open} title="Sure?" verb="Do" onConfirm={() => undefined} onClose={() => setOpen(false)} />
      ) : (
        <ReasonModal
          opened={open}
          title="Why?"
          label="Reason"
          confirmLabel="Go"
          onConfirm={() => undefined}
          onClose={() => setOpen(false)}
        />
      )}
    </MantineProvider>
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('модалки: фокус після закриття повертає лише наш хук', () => {
  for (const kind of ['confirm', 'reason'] as const) {
    it(`${kind}: Esc -> фокус на тригері, і тригер фокусується рівно один раз`, async () => {
      render(<Host kind={kind} />);
      const trigger = screen.getByRole('button', { name: 'Trigger' });
      trigger.focus();
      const original = HTMLElement.prototype.focus;
      const targets: Element[] = [];
      vi.spyOn(HTMLElement.prototype, 'focus').mockImplementation(function (this: HTMLElement, ...args) {
        targets.push(this);
        original.apply(this, args);
      });

      fireEvent.click(trigger);
      const dialog = await screen.findByRole('dialog');
      await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));
      targets.length = 0;

      fireEvent.keyDown(dialog, { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(trigger));
      // Дати Mantine час на власне повернення (воно іде після завершення переходу).
      await new Promise((resolve) => setTimeout(resolve, 300));

      expect(document.activeElement).toBe(trigger);
      expect(targets.filter((target) => target === trigger)).toHaveLength(1);
    });
  }
});
