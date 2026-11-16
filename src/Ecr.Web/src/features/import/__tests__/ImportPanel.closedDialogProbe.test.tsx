import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { ImportPanel } from '@/features/import/ImportPanel';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «null-захист в обробнику»: «Apply» перегляду імпорту книги, що закрився (ще в DOM) або ще чекає
 * перечитування документа, не застосовує імпорт удруге.
 *
 * Зонд (справжня панель, `keepMountedModalsTheme`, запис тримає тест, перечитування — ворота):
 * - вікно 1: `settled.run` — одна дія в польоті (`inFlight`), повтор нічого;
 * - вікно 2: після відповіді панель чекає `invalidateQueries(['document', …])`, діалог відкритий — `run`
 *   ще не відпустив `inFlight` (чекає завершення `mutateAsync` разом з `onSuccess`);
 * - вікно 3: тіло діалогу живе лише при `preview !== null` (з нього береться `previewToken`) — кнопка
 *   від'єднана. ЗАКРИТО, один `POST /api/v1/documents/1/import/apply`.
 */

function DocumentCard(): null {
  useQuery({ queryKey: ['document', 1, 202609], queryFn: () => apiFetch<unknown>('/api/v1/documents/1') });
  return null;
}

const preview = {
  previewToken: 'tok-1',
  changes: [{ rowKey: 'R1', columnCode: 'C1', oldValue: '5', newValue: '6', tableCode: 'T2' }],
  rejected: [],
  conflicts: [],
  overwritable: [],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: ImportPanel «Apply» — один запис на перегляд', () => {
  it('подвійний клік, очікування документа і закритий діалог — рівно один POST …/import/apply', async () => {
    const server = holdWrites({
      write: (_method, url) =>
        url.endsWith('/import/preview')
          ? { body: preview }
          : { body: { appliedCells: 1, rowVersions: { R1: 'v2' }, validation: [] } },
    });
    const gate = gateReads();
    const view = renderKeepMounted(
      <>
        <DocumentCard />
        <ImportPanel documentId={1} periodKey={202609} />
      </>,
    );

    const input = view.container.querySelector('input[type="file"]');
    if (input === null) throw new Error('немає поля вибору файлу');
    fireEvent.change(input, { target: { files: [new File(['x'], 'book.xlsx')] } });
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/documents/1/import/preview']);
    });
    server.release();

    const dialog = await screen.findByRole('dialog');
    const apply = within(dialog).getByRole('button', { name: '⟦import.apply⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => apply,
      isOpen: () => screen.queryByRole('dialog') !== null,
      expected: ['POST /api/v1/documents/1/import/preview', 'POST /api/v1/documents/1/import/apply'],
      awaitedRead: '/api/v1/documents/1',
      afterClose: 'detached',
    });
    expect(server.bodies[1]).toEqual({ previewToken: 'tok-1' });
  });
});
