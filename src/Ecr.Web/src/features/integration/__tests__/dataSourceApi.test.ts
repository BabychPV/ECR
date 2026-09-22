import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
vi.mock('@/api/client', () => ({ apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init) }));

import {
  createDataSource,
  deleteDataSource,
  listDataSources,
  type SaveDataSourceBody,
  testDataSource,
  updateDataSource,
} from '../dataSourceApi';

const body: SaveDataSourceBody = {
  code: 'PI_MAIN',
  nameL10n: { en: 'PI AF primary' },
  transport: 'PiWebApi',
  endpoint: 'https://pi.corp.example/piwebapi',
  isActive: true,
};

const RowVersion = 'AAAAAAAAB9E=';

describe('features/integration/dataSourceApi', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(undefined);
  });

  it('кожна дія йде на свою адресу своїм методом', async () => {
    await listDataSources();
    await createDataSource(body);
    await updateDataSource(3, { ...body, isActive: false }, RowVersion);
    await testDataSource(3, 'Перевіряємо після переїзду сервера.');
    await deleteDataSource(3, RowVersion);

    expect(apiFetch.mock.calls.map(([path, init]) => `${init?.method ?? 'GET'} ${path}`)).toEqual([
      'GET /api/v1/data-sources',
      'POST /api/v1/data-sources',
      'PUT /api/v1/data-sources/3',
      'POST /api/v1/data-sources/3/test',
      'DELETE /api/v1/data-sources/3',
    ]);
  });

  it('зміна і видалення несуть If-Match із версією рядка, а перелік, створення й перевірка — ні', async () => {
    await listDataSources();
    await createDataSource(body);
    await updateDataSource(3, body, RowVersion);
    await deleteDataSource(3, RowVersion);
    await testDataSource(3, 'Перевіряємо після переїзду сервера.');

    // ⛔ Без заголовка сервер відповідає 422, а зі старою версією — 409: дві
    // одночасні правки одного з'єднання більше не затирають одна одну мовчки.
    const headers = apiFetch.mock.calls.map(([, init]) => new Headers(init?.headers).get('If-Match'));

    expect(headers).toEqual([null, null, `"${RowVersion}"`, `"${RowVersion}"`, null]);
  });

  it('перевірка з’єднання несе причину, а вимкнення йде зміною, не видаленням', async () => {
    await updateDataSource(3, { ...body, isActive: false }, RowVersion);
    await testDataSource(3, 'Перевіряємо після переїзду сервера.');

    // ⛔ Причина обов'язкова на сервері (422 без неї) і потрапляє в журнал
    // безпеки — тому вона в тілі, а не в адресі: адреса їде в лог проксі.
    expect(JSON.parse(String(apiFetch.mock.calls[1]?.[1]?.body))).toEqual({
      reason: 'Перевіряємо після переїзду сервера.',
    });

    // ⛔ Джерело із сутностями збору сервер не видаляє (409): єдиний спосіб
    // припинити збір — `isActive: false`, і він оборотний.
    expect(JSON.parse(String(apiFetch.mock.calls[0]?.[1]?.body)).isActive).toBe(false);
  });

  it('у жодному тілі запиту немає поля секрету', async () => {
    await createDataSource(body);
    await updateDataSource(3, { ...body, isActive: true }, RowVersion);

    // ⛔ Рішення людини на Q15-06: сховища секретів немає, джерела ходять під
    // службовим обліковим записом. Поле «секрет» у формі означало б знак, за
    // яким пароль поїхав би в базу й у резервну копію.
    const bodies = apiFetch.mock.calls.map(([, init]) => JSON.parse(String(init?.body)) as object);

    expect(bodies).toHaveLength(2);
    expect(bodies.every((sent) => !('secret' in sent) && !('secretName' in sent))).toBe(true);
  });
});
