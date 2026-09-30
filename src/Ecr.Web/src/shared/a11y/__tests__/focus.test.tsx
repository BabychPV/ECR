import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { focusIsLost, useFocusAfterBusy, useListFocus, useReturnFocusOnUnmount } from '../focus';

/**
 * Утримання фокуса клавіатури (WCAG 2.4.3): три місця, де Mantine і браузер кидають фокус на `<body>`.
 *
 * ⚠ jsdom сам не знімає фокус із кнопки, що стала `disabled`, — стенди нижче роблять це явно (`blur()`), як
 * браузер.
 *
 * ⛔ Мутаційні докази (перевірено руками 2026-09-30): у `useReturnFocusOnUnmount` повернути `focusSoon`
 * без виклику → червоний «діалог за умовою»; у `useFocusAfterBusy` прибрати `onlyIfLost` (`focusSoon(target)`)
 * → червоний «не смикає»; у `useListFocus` прибрати гілку `removed` → червоний «прибирання».
 */
afterEach(cleanup);

function Dialog({ onClose }: { readonly onClose: () => void }): JSX.Element {
  useReturnFocusOnUnmount();

  return (
    <div role="dialog">
      <button type="button" autoFocus onClick={onClose}>
        close
      </button>
    </div>
  );
}

function DialogStand(): JSX.Element {
  const [open, setOpen] = useState(false);

  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>
        open
      </button>
      {open && <Dialog onClose={() => setOpen(false)} />}
    </>
  );
}

function BusyStand({ other }: { readonly other: boolean }): JSX.Element {
  const [busy, setBusy] = useState(false);
  const focus = useFocusAfterBusy(busy);

  return (
    <>
      <button
        type="button"
        ref={focus.ref}
        disabled={busy}
        onClick={(event) => {
          focus.arm();
          setBusy(true);
          event.currentTarget.blur();
          if (other) document.getElementById('other')?.focus();
        }}
      >
        run
      </button>
      <input id="other" aria-label="other" />
      <button type="button" onClick={() => setBusy(false)}>
        done
      </button>
    </>
  );
}

function ListStand(): JSX.Element {
  const [rows, setRows] = useState<string[]>(['a']);
  const focus = useListFocus(rows.length);

  return (
    <div ref={focus.container}>
      {rows.map((row, index) => (
        <div key={row} data-focus-row="">
          <input type="hidden" value={row} />
          <input aria-label={`field ${row}`} />
          <button
            type="button"
            onClick={() => {
              focus.removed();
              setRows((current) => current.filter((_, i) => i !== index));
            }}
          >
            remove {row}
          </button>
        </div>
      ))}
      <button
        type="button"
        ref={focus.addButton}
        onClick={() => {
          focus.added();
          setRows((current) => [...current, String.fromCharCode(97 + current.length)]);
        }}
      >
        add
      </button>
    </div>
  );
}

describe('useReturnFocusOnUnmount', () => {
  it('діалог за умовою: після розмонтування фокус повертається на кнопку, що його відкрила', async () => {
    render(<DialogStand />);
    const open = screen.getByRole('button', { name: 'open' });
    open.focus();
    fireEvent.click(open);

    const close = screen.getByRole('button', { name: 'close' });
    close.focus();
    fireEvent.click(close);

    await waitFor(() => expect(document.activeElement).toBe(open));
  });
});

describe('useFocusAfterBusy', () => {
  it('фокус загубився на час запиту — повертається на кнопку', async () => {
    render(<BusyStand other={false} />);
    const run = screen.getByRole('button', { name: 'run' });
    run.focus();
    fireEvent.click(run);
    expect(focusIsLost()).toBe(true);

    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'done' }));
      (document.activeElement as HTMLElement | null)?.blur();
    });

    await waitFor(() => expect(document.activeElement).toBe(run));
  });

  it('не смикає: людина тим часом перейшла в інше поле — фокус лишається там', async () => {
    render(<BusyStand other />);
    const run = screen.getByRole('button', { name: 'run' });
    run.focus();
    fireEvent.click(run);
    const other = screen.getByRole('textbox', { name: 'other' });
    expect(document.activeElement).toBe(other);

    act(() => {
      screen.getByRole('button', { name: 'done' }).click();
      other.focus();
    });

    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(document.activeElement).toBe(other);
  });
});

describe('useListFocus', () => {
  it('додавання: фокус у перше видиме поле нового рядка (не в прихований input)', () => {
    render(<ListStand />);
    fireEvent.click(screen.getByRole('button', { name: 'add' }));

    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'field b' }));
  });

  it('прибирання: фокус на «Додати», а не на <body>', () => {
    render(<ListStand />);
    const remove = screen.getByRole('button', { name: 'remove a' });
    remove.focus();
    fireEvent.click(remove);

    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'add' }));
  });
});
