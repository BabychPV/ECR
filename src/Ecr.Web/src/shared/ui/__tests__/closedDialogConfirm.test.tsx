import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';

/**
 * B145: діалог підтвердження, що вже ЗАКРИТИЙ (`opened = false`), але ще в DOM, дію не виконує.
 *
 * ⛔ Дефект класу. `Modal` після `opened = false` лишається в DOM ~200 мс (анімація закриття), і кнопка
 * підтвердження далі викликала `onConfirm`. Власник у цю мить дію вже завершив і зайнятість зняв, тож
 * другий клік подвійного кліку, повільнішого за відповідь сервера, виконував дію ВДРУГЕ — без
 * підтвердження і, можливо, вже над іншим об'єктом. Захист — у самих `ConfirmModal` / `ReasonModal`
 * (корінь класу); обгортки `features/workflow/lazyDialogs.tsx` — другий шар.
 *
 * ⚠ Без залежності від часу. Компоненти СПРАВЖНІ; тема `keepMountedModalsTheme` лише лишає закритий
 * `Modal` у DOM назавжди, тож «клік по діалогу, що закривається» не змагається з таймером анімації.
 * `button.isConnected` перевіряється перед кожним таким кліком: клік по від'єднаному вузлу до React не
 * дійшов би, і тест був би зеленим ні про що.
 *
 * Мутаційні докази (повернути `onClick={onConfirm}` / `onClick={() => onConfirm(reason.trim())}`):
 * - `ConfirmModal.tsx` → червоний «ConfirmModal: …» (`expected "vi.fn()" to be called 1 times, but got 3 times`);
 * - `ReasonModal.tsx` → червоний «ReasonModal: …» (те саме повідомлення).
 */

afterEach(() => {
  cleanup();
});

describe('B145: закритий діалог (ще в DOM) не підтверджує', () => {
  it('ConfirmModal: відкритий підтверджує раз; закритий — ні; відкритий знову — підтверджує', () => {
    const onConfirm = vi.fn();
    const dialog = (opened: boolean): JSX.Element => (
      <MantineProvider theme={keepMountedModalsTheme}>
        <ConfirmModal
          opened={opened}
          title="Delete role “Night shift”?"
          verb="Delete role"
          cancelLabel="Cancel"
          onConfirm={onConfirm}
          onClose={() => undefined}
        />
      </MantineProvider>
    );
    const view = render(dialog(true));
    const button = screen.getByTestId('confirm-verb');

    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(1);

    // Власник закрив діалог: `opened = false`, кнопка ще в DOM і не вимкнена.
    view.rerender(dialog(false));
    expect(button.isConnected).toBe(true);
    expect(button).toHaveProperty('disabled', false);
    fireEvent.click(button);
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(1);

    // Відкрили знову — підтвердження працює (захист не «залипає»).
    view.rerender(dialog(true));
    expect(button.isConnected).toBe(true);
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(2);
  });

  it('ReasonModal: відкритий підтверджує раз із причиною; закритий — ні; відкритий знову — підтверджує', () => {
    const onConfirm = vi.fn();
    const dialog = (opened: boolean): JSX.Element => (
      <MantineProvider theme={keepMountedModalsTheme}>
        <ReasonModal
          opened={opened}
          title="Reject sheet"
          label="Reason"
          confirmLabel="Reject"
          onConfirm={onConfirm}
          onClose={() => undefined}
        />
      </MantineProvider>
    );
    const view = render(dialog(true));
    const reason = screen.getByRole('textbox', { name: /Reason/ });
    const button = screen.getByRole('button', { name: 'Reject' });

    fireEvent.change(reason, { target: { value: '  wrong totals  ' } });
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onConfirm).toHaveBeenLastCalledWith('wrong totals');

    // Власник закрив діалог: причина ще в полі, кнопка ще в DOM і не вимкнена.
    view.rerender(dialog(false));
    expect(button.isConnected).toBe(true);
    expect(button).toHaveProperty('disabled', false);
    fireEvent.click(button);
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(1);

    // Відкрили знову: поле очищене (як і раніше), нова причина — нове підтвердження.
    view.rerender(dialog(true));
    expect(button.isConnected).toBe(true);
    expect(button).toHaveProperty('disabled', true);
    fireEvent.change(reason, { target: { value: 'second' } });
    fireEvent.click(button);
    expect(onConfirm).toHaveBeenCalledTimes(2);
    expect(onConfirm).toHaveBeenLastCalledWith('second');
  });
});
