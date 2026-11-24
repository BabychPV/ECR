import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import type { RegistryDefDto } from '@/api/types';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { EntryDrawer } from '../EntryDrawer';
import { storedRows } from './fixtures';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: шторка запису довідника → «Edit entry» (`RegistryEntryEditor`) — один `POST …/entries` на збереження (зонд:
 * ЗАКРИТО, база 826f654e4).
 *
 * Шторка сама нічого не пише; її редактор назви змонтований завжди (`opened={editing === 'name'}`), але форма в ньому —
 * лише поки діалог відкритий (`{opened && …}`), тож у закритому `Modal` кнопки «Save» немає. Обробник звіряє
 * `upsert.isPending`, а `onSuccess` перечитує записи ДО закриття. «Validity» (`ValidityEditor`) — B145b.
 *
 * Тест тримає це: без `if (upsert.isPending) return;` — червоний; без `{opened && …}` — червоний (кнопка закритого
 * діалогу лишилась би в DOM і слала б другий POST).
 */

const Registry = { id: 1, code: 'FLARE_CASE', nameL10n: { values: { en: 'Streams' } }, isTemporal: false, fields: [] } as unknown as RegistryDefDto;
const Row = storedRows[0]!;
const Save = 'POST /api/v1/registries/FLARE_CASE/entries';

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: EntryDrawer → Edit entry — один POST на збереження', () => {
  it('подвійний клік шле один POST; після успіху у закритому діалозі кнопки немає', async () => {
    const server = holdWrites({
      read: (url) => {
        if (url.endsWith(`/entries/${String(Row.id)}`)) return { id: Row.id, code: Row.code, displayL10n: { values: { en: Row.display } }, values: {} };
        if (url.includes('/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true }];
        if (url.endsWith('/rows') || url.endsWith('/history')) return { items: [], nextCursor: null, totalCount: 0 };

        return [];
      },
      write: () => ({ body: { id: Row.id } }),
    });
    renderKeepMounted(
      <EntryDrawer registry={Registry} row={Row} fields={[]} asOf={null} readOnly={false} />,
      `/x?panel=entry-${String(Row.id)}`,
    );

    fireEvent.click(await screen.findByRole('button', { name: /registries\.editEntry/ }));
    const dialog = await screen.findByRole('dialog', { name: /registries\.editEntry/ });
    const name = await within(dialog).findByDisplayValue(Row.display);
    fireEvent.change(name, { target: { value: `${Row.display} (renamed)` } });
    const save = within(dialog).getByRole('button', { name: /common\.save/ });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([Save]);
    });
    await nextTick();
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([Save]);

    server.release();
    await waitFor(() => {
      expect(save.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Save]);
  });
});
