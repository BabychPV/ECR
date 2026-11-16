import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { EndEarlyModal } from '@/features/permits/lib/EndEarlyModal';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';

/**
 * B145b: «End early», що вже ЗАКРИТИЙ (`opened = false`), але ще в DOM, дострокове завершення не підтверджує.
 *
 * ⛔ Дефект класу B145. `EndEarlyModal` — власний двійник `ReasonModal` (з помилкою всередині діалогу),
 * тож кореневий захист `shared/ui/ReasonModal` його не покриває. Після `opened = false` причина ще в
 * полі (очищається лише наступним відкриттям), кнопка не вимкнена, і клік по ній у вікні анімації
 * закриття викликав `onConfirm` вдруге — у власників це другий `POST …/end` дозволу.
 *
 * ⚠ Без залежності від часу: компонент справжній, тема `keepMountedModalsTheme` лишає закритий `Modal`
 * у DOM назавжди; `isConnected` звіряється перед кліком.
 *
 * Мутаційний доказ: `EndEarlyModal.tsx`, кнопка підтвердження → `onClick={() => onConfirm(reason.trim())}`
 * → червоний (`expected "vi.fn()" to be called 1 times, but got 3 times`).
 */

afterEach(() => {
  cleanup();
});

describe('B145b: EndEarlyModal, що закрився (ще в DOM), не підтверджує', () => {
  it('відкритий підтверджує раз із причиною; закритий — ні; відкритий знову — підтверджує', () => {
    const onConfirm = vi.fn();
    const dialog = (opened: boolean): JSX.Element => (
      <MantineProvider theme={keepMountedModalsTheme}>
        <EndEarlyModal
          opened={opened}
          title="End permit early"
          label="Reason"
          description="Why the permit ends before its term."
          confirmLabel="End permit"
          minLength={3}
          isPending={false}
          error={null}
          onEdit={() => undefined}
          returnFocusTo={() => null}
          onConfirm={onConfirm}
          onClose={() => undefined}
        />
      </MantineProvider>
    );
    const view = render(dialog(true));
    const reason = screen.getByRole('textbox', { name: /Reason/ });
    const button = screen.getByRole('button', { name: 'End permit' });

    fireEvent.change(reason, { target: { value: '  revoked  ' } });
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onConfirm).toHaveBeenLastCalledWith('revoked');

    // Власник закрив діалог: причина ще в полі, кнопка ще в DOM і не вимкнена.
    view.rerender(dialog(false));
    expect(button.isConnected).toBe(true);
    expect(button).toHaveProperty('disabled', false);
    fireEvent.click(button);
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(1);

    // Відкрили знову: поле очищене, нова причина — нове підтвердження (захист не «залипає»).
    view.rerender(dialog(true));
    expect(button).toHaveProperty('disabled', true);
    fireEvent.change(reason, { target: { value: 'second' } });
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(2);
    expect(onConfirm).toHaveBeenLastCalledWith('second');
  });
});
