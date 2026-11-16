import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { AddValueDialog } from '../AddValueDialog';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Not in the list? Add a value…» — один `PUT /permit-lists/{key}/items` на відкриття (зонд: ЗАКРИТО, база
 * 826f654e4).
 *
 * Власник (`RowDrawer`) монтує діалог лише за `{adding !== null && …}` і знімає його в `onClose`; сам діалог —
 * `<Modal opened>` без закритого стану. `submit` уже звіряє `change.isPending`, а хук перечитує переліки ДО виклику
 * `onSuccess` — увесь цей час запит «в польоті», тож другий сабміт нічого не шле.
 *
 * Тест тримає це: без `change.isPending ||` у `submit` — червоний.
 */

const Change = 'PUT /api/v1/permit-lists/shared%3AWATER_BODY/items';

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: AddValueDialog — один PUT на відкриття', () => {
  it('подвійний сабміт шле один PUT; після успіху діалог знято з дерева', async () => {
    const server = holdWrites({
      read: () => [],
      write: () => ({ body: { key: 'shared:WATER_BODY', items: [{ id: 9, code: 'CASPIAN', displayL10n: { en: 'Caspian' }, isActive: true }] } }),
    });
    const onAdded = vi.fn();

    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? <AddValueDialog listKey="shared:WATER_BODY" scope="shared" listName="WATER_BODY" onAdded={onAdded} onClose={() => setOpen(false)} /> : null;
    }

    renderKeepMounted(<Host />);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'Caspian' } });
    const form = screen.getByRole('dialog').querySelector('form');
    if (form === null) throw new Error('форми немає');

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Change]);
    });
    await nextTick();
    fireEvent.submit(form);
    await settle();
    expect(server.sent).toEqual([Change]);

    server.release();
    await waitFor(() => {
      expect(form.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Change]);
    expect(onAdded).toHaveBeenCalledTimes(1);
  });
});
