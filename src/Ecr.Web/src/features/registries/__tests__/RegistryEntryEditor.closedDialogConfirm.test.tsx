import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto } from '@/api/types';
import { RegistryEntryEditor } from '@/features/registries/RegistryEntryEditor';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { holdReads } from '@/test/heldReads';

/**
 * B149 (група 3, «тіло знімається разом зі станом»): форма запису довідника не шле запит удруге — ні поки
 * діалог ще відкритий і чекає перечитування переліку, ні після закриття.
 *
 * Зонд (справжній діалог, закритий `Modal` лишається в DOM, відповіді віддає тест):
 * - вікно «ще відкритий»: `onSuccess` чекає `invalidateQueries` переліку ВСЕРЕДИНІ мутації, тож `isPending`
 *   тримається до закриття — кнопка в DOM, клік відсікає `if (upsert.isPending) return`;
 * - після закриття форма (`{opened && <EntryForm/>}`) знімається в тому ж рендері, що й `opened = false`, —
 *   кнопка від'єднана (`isConnected === false`), клік до React не доходить.
 * Вердикт: ЗАКРИТО; цей тест — регресія.
 */

const Registry: RegistryDefDto = {
  id: 11,
  code: 'CHEM',
  nameL10n: { values: { en: 'Chemicals' } },
  isHierarchical: false,
  isTemporal: false,
  sourceKind: 'Local',
  fields: [],
};

const Languages = [{ code: 'en', isDefault: true, isActive: true, nameNative: 'English' }];

function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);
  // Перелік записів, як на сторінці-власнику: його й перечитує `onSuccess`.
  useQuery({
    queryKey: queryKeys.registries.entries('CHEM'),
    queryFn: () => apiFetch<unknown>('/api/v1/registries/CHEM/entries'),
  });

  return <RegistryEntryEditor registry={Registry} entry={null} opened={opened} onClose={() => setOpened(false)} />;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: форма запису довідника — один POST на відкриття', () => {
  it('ні під час перечитування (діалог відкритий), ні після закриття другого POST немає', async () => {
    const server = holdWrites({
      read: (url) => (url.endsWith('/api/v1/languages') ? Languages : {}),
      write: () => ({ body: { id: 9 } }),
    });
    const reads = holdReads((url) => url === '/api/v1/registries/CHEM/entries');
    renderKeepMounted(<Host />);

    fireEvent.change(screen.getByLabelText('⟦registries.code⟧'), { target: { value: 'PHENOL' } });
    fireEvent.change(await screen.findByLabelText('⟦registries.name⟧ · English'), { target: { value: 'Phenol' } });
    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/registries/CHEM/entries']);
    });

    // Сервер відповів; власник чекає перечитування переліку — діалог ще відкритий.
    reads.arm();
    server.release();
    await waitFor(() => {
      expect(reads.waiting()).toBe(1);
    });
    expect(screen.getByRole('dialog')).not.toBeNull();
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/registries/CHEM/entries']);

    // Перелік перечитано, діалог закрито: форма знята разом із кнопкою.
    reads.open();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(save.isConnected).toBe(false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/registries/CHEM/entries']);
  });
});
