import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
vi.mock('@/api/client', () => ({
  apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init),
}));

import {
  createRowWindowMap,
  deleteRowWindowMap,
  fetchRowWindowMap,
  fetchRowWindowMaps,
  updateRowWindowMap,
} from '../rowWindowApi';

/** Метод і адреса першого виклику `apiFetch` — те, що бачить сервер. */
function firstCall(): { line: string; init: RequestInit | undefined } {
  const [path, init] = apiFetch.mock.calls[0]!;

  return { line: `${init?.method ?? 'GET'} ${path}`, init };
}

describe('features/sources/rowWindowApi', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(undefined);
  });

  it('HSE301 A1: перелік — GET /api/v1/row-window-maps, sourceEntityId у рядку запиту лише коли заданий', async () => {
    await fetchRowWindowMaps(42);
    expect(firstCall().line).toBe('GET /api/v1/row-window-maps?sourceEntityId=42');

    apiFetch.mockClear();
    await fetchRowWindowMaps();
    expect(firstCall().line).toBe('GET /api/v1/row-window-maps?');
  });

  it('HSE301 A1: одна прив\'язка — GET /api/v1/row-window-maps/{id}', async () => {
    await fetchRowWindowMap(5);

    expect(firstCall().line).toBe('GET /api/v1/row-window-maps/5');
  });

  it('HSE301 A1: створення — POST з тілом як є, JSON', async () => {
    await createRowWindowMap({
      tableDefId: 1,
      targetColumnDefId: 2,
      startColumnDefId: 3,
      endColumnDefId: 4,
      summary: 'Total',
      isStep: false,
      targetUnitId: 9,
      sources: [{ selectorValue: null, sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
    });

    const { line, init } = firstCall();
    expect(line).toBe('POST /api/v1/row-window-maps');
    expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json');
    expect(JSON.parse(String(init?.body))).toMatchObject({ tableDefId: 1, summary: 'Total', sources: [{ sourceField: 'Flare.Total' }] });
  });

  it('HSE301 A1: заміна — PUT з rowVersion, видалення — DELETE за id', async () => {
    await updateRowWindowMap(5, {
      startColumnDefId: 3,
      endColumnDefId: 4,
      summary: 'Average',
      isStep: true,
      targetUnitId: 9,
      isActive: false,
      rowVersion: '0000000000000ABC',
      sources: [],
    });

    const put = firstCall();
    expect(put.line).toBe('PUT /api/v1/row-window-maps/5');
    expect(JSON.parse(String(put.init?.body))).toMatchObject({ isActive: false, rowVersion: '0000000000000ABC' });

    apiFetch.mockClear();
    await deleteRowWindowMap(5);
    expect(firstCall().line).toBe('DELETE /api/v1/row-window-maps/5');
  });
});
