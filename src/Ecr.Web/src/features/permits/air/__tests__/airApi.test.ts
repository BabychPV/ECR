import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from '@/api/client';
import {
  analyseAirImport,
  applyAirImport,
  getAirLimits,
  getAirScopeCodes,
  getImportProfile,
  getNaeTree,
  listImportProfiles,
  previewAirImport,
  saveImportProfile,
} from '../airApi';
import { xlsx } from './airFixtures';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));

const fetchMock = vi.mocked(apiFetch);

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({} as never);
});

function formOf(call: number): FormData {
  const init = fetchMock.mock.calls[call]?.[1];
  expect(init?.body).toBeInstanceOf(FormData);

  return init?.body as FormData;
}

describe('airApi: адреси й методи', () => {
  it('читання', async () => {
    await getAirLimits(5);
    await listImportProfiles();
    await getImportProfile('ERA TABLE/33');
    await getNaeTree();
    await getAirScopeCodes();
    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual([
      '/api/v1/permits/5/air-limits',
      '/api/v1/air-limits/import-profiles',
      '/api/v1/air-limits/import-profiles/ERA%20TABLE%2F33',
      '/api/v1/registries/NAE_TREE/tree',
      '/api/v1/air-limits/scope-codes',
    ]);
  });

  it('збереження профілю: PUT із JSON', async () => {
    await saveImportProfile('X', { nameL10n: { en: 'X' }, spec: { fields: [] }, rowVersion: null });
    const [path, init] = fetchMock.mock.calls[0]!;
    expect(path).toBe('/api/v1/air-limits/import-profiles/X');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(init?.body as string)).toEqual({ nameL10n: { en: 'X' }, spec: { fields: [] }, rowVersion: null });
  });
});

describe('airApi: multipart імпорту', () => {
  const options = {
    profileCode: 'ERA_TABLE_33',
    region: 'ATYRAU',
    area: '-',
    resolutions: [{ kind: 'nae', code: '3544', action: 'create', createUnder: '3' }],
    confirmedOtherOwners: ['7'],
    expectedFingerprint: 'fp-1',
  } as const;

  it('analyse/preview/apply: частини file і request (JSON), POST, без ручного Content-Type', async () => {
    const file = xlsx();
    await analyseAirImport(file, options);
    await previewAirImport(5, file, options);
    await applyAirImport(5, file, options);

    expect(fetchMock.mock.calls.map((call) => [call[0], call[1]?.method])).toEqual([
      ['/api/v1/air-limits/import/analyse', 'POST'],
      ['/api/v1/permits/5/air-limits/import/preview', 'POST'],
      ['/api/v1/permits/5/air-limits/import/apply', 'POST'],
    ]);
    for (const call of [0, 1, 2]) {
      const form = formOf(call);
      expect((form.get('file') as File).name).toBe('limits.xlsx');
      expect(JSON.parse(form.get('request') as string)).toEqual(options);
      expect(fetchMock.mock.calls[call]?.[1]?.headers).toBeUndefined();
    }
  });
});
