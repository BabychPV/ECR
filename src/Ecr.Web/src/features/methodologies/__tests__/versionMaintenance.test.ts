import { afterEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  deleteMethodologyVersion,
  methodologyCoverage,
  methodologyVersionDiff,
} from '@/features/methodologies/api';

/**
 * Покриття, різниця й видалення версії методології (`BE-25`).
 *
 * ⚠ Адреса й МЕТОД: виклик іншим методом сервер відбив би `405`, а серверні
 * тести ходять у маршрут самі й цього не побачили б.
 */

const sent: { url: string; method: string }[] = [];

function mockServer(status: number, body?: unknown): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({ url: String(url), method: String(init?.method ?? 'GET') });

      return body === undefined
        ? new Response(null, { status })
        : new Response(JSON.stringify(body), {
            status,
            headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
          });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('версія методології: покриття, різниця, видалення', () => {
  it('покриття і різниця читаються GET-ом із версією в адресі й базою в запиті', async () => {
    mockServer(200, { methodologyVersionId: 7, outputs: [], waitingBindings: [] });
    const coverage = await methodologyCoverage(3, 7);

    expect(coverage.methodologyVersionId).toBe(7);
    expect(sent).toEqual([{ url: '/api/v1/methodologies/3/versions/7/coverage', method: 'GET' }]);

    mockServer(200, { baseVersionId: 5, methodologyVersionId: 7, items: [] });
    await methodologyVersionDiff(3, 7, 5);

    expect(sent).toEqual([{ url: '/api/v1/methodologies/3/versions/7/diff?baseVersionId=5', method: 'GET' }]);
  });

  it('видалення йде DELETE на адресу версії', async () => {
    mockServer(204);

    await deleteMethodologyVersion(3, 7);

    expect(sent).toEqual([{ url: '/api/v1/methodologies/3/versions/7', method: 'DELETE' }]);
  });

  it('відмова «не чернетка» доходить як EcrApiError з кодом і причиною', async () => {
    mockServer(409, {
      title: 'conflict',
      status: 409,
      errorCode: 'ECR-CALC-0409',
      correlationId: 'cid',
      messageKey: 'err.ECR-CALC-0409.versionNotDraft',
      reason: 'Published',
    });

    // Ловимо самі, а не `rejects`: так видно, що виклик справді відхилився.
    let caught: unknown = null;
    try {
      await deleteMethodologyVersion(3, 7);
    } catch (error) {
      caught = error;
    }

    expect(caught).toBeInstanceOf(EcrApiError);
    const problem = (caught as EcrApiError).problem;
    expect(problem.status).toBe(409);
    expect(problem.errorCode).toBe('ECR-CALC-0409');
    expect(problem.extensions2?.['reason']).toBe('Published');
  });
});
