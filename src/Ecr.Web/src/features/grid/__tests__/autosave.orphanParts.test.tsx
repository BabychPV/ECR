import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { flushUnsaved } from '@/shared/ui/unsavedSources';
import { cancelAutosave, scheduleAutosave, useDocumentPending } from '../autosave';
import type { PendingEdit } from '../cellPatchRequest';
import { PatchCellsMaxCells } from '../patchParts';
import { pendingSlice, putPendingEdits, resetPending } from '../pendingStore';

/**
 * PSS-17 / рецензія L10, L1: зріз, чия сітка вже розмонтована (аркуш перемкнули посеред великої вставки), зберігає
 * документ (`saveOrphanSlice`) — і понад стелю сервера теж частинами, а не одним `PATCH`, який сервер відхилив би.
 *
 * Мутаційний доказ (не в коміті): у `saveOrphanSlice` замість `saveInParts(versioned, savePart, …)` —
 * `savePart(versioned)` → червоний (один PATCH на 51 000, `422`).
 */

vi.mock('@mantine/notifications', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/notifications')>();
  return { ...actual, notifications: { ...actual.notifications, show: vi.fn(), update: vi.fn(), hide: vi.fn() } };
});

const Table = 4;
const Period = 202609;

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('PSS-17: безхазяйний зріз понад стелю — частинами', () => {
  it('51 000 незбережених комірок без сітки на екрані — два PATCH по черзі, кожен ≤ 50 000; сховище порожнє', async () => {
    const sizes: number[] = [];
    let inFlight = 0;
    let maxInFlight = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        const request = JSON.parse(String(init?.body)) as { rows: { rowKey: string; cells: unknown[] }[] };
        const cells = request.rows.reduce((sum, row) => sum + row.cells.length, 0);
        sizes.push(cells);
        inFlight += 1;
        maxInFlight = Math.max(maxInFlight, inFlight);
        await new Promise((resolve) => setTimeout(resolve, 5));
        inFlight -= 1;

        if (cells > PatchCellsMaxCells) {
          return new Response(JSON.stringify({ title: 'x', status: 422, errorCode: 'ECR-REQ-0422', correlationId: 'c' }), {
            status: 422,
            headers: { 'Content-Type': 'application/problem+json' },
          });
        }

        const rowVersions = Object.fromEntries(request.rows.map((row) => [row.rowKey, 'v2']));
        return new Response(JSON.stringify({ appliedCells: cells, rowVersions, validation: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    renderHook(() => useDocumentPending(9), { wrapper });

    const edits: PendingEdit[] = [];
    for (let r = 0; r < 1020; r++) {
      for (let c = 0; c < 50; c++) {
        edits.push({ rowKey: `r${String(r)}`, columnCode: `C${String(c)}`, value: r + c, isEmpty: false, baseVersion: 'v1' });
      }
    }
    putPendingEdits(Table, Period, edits);
    scheduleAutosave();

    await waitFor(() => expect(pendingSlice(Table, Period).size).toBe(0), { timeout: 10_000 });

    expect(sizes).toEqual([50_000, 1_000]);
    expect(maxInFlight).toBe(1);
  }, 30_000);

  it('L2: вихід зі сторінки, доки йде пакет частинами, ЧЕКАЄ на нього довше за звичні 3 с — і йде мовчки, коли все збережено', async () => {
    // Кожна частина «летить» довше за таймаут виходу (тут 40 мс замість 3 с).
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        const request = JSON.parse(String(init?.body)) as { rows: { rowKey: string; cells: unknown[] }[] };
        await new Promise((resolve) => setTimeout(resolve, 120));
        const rowVersions = Object.fromEntries(request.rows.map((row) => [row.rowKey, 'v2']));
        return new Response(
          JSON.stringify({ appliedCells: request.rows.length, rowVersions, validation: [] }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }),
    );

    renderHook(() => useDocumentPending(9), { wrapper });

    const edits: PendingEdit[] = [];
    for (let r = 0; r < 2000; r++) {
      for (let c = 0; c < 60; c++) {
        edits.push({ rowKey: `r${String(r)}`, columnCode: `C${String(c)}`, value: r + c, isEmpty: false, baseVersion: 'v1' });
      }
    }
    putPendingEdits(Table, Period, edits);

    // Те, що кличе `UnsavedGuard` при спробі піти зі сторінки.
    const saved = await flushUnsaved(40);

    expect(saved).toBe(true);
    expect(pendingSlice(Table, Period).size).toBe(0);
  }, 30_000);
});
