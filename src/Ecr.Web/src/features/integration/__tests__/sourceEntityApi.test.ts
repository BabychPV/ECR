import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
vi.mock('@/api/client', () => ({ apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init) }));

import { bindSourceEntityRegistry, createSourceEntity } from '../sourceEntityApi';

describe('features/integration/sourceEntityApi', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(undefined);
  });

  it('ФВ-13.11: заведення — POST /api/v1/sources із позицією каталогу; прив\'язка — PUT /sources/{id}/registry', async () => {
    await createSourceEntity({
      dataSourceId: 7,
      code: 'Flare_01',
      displayName: null,
      entityPath: '\\\\AF\\Flare_01',
      sourceKind: null,
    });
    await bindSourceEntityRegistry(42, 70);
    await bindSourceEntityRegistry(42, null);

    expect(apiFetch.mock.calls.map(([path, init]) => `${init?.method ?? 'GET'} ${path}`)).toEqual([
      'POST /api/v1/sources',
      'PUT /api/v1/sources/42/registry',
      'PUT /api/v1/sources/42/registry',
    ]);

    // ⛔ Відв'язка — це ЯВНИЙ `null` у тілі, а не відсутнє поле: сервер
    // приймає `{ registryDefId: null }` як «зняти прив'язку».
    expect(apiFetch.mock.calls.map(([, init]) => JSON.parse(String(init?.body)))).toEqual([
      { dataSourceId: 7, code: 'Flare_01', displayName: null, entityPath: '\\\\AF\\Flare_01', sourceKind: null },
      { registryDefId: 70 },
      { registryDefId: null },
    ]);
  });
});
