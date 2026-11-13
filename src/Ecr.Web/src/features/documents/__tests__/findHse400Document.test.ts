import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * ✎ B78 F13 P3-2: «Відкрити HSE400» шукає документ за КОДОМ ШАБЛОНУ проєкту (часова шкала версій), спершу в
 * проєкті поточного документа, а не перший збіг пошуку «HSE400» за назвою.
 */

const apiFetch = vi.fn();
const listDocuments = vi.fn();
const fetchAllProjects = vi.fn();

vi.mock('@/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api/client')>();
  return { ...actual, apiFetch: (path: string) => apiFetch(path) as unknown };
});
vi.mock('@/features/documents/api', () => ({ listDocuments: (params: unknown) => listDocuments(params) as unknown }));
vi.mock('@/features/projects/allProjects', () => ({ fetchAllProjects: () => fetchAllProjects() as unknown }));

const { findHse400Document } = await import('@/features/documents/findHse400Document');
const { EcrApiError } = await import('@/api/client');

function timeline(projectId: number, code: string, fromPeriodKey = 202601) {
  return { projectId, currentVersionId: 1, items: [{ fromPeriodKey, templateCode: code, templateVersionId: 1, version: '1.0' }] };
}

function page(...ids: number[]) {
  return { items: ids.map((id) => ({ id })), nextCursor: null, totalCount: ids.length };
}

describe('findHse400Document (B78 F13 P3-2)', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    listDocuments.mockReset();
    fetchAllProjects.mockReset();
  });

  it('проєкт із шаблоном HSE400 — його документ року; Land і «HSE400» в іншому шаблоні пропущено', async () => {
    fetchAllProjects.mockResolvedValue({ items: [{ id: 1 }, { id: 3 }, { id: 2 }, { id: 4 }], nextCursor: null, totalCount: 4 });
    apiFetch.mockImplementation((path: string) => {
      if (path.endsWith('/1/template-versions')) return Promise.resolve(timeline(1, 'LAND_UI'));
      if (path.endsWith('/3/template-versions')) return Promise.resolve(timeline(3, 'HSE400_COPY'));
      if (path.endsWith('/2/template-versions')) return Promise.resolve(timeline(2, 'HSE400'));
      // Невидимий проєкт — пропуск, а не відмова всього пошуку.
      return Promise.reject(new EcrApiError({ status: 404, title: 'Not found' } as never));
    });
    listDocuments.mockResolvedValue(page(77));

    await expect(findHse400Document(1, 2026, 96)).resolves.toBe(77);
    expect(listDocuments).toHaveBeenCalledTimes(1);
    expect(listDocuments).toHaveBeenCalledWith({ periodKey: null, year: 2026, projectId: 2 });
  });

  it('спершу проєкт поточного документа; сам поточний документ не відкривається', async () => {
    fetchAllProjects.mockResolvedValue({ items: [{ id: 2 }, { id: 5 }], nextCursor: null, totalCount: 2 });
    apiFetch.mockImplementation((path: string) =>
      Promise.resolve(timeline(path.includes('/5/') ? 5 : 2, 'HSE400')),
    );
    listDocuments.mockImplementation((params: { projectId: number }) =>
      Promise.resolve(params.projectId === 5 ? page(96, 55) : page(11)),
    );

    await expect(findHse400Document(5, 2026, 96)).resolves.toBe(55);
    expect(listDocuments.mock.calls[0]?.[0]).toMatchObject({ projectId: 5 });
  });

  it('шаблон HSE400 у проєкті почав діяти лише після року — не той рік; немає — null', async () => {
    fetchAllProjects.mockResolvedValue({ items: [{ id: 2 }], nextCursor: null, totalCount: 1 });
    apiFetch.mockResolvedValue(timeline(2, 'HSE400', 202701));

    await expect(findHse400Document(null, 2026, 96)).resolves.toBeNull();
    expect(listDocuments).not.toHaveBeenCalled();
  });

  // ✎ B93 (a): діє версія, ЧИННА в році (остання з `fromPeriodKey` ≤ кінця року), а не «будь-коли була HSE400».
  it('HSE400, яку пізніший шаблон замінив до кінця року, — не HSE400 у цьому році', async () => {
    fetchAllProjects.mockResolvedValue({ items: [{ id: 2 }, { id: 3 }], nextCursor: null, totalCount: 2 });
    apiFetch.mockImplementation((path: string) => {
      if (path.endsWith('/2/template-versions')) {
        return Promise.resolve({
          projectId: 2,
          currentVersionId: 9,
          items: [
            { fromPeriodKey: 202501, templateCode: 'HSE400', templateVersionId: 1, version: '1.0' },
            { fromPeriodKey: 202601, templateCode: 'LAND_UI', templateVersionId: 9, version: '1.0' },
          ],
        });
      }
      return Promise.resolve(timeline(3, 'HSE400', 202501));
    });
    listDocuments.mockImplementation((params: { projectId: number }) =>
      Promise.resolve(params.projectId === 2 ? page(22) : page(33)),
    );

    await expect(findHse400Document(null, 2026, 96)).resolves.toBe(33);
    expect(listDocuments).toHaveBeenCalledTimes(1);
    expect(listDocuments).toHaveBeenCalledWith({ periodKey: null, year: 2026, projectId: 3 });
  });

  // ✎ B93 (c): кілька документів року в проєкті — явне правило, а не порядок переліку.
  it('кілька документів року: спершу з періодом панелі, далі найменший Id', async () => {
    fetchAllProjects.mockResolvedValue({ items: [{ id: 5 }], nextCursor: null, totalCount: 1 });
    apiFetch.mockResolvedValue(timeline(5, 'HSE400'));
    listDocuments.mockResolvedValueOnce({
      items: [
        { id: 96, periodStates: [{ periodKey: 202605, state: 'Draft' }] },
        { id: 40, periodStates: [{ periodKey: 202601, state: 'Draft' }] },
        { id: 70, periodStates: [{ periodKey: 202605, state: 'Draft' }] },
      ],
      nextCursor: null,
      totalCount: 3,
    });
    await expect(findHse400Document(5, 2026, 96, 202605)).resolves.toBe(70);

    listDocuments.mockResolvedValueOnce(page(90, 96, 40));
    await expect(findHse400Document(5, 2026, 96, 202605)).resolves.toBe(40);
  });

  it('відмова API (не 403/404) — виняток для панелі', async () => {
    fetchAllProjects.mockResolvedValue({ items: [{ id: 2 }], nextCursor: null, totalCount: 1 });
    apiFetch.mockRejectedValue(new EcrApiError({ status: 500, title: 'Boom' } as never));

    await expect(findHse400Document(null, 2026, 96)).rejects.toBeInstanceOf(EcrApiError);
  });
});
