import { StrictMode, useState, type JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Menu } from '@mantine/core';
import { testTheme } from '@/test/render';
import { ConfirmModal } from '../ConfirmModal';
import { ReasonModal } from '../ReasonModal';

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

/**
 * P3-F2: діалог відкрито пунктом меню («More» → «End this permit early…»): пункт зникає разом із випадним списком, тож
 * запам'ятований елемент уже не в дереві. Фокус має повернутись на кнопку меню, а без неї - на заголовок, не на `BODY`.
 */
function MenuHost({
  mountOnly,
  hideTriggerOnOpen,
  closeDelayMs = 0,
}: {
  readonly mountOnly: boolean;
  readonly hideTriggerOnOpen: boolean;
  /**
   * Через скільки список меню закривається після кліку по пункту (у браузері - перехід `exit`). Це змодельована
   * затримка, а не очікування в асерті: усі перевірки йдуть через `waitFor`. Таймінг-перевірка 20.10: 20/20 поспіль.
   */
  readonly closeDelayMs?: number;
}): JSX.Element {
  const [open, setOpen] = useState(false);
  const [triggerShown, setTriggerShown] = useState(true);
  const [menuOpened, setMenuOpened] = useState(false);
  const modal = (opened: boolean): JSX.Element => (
    <ReasonModal
      opened={opened}
      title="End early"
      label="Reason"
      confirmLabel="End"
      onConfirm={() => undefined}
      onClose={() => setOpen(false)}
    />
  );

  return (
    <MantineProvider theme={testTheme}>
      <h1>Permit page</h1>
      {/* Список закривається ПІСЛЯ відкриття діалогу (у браузері - перехід `exit` Mantine): пункт ще в дереві, коли хук запам'ятовує фокус. */}
      <Menu opened={menuOpened} onChange={setMenuOpened} closeOnItemClick={false}>
        <Menu.Target>
          {triggerShown ? <button type="button">More</button> : <span />}
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item
            onClick={() => {
              setOpen(true);
              setTimeout(() => {
                setMenuOpened(false);
                if (hideTriggerOnOpen) setTriggerShown(false);
              }, closeDelayMs);
            }}
          >
            End this permit early…
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
      {mountOnly ? open && modal(true) : modal(open)}
    </MantineProvider>
  );
}

afterEach(() => cleanup());

describe('useRestoreFocus: діалог відкрито пунктом меню', () => {
  for (const mountOnly of [true, false]) {
    it(`Esc повертає фокус на кнопку «More» (${mountOnly ? 'змонтовано відкритим' : 'opened перемикається'})`, async () => {
      render(<MenuHost mountOnly={mountOnly} hideTriggerOnOpen={false} />);
      const more = screen.getByRole('button', { name: 'More' });
      fireEvent.click(more);
      const item = await screen.findByRole('menuitem', { name: 'End this permit early…' });
      item.focus();
      fireEvent.click(item);
      const dialog = await screen.findByRole('dialog', { name: 'End early' });
      await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));
      await waitFor(() => expect(document.querySelector('[role="menuitem"]')).toBeNull());

      fireEvent.keyDown(dialog, { key: 'Escape' });

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'More' })));
    });
  }

  // Реальний порядок гонки (CI integ-y31): клік по пункту -> діалог відкрито -> Esc, ПОКИ пункт ще в дереві (меню згасає)
  // -> пункт розмонтовується. Фокус мав би лишитись на «More», а не піти разом із пунктом на BODY.
  for (const mountOnly of [true, false]) {
    it(`Esc до того, як меню розмонтувалось: фокус лишається на «More» (${mountOnly ? 'змонтовано відкритим' : 'opened перемикається'})`, async () => {
      render(<MenuHost mountOnly={mountOnly} hideTriggerOnOpen={false} closeDelayMs={400} />);
      fireEvent.click(screen.getByRole('button', { name: 'More' }));
      const item = await screen.findByRole('menuitem', { name: 'End this permit early…' });
      item.focus();
      fireEvent.click(item);
      const dialog = await screen.findByRole('dialog', { name: 'End early' });
      expect(document.querySelector('[role="menuitem"]')).not.toBeNull();

      fireEvent.keyDown(dialog, { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.querySelector('[role="menuitem"]')).toBeNull());

      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'More' }));
    });
  }

  it('кнопки меню вже немає - фокус на заголовку сторінки, а не на BODY', async () => {
    render(<MenuHost mountOnly={false} hideTriggerOnOpen />);
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    const item = await screen.findByRole('menuitem', { name: 'End this permit early…' });
    item.focus();
    fireEvent.click(item);
    const dialog = await screen.findByRole('dialog', { name: 'End early' });
    await waitFor(() => expect(document.querySelector('[role="menuitem"]')).toBeNull());

    fireEvent.keyDown(dialog, { key: 'Escape' });

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Permit page' })));
  });
});

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
