import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
vi.mock('@/api/client', () => ({ apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init) }));

import {
  createCollectionSchedule,
  deleteCollectionSchedule,
  listCollectionSchedules,
  updateCollectionSchedule,
} from '../scheduleApi';

describe('features/integration/scheduleApi', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(undefined);
  });

  it('кожна дія йде на свою адресу своїм методом', async () => {
    await listCollectionSchedules();
    await createCollectionSchedule({ sourceEntityId: 42, cron: '0 15 2 * * ?', isEnabled: true });
    await updateCollectionSchedule(7, { cron: '0 15 2 * * ?', isEnabled: true }, 'AAAAAAAAB9E=');
    await deleteCollectionSchedule(7, 'AAAAAAAAB9E=');

    expect(apiFetch.mock.calls.map(([path, init]) => `${init?.method ?? 'GET'} ${path}`)).toEqual([
      'GET /api/v1/collection-schedules',
      'POST /api/v1/collection-schedules',
      'PUT /api/v1/collection-schedules/7',
      'DELETE /api/v1/collection-schedules/7',
    ]);

    // ⛔ Створення несе сутність джерела: без неї сервер не знає, ЩО збирати, а
    // адреса в обох дій однакова — різниця лише в тілі.
    expect(JSON.parse(String(apiFetch.mock.calls[1]?.[1]?.body))).toEqual({
      sourceEntityId: 42,
      cron: '0 15 2 * * ?',
      isEnabled: true,
    });
    expect(JSON.parse(String(apiFetch.mock.calls[2]?.[1]?.body))).toEqual({
      cron: '0 15 2 * * ?',
      isEnabled: true,
    });
  });

  it('перелік із кодом зʼєднання передає його параметром dataSource, закодованим', async () => {
    await listCollectionSchedules({ dataSource: 'PI WEST&1' });
    await listCollectionSchedules({ dataSource: '' });
    await listCollectionSchedules({ dataSource: '   ' });
    await listCollectionSchedules({});

    // ⛔ Фільтрує сервер: відбір на клієнті після стелі переліку дав би неповну
    // вкладку. Порожній код чи самі пробіли — без параметра, тобто всі розклади.
    expect(apiFetch.mock.calls.map(([path]) => path)).toEqual([
      '/api/v1/collection-schedules?dataSource=PI%20WEST%261',
      '/api/v1/collection-schedules',
      '/api/v1/collection-schedules',
      '/api/v1/collection-schedules',
    ]);
  });

  it('контекст react-query першим аргументом (queryFn напряму) не стає фільтром', async () => {
    // ⛔ Саме так `useCollectionSchedules` і кличе функцію: `queryFn:
    // listCollectionSchedules`. Позиційний рядковий параметр узяв би цей
    // об'єкт за код і послав `?dataSource=[object Object]`.
    const context = {
      queryKey: ['collection-schedules'] as const,
      signal: new AbortController().signal,
      meta: undefined,
    };
    await listCollectionSchedules(context);

    expect(apiFetch.mock.calls.map(([path]) => path)).toEqual(['/api/v1/collection-schedules']);
  });

  it('зміна і видалення несуть If-Match із версією рядка, а перелік і створення — ні', async () => {
    await listCollectionSchedules();
    await createCollectionSchedule({ sourceEntityId: 42, cron: '0 15 2 * * ?', isEnabled: true });
    await updateCollectionSchedule(7, { cron: '0 15 2 * * ?', isEnabled: false }, 'AAAAAAAAB9E=');
    await deleteCollectionSchedule(7, 'AAAAAAAAB9E=');

    // ⛔ Без заголовка сервер відповідає 422, а з чужою версією — 409: саме це
    // й робить правку розкладу безпечною для двох відкритих екранів.
    // ⚠ Створення заголовка не несе навмисно: воно нічого не перезаписує, і
    // версії рядка, якого ще немає, взяти нізвідки.
    const headers = apiFetch.mock.calls.map(([, init]) => new Headers(init?.headers).get('If-Match'));

    expect(headers).toEqual([null, null, '"AAAAAAAAB9E="', '"AAAAAAAAB9E="']);
  });
});
