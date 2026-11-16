import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { DocumentPeriodReopenDialog } from '../DocumentPeriodReopenDialog';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Reopen January 2026 for this document…» — один `POST …/period-reopen` на відкриття (зонд: ЗАКРИТО, база
 * 826f654e4).
 *
 * Власник (`useDocumentPeriodReopen`) монтує діалог лише за `{opened && state !== undefined ? … : null}` і знімає його в
 * `onClose`; сам діалог — `<Modal opened>` без закритого стану. Обробник уже звіряє `mutation.isPending`, а `onSuccess`
 * перечитує документ і зрізи ДО закриття — увесь цей час запит «в польоті», тож другий клік нічого не шле.
 *
 * Тест тримає це: без `if (!mutation.isPending)` в обробнику — червоний.
 */

const Reopen = 'POST /api/v1/documents/2/period-reopen';

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: DocumentPeriodReopenDialog — один POST на відкриття', () => {
  it('подвійний клік шле один POST; після успіху діалог знято з дерева', async () => {
    const until = new Date(Date.now() + 86_400_000).toISOString();
    const server = holdWrites({ write: () => ({ body: { id: 1, documentId: 2, periodKey: 202601, openUntil: until } }) });

    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? <DocumentPeriodReopenDialog documentId={2} periodKey={202601} defaultOpenUntil={until} onClose={() => setOpen(false)} /> : null;
    }

    renderKeepMounted(<Host />);
    fireEvent.change(screen.getByTestId('document-period-reopen-reason'), { target: { value: 'Late invoice' } });
    const confirm = screen.getByTestId('document-period-reopen-confirm');

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(server.sent).toEqual([Reopen]);
    });
    await nextTick();
    fireEvent.click(confirm);
    await settle();
    expect(server.sent).toEqual([Reopen]);

    server.release();
    await waitFor(() => {
      expect(confirm.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Reopen]);
  });
});
