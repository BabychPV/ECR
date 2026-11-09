import { describe, it, expect, vi, afterEach } from 'vitest';
import { deleteProject } from '@/features/projects/deleteProject';

/**
 * ✎ UAT 08.11 (c): типізований виклик видалення проєкту (UI чекає погодження макета).
 * Мутація: змінити метод на POST — червоніє.
 */
afterEach(() => vi.unstubAllGlobals());

describe('deleteProject', () => {
  it('DELETE /api/v1/projects/{id}; 204 — без тіла', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await deleteProject(12);

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(String(url)).toContain('/api/v1/projects/12');
    expect(init.method).toBe('DELETE');
  });
});
