import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ReorderCell, ReorderableRows } from '../ReorderControls';
import { renderWithMantine } from '@/test/render';

/**
 * Перестановка рядків списку (`ФВ-2.6`): перетягування й доступна
 * альтернатива кнопками.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): у `ReorderCell`
 * прибрати `index === 0` з `disabled` — червоніє «межі»; у `onDrop` прибрати
 * перевірку `from !== index` — червоніє «скидання на себе»; в `onDragOver`
 * прибрати `state.from === null` — червоніє «чуже перетягування»; у
 * `handleProps` для `enabled=false` повернути `draggable: true` — червоніє
 * «вимкнений список».
 */
const items = ['Alpha', 'Beta', 'Gamma'];

function renderList(onMove: (from: number, to: number) => void, enabled = true): void {
  renderWithMantine(
    <table>
      <tbody>
        <ReorderableRows items={items} enabled={enabled} onMove={onMove}>
          {(item, index, drag) => (
            <tr key={item} data-testid={`row-${item}`} {...drag.targetProps(index)}>
              <td>
                <ReorderCell
                  index={index}
                  count={items.length}
                  name={item}
                  disabled={!enabled}
                  onMove={onMove}
                  drag={drag}
                  {...(enabled ? {} : { describedBy: 'hint' })}
                />
              </td>
              <td>{item}</td>
            </tr>
          )}
        </ReorderableRows>
      </tbody>
    </table>,
  );
}

const handle = (index: number): HTMLElement => {
  const node = document.querySelector<HTMLElement>(`[data-reorder-handle="${String(index)}"]`);
  if (node === null) throw new Error(`немає ручки ${String(index)}`);
  return node;
};

const dataTransfer = (): Partial<DataTransfer> => ({ setData: vi.fn(), dropEffect: 'none', effectAllowed: 'all' });

describe('ReorderCell — кнопки як альтернатива перетягуванню', () => {
  it('кнопки мають доступну назву з іменем і переставляють на одну позицію', async () => {
    const onMove = vi.fn();
    renderList(onMove);
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /reorder\.moveDown.*Alpha/ }));
    expect(onMove).toHaveBeenLastCalledWith(0, 1);

    await user.click(screen.getByRole('button', { name: /reorder\.moveUp.*Gamma/ }));
    expect(onMove).toHaveBeenLastCalledWith(2, 1);
  });

  it('межі: першу не можна вище, останню нижче', () => {
    renderList(vi.fn());

    expect((screen.getByRole('button', { name: /reorder\.moveUp.*Alpha/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: /reorder\.moveDown.*Gamma/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: /reorder\.moveDown.*Alpha/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('з клавіатури: Tab до кнопки й Enter', async () => {
    const onMove = vi.fn();
    renderList(onMove);
    const user = userEvent.setup();

    screen.getByRole('button', { name: /reorder\.moveDown.*Beta/ }).focus();
    await user.keyboard('{Enter}');
    expect(onMove).toHaveBeenCalledWith(1, 2);
  });

  it('ручка прихована від читача: вона дублює кнопки', () => {
    renderList(vi.fn());
    expect((handle(0))?.getAttribute('aria-hidden')).toBe('true');
  });
});

describe('useDragReorder — перетягування', () => {
  it('скидання на інший рядок переставляє', () => {
    const onMove = vi.fn();
    renderList(onMove);

    fireEvent.dragStart(handle(0), { dataTransfer: dataTransfer() });
    expect(fireEvent.dragOver(screen.getByTestId('row-Gamma'), { dataTransfer: dataTransfer() })).toBe(false);
    expect((screen.getByTestId('row-Gamma'))?.getAttribute('data-drop-target')).toBe('true');

    fireEvent.drop(screen.getByTestId('row-Gamma'), { dataTransfer: dataTransfer() });
    expect(onMove).toHaveBeenCalledWith(0, 2);
    expect((screen.getByTestId('row-Gamma')).hasAttribute('data-drop-target')).toBe(false);
  });

  it('скидання на себе нічого не робить', () => {
    const onMove = vi.fn();
    renderList(onMove);

    fireEvent.dragStart(handle(1), { dataTransfer: dataTransfer() });
    fireEvent.drop(screen.getByTestId('row-Beta'), { dataTransfer: dataTransfer() });
    expect(onMove).not.toHaveBeenCalled();
  });

  it('чуже перетягування (без dragStart у списку) ціллю не є', () => {
    const onMove = vi.fn();
    renderList(onMove);

    // ⚠ `fireEvent` повертає `false`, коли обробник кликнув `preventDefault()`:
    // саме він у браузері робить рядок дозволеною ціллю скидання.
    expect(fireEvent.dragOver(screen.getByTestId('row-Beta'), { dataTransfer: dataTransfer() })).toBe(true);
    fireEvent.drop(screen.getByTestId('row-Beta'), { dataTransfer: dataTransfer() });
    expect((screen.getByTestId('row-Beta')).hasAttribute('data-drop-target')).toBe(false);
    expect(onMove).not.toHaveBeenCalled();
  });

  it('вимкнений список: не тягнеться, кнопки вимкнені й пояснені', () => {
    const onMove = vi.fn();
    renderList(onMove, false);

    expect((handle(0))?.getAttribute('draggable')).toBe('false');
    const down = screen.getByRole('button', { name: /reorder\.moveDown.*Alpha/ });
    expect((down as HTMLButtonElement).disabled).toBe(true);
    expect((down)?.getAttribute('aria-describedby')).toBe('hint');

    fireEvent.dragStart(handle(0), { dataTransfer: dataTransfer() });
    fireEvent.drop(screen.getByTestId('row-Beta'), { dataTransfer: dataTransfer() });
    expect(onMove).not.toHaveBeenCalled();
  });
});
