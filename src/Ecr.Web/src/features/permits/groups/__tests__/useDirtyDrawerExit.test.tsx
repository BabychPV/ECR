import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { useDirtyDrawerExit } from '../useDirtyDrawerExit';

/**
 * Перехоплення виходу зі шторки: Esc поза списком питає, у відкритому списку - ні; клік по хрестику/затемненню гаситься;
 * Esc, що ЗАКРИВ підтвердження, не відкриває його знову в тій самій задачі (живий e2e, run 39921583289: `Modal` Mantine
 * закриває підтвердження на `window` у фазі захоплення, React вмикає хук, і та сама подія доходить до нового слухача).
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `|| fresh` у `onKeyDown` - валить «Esc у тій самій задачі».
 */
function Probe({ active, onAttempt }: { active: boolean; onAttempt: () => void }): React.JSX.Element {
  useDirtyDrawerExit('P', active, onAttempt);

  return (
    <div data-panel="P">
      <button type="button" className="mantine-Drawer-close">
        x
      </button>
      <input aria-expanded="false" data-testid="plain" />
      <input aria-expanded="true" data-testid="open-list" />
    </div>
  );
}

const settled = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 5));
const esc = (target: Element): void => {
  target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
};

afterEach(cleanup);

describe('useDirtyDrawerExit', () => {
  it('Esc у тій самій задачі, в якій хук увімкнули, ігнорується; пізніше - питає', async () => {
    const onAttempt = vi.fn();
    const view = render(<Probe active={false} onAttempt={onAttempt} />);
    view.rerender(<Probe active onAttempt={onAttempt} />);

    esc(view.getByTestId('plain'));
    expect(onAttempt).not.toHaveBeenCalled();

    await settled();
    esc(view.getByTestId('plain'));
    expect(onAttempt).toHaveBeenCalledTimes(1);
  });

  it('Esc у відкритому списку вибору не питає', async () => {
    const onAttempt = vi.fn();
    const view = render(<Probe active onAttempt={onAttempt} />);
    await settled();

    esc(view.getByTestId('open-list'));

    expect(onAttempt).not.toHaveBeenCalled();
  });

  it('клік по хрестику гаситься й питає; без active - нічого не слухає', async () => {
    const onAttempt = vi.fn();
    const onOther = vi.fn();
    const view = render(<Probe active onAttempt={onAttempt} />);
    view.getByRole('button').addEventListener('click', onOther);

    view.getByRole('button').click();
    expect(onAttempt).toHaveBeenCalledTimes(1);
    expect(onOther).not.toHaveBeenCalled();

    view.rerender(<Probe active={false} onAttempt={onAttempt} />);
    view.getByRole('button').click();
    await settled();
    esc(view.getByTestId('plain'));
    expect(onAttempt).toHaveBeenCalledTimes(1);
    expect(onOther).toHaveBeenCalledTimes(1);
  });
});
