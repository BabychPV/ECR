import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { DataSourceFormModal } from '@/features/integration/DataSourceFormModal';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B149 (група 3, «тіло знімається разом зі станом»): діалог нового з'єднання не створює вдруге.
 *
 * Зонд (справжній діалог, закритий `Modal` лишається в DOM, відповідь віддає тест): обидва власники
 * (`DataSourcesTable`, `DataSourceDrawer`) монтують діалог під `{creating && …}` / `{editing && …}`, а сам діалог
 * — форму під `{opened && …}`. `onSuccess` не чекає перечитування (`void invalidateQueries`) і закриває одразу —
 * вікна «ще відкритий після відповіді» немає; закриття знімає діалог цілком, кнопка від'єднана.
 * Під час запиту кнопка `loading` → `disabled`.
 * Вердикт: ЗАКРИТО; цей тест — регресія.
 */

function Host(): JSX.Element {
  const [creating, setCreating] = useState(true);

  return <>{creating && <DataSourceFormModal opened source={null} onClose={() => setCreating(false)} />}</>;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: нове з\'єднання — один POST на відкриття', () => {
  it('під час запиту й після закриття другого POST немає', async () => {
    const server = holdWrites({ write: () => ({ body: { id: 3 } }) });
    renderKeepMounted(<Host />);

    fireEvent.change(screen.getByLabelText(/sources\.code/), { target: { value: 'PI1' } });
    fireEvent.change(screen.getByLabelText(/sources\.name/), { target: { value: 'PI one' } });
    fireEvent.change(screen.getByLabelText(/sources\.endpoint/), { target: { value: 'https://pi.example/piwebapi' } });
    const create = screen.getByRole('button', { name: '⟦sources.create⟧' });
    await waitFor(() => {
      expect(create).toHaveProperty('disabled', false);
    });
    fireEvent.click(create);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/data-sources']);
    });

    // Запит у дорозі: кнопка зайнята.
    expect(create).toHaveProperty('disabled', true);
    fireEvent.click(create);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/data-sources']);

    // Сервер створив: власник зняв діалог цілком.
    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(create.isConnected).toBe(false);
    fireEvent.click(create);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/data-sources']);
  });
});
