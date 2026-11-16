import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { UnitEditModal } from '@/features/units/UnitEditModal';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { holdReads } from '@/test/heldReads';

/**
 * B149 (група 3, «тіло знімається разом зі станом»): правка одиниці не шле `PUT` удруге.
 *
 * Зонд (справжній діалог, закритий `Modal` лишається в DOM, відповіді віддає тест):
 * - вікно «ще відкритий»: `onSuccess` чекає `invalidateQueries(units.all)` ВСЕРЕДИНІ мутації (перечитується й
 *   сама одиниця діалогу) — `isPending` тримається, кнопка `loading` → `disabled`, клік до обробника не доходить;
 * - після закриття (`unitId = null`) форма `{unitId !== null && …}` знімається в тому ж рендері — кнопка
 *   від'єднана.
 * Вердикт: ЗАКРИТО; цей тест — регресія.
 */

const Lb = {
  id: 5,
  code: 'lb',
  symbolL10n: { en: 'lb' },
  nameL10n: { en: 'Pound' },
  dimensionId: 1,
  isBase: false,
  factorToBase: '0.4535923700',
  offsetToBase: '0.0000000000',
  rowVersion: 'v1',
};

function Host(): JSX.Element {
  const [unitId, setUnitId] = useState<number | null>(5);

  return <UnitEditModal unitId={unitId} code="lb" onClose={() => setUnitId(null)} />;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: правка одиниці — один PUT на відкриття', () => {
  it('ні під час перечитування (діалог відкритий), ні після закриття другого PUT немає', async () => {
    const server = holdWrites({
      read: (url) => (url === '/api/v1/units/5' ? Lb : url === '/api/v1/units/5/usage' ? { total: 0, items: [] } : {}),
      write: () => ({ body: Lb }),
    });
    const reads = holdReads((url) => url.startsWith('/api/v1/units/5'));
    renderKeepMounted(<Host />);

    fireEvent.change(await screen.findByLabelText(/units\.symbol/), { target: { value: 'pound' } });
    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['PUT /api/v1/units/5']);
    });

    // Сервер відповів; мутація чекає перечитування одиниці — діалог ще відкритий.
    reads.arm();
    server.release();
    await waitFor(() => {
      expect(reads.waiting()).toBeGreaterThan(0);
    });
    expect(screen.getByRole('dialog')).not.toBeNull();
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/units/5']);

    // Перечитано, діалог закрито: форма знята разом із кнопкою.
    reads.open();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(save.isConnected).toBe(false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/units/5']);
  });
});
