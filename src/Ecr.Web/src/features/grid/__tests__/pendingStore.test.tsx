import type { JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import type { PendingEdit } from '../useCellPatch';
import {
  cellKey,
  currentDocumentId,
  discardPendingRows,
  hasPending,
  openDocument,
  pendingCount,
  pendingSlice,
  pendingSlices,
  putPendingEdit,
  replacePendingSlice,
  resetPending,
  usePendingCount,
  usePendingSlice,
} from '../pendingStore';

/**
 * `D14-12`: незбережені правки належать документу, а не сітці.
 *
 * ⛔ Головне, що тут доводиться, — правка ПЕРЕЖИВАЄ розмонтування сітки. Саме
 * цього не вміла попередня схема: `pending` жив у `useState` всередині
 * `DocumentGrid`, і перемикання аркуша губило правку молодшу за дебаунс
 * (`W-02`). Тому кожен тест, який щось стверджує про виживання, РОЗМОНТОВУЄ
 * компонент, а не просто перечитує стан.
 */

const Table = 700;
const Period = 202609;

function edit(rowKey: string, columnCode: string, value: unknown): PendingEdit {
  return { rowKey, columnCode, value, isEmpty: false, baseVersion: 'v1' };
}

afterEach(() => {
  cleanup();
  resetPending();
});

describe('D14-12 · сховище правок рівня документа', () => {
  it('правка переживає розмонтування сітки, яка її зробила', () => {
    openDocument(5);

    function Grid(): JSX.Element {
      const pending = usePendingSlice(Table, Period);

      return <span>{`комірок: ${String(pending.size)}`}</span>;
    }

    const view = render(<Grid />);

    act(() => {
      putPendingEdit(Table, Period, edit('R1', 'C1', 12.4));
    });
    expect(screen.getByText('комірок: 1')).toBeDefined();

    // ⛔ Ось воно: сітка зникає — правка лишається.
    view.unmount();

    expect(pendingSlice(Table, Period).size).toBe(1);
    expect(pendingCount()).toBe(1);
  });

  it('правки різних зрізів не змішуються, а разом дають лічильник документа', () => {
    openDocument(5);

    putPendingEdit(Table, Period, edit('R1', 'C1', 1));
    putPendingEdit(701, Period, edit('R1', 'C1', 2));
    putPendingEdit(Table, 202512, edit('R1', 'C1', 3));

    expect(pendingSlice(Table, Period).size).toBe(1);
    expect(pendingSlice(701, Period).size).toBe(1);
    expect(pendingSlice(Table, 202512).size).toBe(1);
    expect(pendingCount()).toBe(3);

    // ⚠ Ключ зрізу — пара; період так само розрізняє, як і таблиця.
    expect(pendingSlices()).toHaveLength(3);
  });

  it('перехід в інший документ стирає накопичене, повторне відкриття того самого — ні', () => {
    openDocument(5);
    putPendingEdit(Table, Period, edit('R1', 'C1', 1));

    /*
     * ⛔ Повторний виклик не має стирати: `DocumentPage` кличе `openDocument`
     * у `useEffect`, а той у `StrictMode` виконується ДВІЧІ — стирання «про
     * всяк випадок» знищувало б правки на другому проході, і дефект виглядав
     * би як «іноді зникає».
     */
    openDocument(5);
    expect(pendingCount()).toBe(1);

    openDocument(6);
    expect(pendingCount()).toBe(0);
    expect(currentDocumentId()).toBe(6);
  });

  it('зріз без правок зникає зі сховища, а не лишається порожнім', () => {
    openDocument(5);
    putPendingEdit(Table, Period, edit('R1', 'C1', 1));

    replacePendingSlice(Table, Period, new Map());

    expect(hasPending()).toBe(false);
    expect(pendingSlices()).toHaveLength(0);
  });

  it('підтвердження рядка прибирає його правки, але НЕ ті, що зроблені після надсилання', () => {
    openDocument(5);

    const sentR1C1 = edit('R1', 'C1', 1);
    putPendingEdit(Table, Period, sentR1C1);
    putPendingEdit(Table, Period, edit('R2', 'C1', 2));

    // Знімок того, що пішло на сервер.
    const sent = new Map(pendingSlice(Table, Period));

    // Доки patch летів, користувач правив ту саму комірку далі.
    putPendingEdit(Table, Period, edit('R1', 'C1', 99));

    discardPendingRows(Table, Period, ['R1'], sent);

    const left = pendingSlice(Table, Period);

    /*
     * ⛔ Мутаційний доказ: приберіть у `discardPendingRows` умову `newer` —
     * правка 99 зникне разом із підтвердженою, тобто значення, якого сервер не
     * бачив, буде мовчки втрачено. Саме цей рядок тоді впаде.
     */
    expect(left.get(cellKey({ rowKey: 'R1', columnCode: 'C1' }))?.value).toBe(99);
    expect(left.get(cellKey({ rowKey: 'R2', columnCode: 'C1' }))?.value).toBe(2);
    expect(left.size).toBe(2);
  });

  it('підтвердження без правок після надсилання прибирає рядок цілком', () => {
    openDocument(5);
    putPendingEdit(Table, Period, edit('R1', 'C1', 1));
    putPendingEdit(Table, Period, edit('R2', 'C1', 2));

    const sent = new Map(pendingSlice(Table, Period));
    discardPendingRows(Table, Period, ['R1'], sent);

    expect([...pendingSlice(Table, Period).keys()]).toEqual([
      cellKey({ rowKey: 'R2', columnCode: 'C1' }),
    ]);
  });

  it('лічильник документа бачить правки сітки, якої на екрані вже немає', () => {
    openDocument(5);

    function Grid(): JSX.Element {
      usePendingSlice(Table, Period);

      return <span>сітка</span>;
    }

    function StatusBar(): JSX.Element {
      return <span>{`незбережено: ${String(usePendingCount())}`}</span>;
    }

    const grid = render(<Grid />);
    render(<StatusBar />);

    act(() => {
      putPendingEdit(Table, Period, edit('R1', 'C1', 1));
    });
    expect(screen.getByText('незбережено: 1')).toBeDefined();

    /*
     * ⛔ Це — передумова `useBlocker` з `D14-12` (крок 3). Доки лічильник жив у
     * сітці, вихід зі сторінки НЕ МІГ дізнатися про незбережене: сітку на той
     * момент уже розмонтовано прокруткою або перемиканням аркуша.
     */
    grid.unmount();

    expect(screen.getByText('незбережено: 1')).toBeDefined();
  });

  it('зріз без правок віддає ТЕ САМЕ посилання — інакше useSyncExternalStore зациклиться', () => {
    openDocument(5);

    /*
     * ⚠ Не мікрооптимізація. `useSyncExternalStore` порівнює знімки за
     * посиланням; `new Map()` у геттері означав би новий знімок на кожен
     * рендер, попередження «getSnapshot should be cached» і нескінченне
     * перемальовування — на БІЛЬШОСТІ зрізів, бо правок у них немає.
     */
    expect(pendingSlice(999, Period)).toBe(pendingSlice(998, 202512));

    putPendingEdit(Table, Period, edit('R1', 'C1', 1));
    const first = pendingSlice(Table, Period);

    expect(pendingSlice(Table, Period)).toBe(first);
  });
});
