import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
vi.mock('@/api/client', () => ({ apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init) }));

import { setRegistrySyncPolicy } from '../registrySyncPolicyApi';

describe('features/sources/registrySyncPolicyApi', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(undefined);
  });

  it('D-212: політика синку — PUT /api/v1/sources/{id}/registry/policy з політикою цілком', async () => {
    await setRegistrySyncPolicy(42, {
      onMissingInSource: 'Deactivate',
      validFromAttribute: 'StartDate',
      validToAttribute: null,
      validToInclusive: false,
    });

    expect(apiFetch).toHaveBeenCalledTimes(1);
    const [path, init] = apiFetch.mock.calls[0]!;
    expect(`${init?.method ?? 'GET'} ${path}`).toBe('PUT /api/v1/sources/42/registry/policy');

    // ⛔ «Не синхронізувати» — ЯВНИЙ null у тілі: PUT замінює політику цілком,
    // і відсутнє поле сервер прочитав би так само, але клієнт мусить казати це прямо.
    expect(JSON.parse(String(init?.body))).toEqual({
      onMissingInSource: 'Deactivate',
      validFromAttribute: 'StartDate',
      validToAttribute: null,
      validToInclusive: false,
    });
  });
});
