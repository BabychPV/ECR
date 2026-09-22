import { afterEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import { deleteDocument } from '@/features/documents/api';

/**
 * Видалення документа-чернетки.
 *
 * ⚠ Адреса й МЕТОД: виклик, що пішов би іншим методом, на сервері дав би `405`,
 * а серверні тести ходять у маршрут самі й цього не побачили б.
 */

const sent: { url: string; method: string; body: unknown }[] = [];

function mockServer(status: number, body?: unknown): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({
        url: String(url),
        method: String(init?.method ?? 'GET'),
        body: init?.body === undefined ? null : init.body,
      });

      return body === undefined
        ? new Response(null, { status })
        : new Response(JSON.stringify(body), {
            status,
            headers: { 'Content-Type': 'application/problem+json' },
          });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('deleteDocument', () => {
  it('іде методом DELETE на адресу документа і без тіла', async () => {
    mockServer(204);

    await deleteDocument(42);

    expect(sent).toEqual([{ url: '/api/v1/documents/42', method: 'DELETE', body: null }]);
  });

  it('відмова «не чернетка» доходить як EcrApiError з кодом і причиною', async () => {
    mockServer(409, {
      title: 'conflict',
      status: 409,
      errorCode: 'ECR-DOC-0409',
      correlationId: 'cid',
      messageKey: 'err.ECR-DOC-0409.deleteNotDraft',
      reason: 'Submitted',
    });

    // ⚠ Ловимо виняток самі, а не `rejects`: так видно, що виклик справді
    // відхилився, а не що твердження проковтнуло успіх.
    let caught: unknown = null;
    try {
      await deleteDocument(42);
    } catch (error) {
      caught = error;
    }

    expect(caught).toBeInstanceOf(EcrApiError);
    const problem = (caught as EcrApiError).problem;
    expect(problem.status).toBe(409);
    expect(problem.errorCode).toBe('ECR-DOC-0409');
    expect(problem.extensions2?.['reason']).toBe('Submitted');
  });
});
