import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { ColumnGroupsDialog } from '@/features/templates/ColumnGroupsDialog';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Column groups» — один `PUT …/header-groups` на збереження.
 *
 * Зонд (база 826f654e4):
 * - закриття — ЗАКРИТО: власник (`TemplateVersionPage`) монтує діалог лише за `{groupsTable !== null && …}`, сам діалог —
 *   `<Modal opened>` без закритого стану; після успіху кнопка від'єднана;
 * - подвійний клік — ВІДКРИТО: «Save» кликав `save.mutate` без перевірки `isPending`, спінер — лише через 100 мс, а
 *   `onSuccess` ще й чекає перечитування версії: другий клік (наступний тік) слав другий PUT з тим самим деревом.
 *
 * Мутаційний доказ: прибрати `if (save.isPending) return;` у `ColumnGroupsDialog.tsx` → червоний
 * (`expected [ …(2) ] to deeply equal [ Array(1) ]`).
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

describe('B149: ColumnGroupsDialog — один PUT на збереження', () => {
  it('подвійний клік шле один PUT; після успіху діалог знято з дерева', async () => {
    const server = holdWrites({ read: () => [], write: () => ({ body: { presentationRevision: 2, groups: [] } }) });

    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? <ColumnGroupsDialog templateVersionId={5} tableId={9} groups={[]} onClose={() => setOpen(false)} /> : null;
    }

    renderKeepMounted(<Host />);
    const save = screen.getByRole('button', { name: /common\.save|colgroups\.save/ });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    const first = server.sent[0];
    expect(first).toMatch(/^PUT \/api\/v1\/template-versions\/5\/.*9/);
    await nextTick();
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([first]);

    server.release();
    await waitFor(() => {
      expect(save.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([first]);
  });
});
