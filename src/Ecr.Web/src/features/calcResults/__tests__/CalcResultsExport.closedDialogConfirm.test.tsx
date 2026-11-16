import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { CalcResultsExport } from '@/features/calcResults/CalcResultsExport';
import type { ResultFilters } from '@/features/calcResults/api';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалог «Export to Excel» огляду року, що вже закрився, не ставить другу задачу експорту.
 *
 * ⛔ Діра (зонд на базі d618ea1f6, справжній діалог): постановка відповіла — діалог закрито (`onClose`),
 * але власний `Modal` ще ~200 мс у DOM і кнопка «Export» активна. Другий клік подвійного кліку ставив
 * ДРУГУ задачу побудови книги (другий `POST`), а `jobId` першої затирався — її тост «будується» лишався
 * без розв'язки.
 *
 * Захист — у самому діалозі: кнопка діє лише у відкритому діалозі й не під час запиту.
 *
 * Мутаційний доказ: прибрати `!opened ||` у `CalcResultsExport.tsx` → червоний (`expected 2 to be 1`).
 */

function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <CalcResultsExport
      opened={opened}
      onClose={() => setOpened(false)}
      documentId={96}
      documentKey="HSE301-2026"
      year={2026}
      filters={{} as ResultFilters}
      filtered={false}
    />
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: Export to Excel, що закрився (ще в DOM), не ставить другу задачу', () => {
  it('одна постановка; клік по «Export» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({
      // Задача ще виконується: опитування стану нічого не завершує.
      read: (url) => (url.includes('/api/v1/jobs/') ? { jobId: 'export-1', state: 'Running' } : {}),
      write: () => ({ body: { jobId: 'export-1' } }),
    });
    renderKeepMounted(<Host />);

    const go = screen.getByTestId('cr-export-go');
    fireEvent.click(go);
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    expect(server.sent[0]).toMatch(/^POST /);

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(go).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(go.isConnected).toBe(true);
    fireEvent.click(go);
    await settle();
    expect(server.sent.length).toBe(1);
  });
});
