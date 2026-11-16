import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { MethodologyCategoriesDialog } from '../MethodologyCategoriesDialog';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу: доказ лише про обробник і `disabled={busy}`.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Constant categories» — менеджер, що лишається відкритим після запису (зонд: ЗАКРИТО, база 826f654e4).
 *
 * Власник (`MethodologyContentPanels`) монтує діалог лише за `{categoriesDialog !== null && …}` з `opened` літералом, тож
 * закритого стану немає. Кнопки запису вимкнені на час запиту (`disabled={busy}`, `busy = save.isPending || …` — з
 * наступним рендером), а після успіху чернетку очищено вже ПІСЛЯ перечитування переліку: другий клік подвійного кліку
 * і клік після успіху запиту не шлють.
 *
 * Тест тримає це: без `|| busy` у `disabled` кнопки «Add» — червоний.
 */

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: MethodologyCategoriesDialog — один запис на «Add»', () => {
  it('подвійний клік шле один запит; після успіху чернетка порожня — «Add» недоступна', async () => {
    const server = holdWrites({ read: () => [], write: () => ({ body: { code: 'FUEL', description: null, isRetired: false, ordinal: 1, constantCount: 0 } }) });
    renderKeepMounted(<MethodologyCategoriesDialog methodologyId={4} versionId={40} editable opened onClose={() => undefined} />);

    fireEvent.change(await screen.findByRole('textbox', { name: /methodologies\.code/ }), { target: { value: 'FUEL' } });
    const add = screen.getByRole('button', { name: /constCategories\.add/ });

    fireEvent.click(add);
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    const first = server.sent[0];
    await nextTick();
    fireEvent.click(add);
    await settle();
    expect(server.sent).toEqual([first]);

    server.release();
    await waitFor(() => {
      expect((screen.getByRole('textbox', { name: /methodologies\.code/ }) as HTMLInputElement).value).toBe('');
    });
    fireEvent.click(screen.getByRole('button', { name: /constCategories\.add/ }));
    await settle();
    expect(server.sent).toEqual([first]);
  });
});
