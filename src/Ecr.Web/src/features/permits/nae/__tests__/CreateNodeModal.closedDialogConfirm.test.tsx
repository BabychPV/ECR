import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { CreateNodeModal } from '@/features/permits/nae/CreateNodeModal';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { holdReads } from '@/test/heldReads';
import type { TreeLevel } from '@/shared/ui/TreeEditor';

/**
 * B149 (група 3, «тіло знімається разом зі станом»): діалог нового вузла NAE не створює вузол удруге.
 *
 * Зонд (справжній діалог, закритий `Modal` лишається в DOM, відповіді віддає тест):
 * - вікно «ще відкритий»: `onSuccess` форми чекає `invalidateRegistryTree` (перечитуються дерево, рівні й записи;
 *   рівні — запит самого діалогу) ВСЕРЕДИНІ мутації — `isPending` тримається, `submit` відсікає
 *   `if (save.isPending …) return`;
 * - після `onCreated` власник (`NaeStructurePage`) знімає діалог цілком (`{draft !== null && …}`), а сам діалог —
 *   форму під `{draft !== null && …}`: кнопка від'єднана.
 * Вердикт: ЗАКРИТО; цей тест — регресія.
 */

const Site: TreeLevel = { code: 'Site', ordinal: 1, name: 'Site', isOptional: false };
const Languages = [{ code: 'en', isDefault: true, isActive: true, nameNative: 'English' }];

function Host(): JSX.Element {
  const [draft, setDraft] = useState<{ parent: null; level: TreeLevel } | null>({ parent: null, level: Site });

  return (
    <>
      {draft !== null && (
        <CreateNodeModal
          registryCode="NAE"
          registryId={40}
          codeAutomatic={false}
          fields={[]}
          draft={draft}
          onClose={() => setDraft(null)}
          onCreated={() => setDraft(null)}
        />
      )}
    </>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: новий вузол NAE — один POST на відкриття', () => {
  it('ні під час перечитування (діалог відкритий), ні після закриття другого POST немає', async () => {
    const server = holdWrites({
      read: (url) => (url.endsWith('/api/v1/languages') ? Languages : url.endsWith('/hierarchy') ? { levels: [] } : {}),
      write: () => ({ body: { id: 77 } }),
    });
    const reads = holdReads((url) => url === '/api/v1/registries/NAE/hierarchy');
    renderKeepMounted(<Host />);

    fireEvent.change(await screen.findByLabelText(/registries\.code/), { target: { value: 'S1' } });
    fireEvent.change(await screen.findByLabelText(/registries\.name.*English/), { target: { value: 'Site one' } });
    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/registries/NAE/entries']);
    });

    // Сервер створив; форма чекає перечитування — діалог ще відкритий.
    reads.arm();
    server.release();
    await waitFor(() => {
      expect(reads.waiting()).toBe(1);
    });
    expect(screen.getByRole('dialog')).not.toBeNull();
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/registries/NAE/entries']);

    // Перечитано, вузол створено: власник зняв діалог.
    reads.open();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(save.isConnected).toBe(false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/registries/NAE/entries']);
  });
});
