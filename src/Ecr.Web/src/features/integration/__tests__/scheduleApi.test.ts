import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
vi.mock('@/api/client', () => ({ apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init) }));

import { deleteCollectionSchedule, listCollectionSchedules, updateCollectionSchedule } from '../scheduleApi';

describe('features/integration/scheduleApi', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(undefined);
  });

  it('кожна дія йде на свою адресу своїм методом', async () => {
    await listCollectionSchedules();
    await updateCollectionSchedule(7, { cron: '0 15 2 * * ?', isEnabled: true }, 'AAAAAAAAB9E=');
    await deleteCollectionSchedule(7, 'AAAAAAAAB9E=');

    expect(apiFetch.mock.calls.map(([path, init]) => `${init?.method ?? 'GET'} ${path}`)).toEqual([
      'GET /api/v1/collection-schedules',
      'PUT /api/v1/collection-schedules/7',
      'DELETE /api/v1/collection-schedules/7',
    ]);
    expect(JSON.parse(String(apiFetch.mock.calls[1]?.[1]?.body))).toEqual({
      cron: '0 15 2 * * ?',
      isEnabled: true,
    });
  });

  it('зміна і видалення несуть If-Match із версією рядка, а перелік — ні', async () => {
    await listCollectionSchedules();
    await updateCollectionSchedule(7, { cron: '0 15 2 * * ?', isEnabled: false }, 'AAAAAAAAB9E=');
    await deleteCollectionSchedule(7, 'AAAAAAAAB9E=');

    // ⛔ Без заголовка сервер відповідає 422, а з чужою версією — 409: саме це
    // й робить правку розкладу безпечною для двох відкритих екранів.
    const headers = apiFetch.mock.calls.map(([, init]) => new Headers(init?.headers).get('If-Match'));

    expect(headers).toEqual([null, '"AAAAAAAAB9E="', '"AAAAAAAAB9E="']);
  });
});
