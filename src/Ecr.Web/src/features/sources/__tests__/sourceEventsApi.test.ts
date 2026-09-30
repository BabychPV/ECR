import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
const apiEnqueue = vi.fn<(path: string, body?: unknown) => Promise<unknown>>();
vi.mock('@/api/client', () => ({
  apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init),
  apiEnqueue: (path: string, body?: unknown) => apiEnqueue(path, body),
}));

import {
  createSourceEventMap,
  deleteSourceEventMap,
  fetchEventTemplates,
  fetchSourceEventMap,
  fetchSourceEventMaps,
  fetchSourceEvents,
  probeSourceEvents,
  syncSourceEvents,
  updateSourceEventMap,
} from '../sourceEventsApi';

/** Метод і адреса першого виклику `apiFetch` — те, що бачить сервер. */
function firstCall(): { line: string; init: RequestInit | undefined } {
  const [path, init] = apiFetch.mock.calls[0]!;

  return { line: `${init?.method ?? 'GET'} ${path}`, init };
}

describe('features/sources/sourceEventsApi', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiEnqueue.mockReset();
    apiFetch.mockResolvedValue(undefined);
    apiEnqueue.mockResolvedValue({ jobId: 'job-1' });
  });

  it('HSE301 A6: каталог шаблонів — GET /api/v1/data-sources/{id}/event-templates', async () => {
    await fetchEventTemplates(7);

    expect(firstCall().line).toBe('GET /api/v1/data-sources/7/event-templates');
  });

  it('HSE301 A6: проба подій — POST з тілом запиту й без зайвих полів', async () => {
    await probeSourceEvents(7, { template: 'FlareEvent', maxEvents: 5 });

    const { line, init } = firstCall();
    expect(line).toBe('POST /api/v1/data-sources/7/probe-events');
    expect(JSON.parse(String(init?.body))).toEqual({
      template: 'FlareEvent',
      maxEvents: 5,
    });
  });

  it('HSE301 A6: таблиця подій — фільтри в рядку запиту, статуси повторюваним параметром', async () => {
    await fetchSourceEvents(5, {
      mapId: 10,
      status: ['Missing', 'Open'],
      fromUtc: '2026-01-28T00:00:00Z',
      periodKey: 202601,
      limit: 25,
      cursor: 'abc=',
    });

    const url = new URL(firstCall().line.replace('GET ', 'http://x'));
    expect(url.pathname).toBe('/api/v1/sources/5/source-events');
    expect(url.searchParams.getAll('status')).toEqual(['Missing', 'Open']);
    expect(url.searchParams.get('mapId')).toBe('10');
    expect(url.searchParams.get('fromUtc')).toBe('2026-01-28T00:00:00Z');
    expect(url.searchParams.get('periodKey')).toBe('202601');
    expect(url.searchParams.get('limit')).toBe('25');
    expect(url.searchParams.get('cursor')).toBe('abc=');

    // Без фільтрів запит порожній: нічого зайвого не відправляється.
    apiFetch.mockClear();
    await fetchSourceEvents(5);
    expect(new URL(firstCall().line.replace('GET ', 'http://x')).search).toBe('');
  });

  it('HSE301 A6: «Отримати з PI зараз» — POST у чергу, а не звичайний запит', async () => {
    const accepted = await syncSourceEvents(5);

    expect(apiEnqueue).toHaveBeenCalledWith('/api/v1/sources/5/source-events/sync', undefined);
    expect(apiFetch).not.toHaveBeenCalled();
    expect(accepted).toEqual({ jobId: 'job-1' });
  });

  it('HSE301 A6: CRUD мапінгу — методи й адреси', async () => {
    await fetchSourceEventMaps(5);
    expect(firstCall().line).toBe('GET /api/v1/source-event-maps?sourceEntityId=5');

    apiFetch.mockClear();
    await fetchSourceEventMaps();
    expect(firstCall().line).toBe('GET /api/v1/source-event-maps?');

    apiFetch.mockClear();
    await fetchSourceEventMap(9);
    expect(firstCall().line).toBe('GET /api/v1/source-event-maps/9');

    apiFetch.mockClear();
    await createSourceEventMap({
      sourceEntityId: 5,
      documentId: 42,
      tableDefId: 10,
      volumeMode: 'None',
      fields: [],
    });
    expect(firstCall().line).toBe('POST /api/v1/source-event-maps');

    apiFetch.mockClear();
    await updateSourceEventMap(9, {
      volumeMode: 'RowWindow',
      isActive: false,
      fields: [],
    });
    const update = firstCall();
    expect(update.line).toBe('PUT /api/v1/source-event-maps/9');
    expect(JSON.parse(String(update.init?.body))).toMatchObject({
      isActive: false,
    });

    apiFetch.mockClear();
    await deleteSourceEventMap(9);
    expect(firstCall().line).toBe('DELETE /api/v1/source-event-maps/9');
  });
});
