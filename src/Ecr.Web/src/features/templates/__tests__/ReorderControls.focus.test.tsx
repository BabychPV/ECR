import { useState, type JSX } from 'react';
import { describe, expect, it } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { ReorderCell, ReorderableRows } from '../ReorderControls';
import { renderWithMantine } from '@/test/render';

/**
 * Фокус іде за переставленим рядком (WCAG 2.4.3, `ФВ-2.6`).
 *
 * Сторінка версії на час запису вимикає кнопки (`disabled`), а після відповіді React переносить рядок у DOM —
 * у браузері обидва кроки знімають фокус. Стенд нижче відтворює ту саму послідовність: вимкнути, зняти фокус
 * (як браузер), переставити, увімкнути.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати `target.focus()` в ефекті `ReorderCell` →
 * червоніють обидва тести; прибрати перевірку `wanted.disabled` (без переходу на сусідню кнопку) → червоніє
 * «межа списку».
 */
function Stand(): JSX.Element {
  const [items, setItems] = useState(['Alpha', 'Beta', 'Gamma']);
  const [busy, setBusy] = useState(false);

  const onMove = (from: number, to: number): void => {
    setBusy(true);
    (document.activeElement as HTMLElement | null)?.blur();
    setTimeout(() => {
      setItems((current) => {
        const next = [...current];
        const [moved] = next.splice(from, 1);
        next.splice(to, 0, moved as string);
        return next;
      });
      setBusy(false);
    }, 0);
  };

  return (
    <table>
      <tbody>
        <ReorderableRows items={items} enabled={!busy} onMove={onMove}>
          {(item, index, drag) => (
            <tr key={item} data-row={item} {...drag.targetProps(index)}>
              <td>
                <ReorderCell index={index} count={items.length} name={item} disabled={busy} onMove={onMove} drag={drag} />
              </td>
              <td>{item}</td>
            </tr>
          )}
        </ReorderableRows>
      </tbody>
    </table>
  );
}

const order = (): string[] =>
  [...document.querySelectorAll<HTMLElement>('[data-row]')].map((row) => row.dataset.row ?? '');

describe('ReorderCell — фокус після перестановки', () => {
  it('«нижче» лишає фокус на «нижче» того самого елемента в новій позиції', async () => {
    renderWithMantine(<Stand />);

    const alphaDown = screen.getByRole('button', { name: /reorder\.moveDown.*Alpha/ });
    alphaDown.focus();
    await act(async () => {
      fireEvent.click(alphaDown);
    });

    await waitFor(() => expect(order()).toEqual(['Beta', 'Alpha', 'Gamma']));
    await waitFor(() => expect(document.activeElement).toBe(alphaDown));
  });

  it('межа списку: «нижче» на передостанньому — фокус на «вище», бо «нижче» вимкнене', async () => {
    renderWithMantine(<Stand />);

    const betaDown = screen.getByRole('button', { name: /reorder\.moveDown.*Beta/ });
    const betaUp = screen.getByRole('button', { name: /reorder\.moveUp.*Beta/ });
    betaDown.focus();
    await act(async () => {
      fireEvent.click(betaDown);
    });

    await waitFor(() => expect(order()).toEqual(['Alpha', 'Gamma', 'Beta']));
    await waitFor(() => expect(document.activeElement).toBe(betaUp));
  });
});
