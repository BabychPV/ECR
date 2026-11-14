import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * ✎ B78 F13 P3-2 / B109: «Відкрити HSE400» шукає документ за КОДОМ ШАБЛОНУ — одним запитом переліку з фільтром сервера
 * `templateCode=HSE400` за рік (версія, чинна в році, — правило сервера, D-511), а не шкалами проєктів порціями.
 */

const apiFetch = vi.fn();
const listDocuments = vi.fn();
const fetchAllProjects = vi.fn();

// ⛔ Мутаційний сторож (B109): шкали проєктів (`apiFetch …/template-versions`) і перелік проєктів не мають запитуватися —
// повернення до перебору проєктів робить тести «один запит» червоними.
vi.mock('@/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api/client')>();
  return { ...actual, apiFetch: (path: string) => apiFetch(path) as unknown };
});
vi.mock('@/features/documents/api', () => ({ listDocuments: (params: unknown) => listDocuments(params) as unknown }));
vi.mock('@/features/projects/allProjects', () => ({ fetchAllProjects: () => fetchAllProjects() as unknown }));

const { findHse400Document } = await import('@/features/documents/findHse400Document');
const { EcrApiError } = await import('@/api/client');

interface Doc {
  readonly id: number;
  readonly projectId: number;
  readonly periodStates?: readonly { periodKey: number; state: string }[];
}

function page(items: readonly Doc[], nextCursor: string | null = null) {
  return { items, nextCursor, totalCount: null };
}

const hse400Query = { periodKey: null, year: 2026, templateCode: 'HSE400', limit: 500, cursor: null };

describe('findHse400Document (B78 F13 P3-2, B109)', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    listDocuments.mockReset();
    fetchAllProjects.mockReset();
  });

  it('один запит переліку з фільтром templateCode за рік — без шкал і переліку проєктів', async () => {
    listDocuments.mockResolvedValue(page([{ id: 77, projectId: 2 }]));

    await expect(findHse400Document(1, 2026, 96)).resolves.toBe(77);
    expect(listDocuments).toHaveBeenCalledTimes(1);
    expect(listDocuments).toHaveBeenCalledWith(hse400Query);
    expect(apiFetch).not.toHaveBeenCalled();
    expect(fetchAllProjects).not.toHaveBeenCalled();
  });

  it('спершу проєкт поточного документа; сам поточний документ не відкривається', async () => {
    listDocuments.mockResolvedValue(page([{ id: 11, projectId: 2 }, { id: 96, projectId: 5 }, { id: 55, projectId: 5 }]));

    await expect(findHse400Document(5, 2026, 96)).resolves.toBe(55);
  });

  it('у проєкті поточного документа лише він сам — документ іншого проєкту', async () => {
    listDocuments.mockResolvedValue(page([{ id: 96, projectId: 5 }, { id: 31, projectId: 3 }, { id: 22, projectId: 2 }]));

    await expect(findHse400Document(5, 2026, 96)).resolves.toBe(22);
  });

  it('документів HSE400 за рік немає (або лише поточний) — null', async () => {
    listDocuments.mockResolvedValueOnce(page([]));
    await expect(findHse400Document(null, 2026, 96)).resolves.toBeNull();

    listDocuments.mockResolvedValueOnce(page([{ id: 96, projectId: 5 }]));
    await expect(findHse400Document(5, 2026, 96)).resolves.toBeNull();
  });

  // ✎ B93 (c): кілька документів року — явне правило, а не порядок переліку.
  it('кілька документів року: спершу з періодом панелі, далі найменший Id', async () => {
    listDocuments.mockResolvedValueOnce(page([
      { id: 96, projectId: 5, periodStates: [{ periodKey: 202605, state: 'Draft' }] },
      { id: 40, projectId: 5, periodStates: [{ periodKey: 202601, state: 'Draft' }] },
      { id: 70, projectId: 5, periodStates: [{ periodKey: 202605, state: 'Draft' }] },
    ]));
    await expect(findHse400Document(5, 2026, 96, 202605)).resolves.toBe(70);

    listDocuments.mockResolvedValueOnce(page([{ id: 90, projectId: 5 }, { id: 96, projectId: 5 }, { id: 40, projectId: 5 }]));
    await expect(findHse400Document(5, 2026, 96, 202605)).resolves.toBe(40);
  });

  it('понад одну сторінку — наступні сторінки за курсором, а не втрачений документ', async () => {
    listDocuments
      .mockResolvedValueOnce(page([{ id: 80, projectId: 2 }], 'c1'))
      .mockResolvedValueOnce(page([{ id: 12, projectId: 3 }]));

    await expect(findHse400Document(null, 2026, 96)).resolves.toBe(12);
    expect(listDocuments).toHaveBeenCalledTimes(2);
    expect(listDocuments.mock.calls[1]?.[0]).toEqual({ ...hse400Query, cursor: 'c1' });
  });

  it('відмова API — виняток для панелі', async () => {
    listDocuments.mockRejectedValue(new EcrApiError({ status: 500, title: 'Boom' } as never));

    await expect(findHse400Document(null, 2026, 96)).rejects.toBeInstanceOf(EcrApiError);
  });
});
