import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  EcrApiError,
  LOGOUT_PATH,
  apiFetch,
  beginSignOut,
  resetSignOutForTests,
  setLoginRedirect,
} from '@/api/client';

/**
 * A2-06: після кліку «Вийти» вкладка ще живе до перезавантаження на `/login`, і опитування
 * («My tasks»), фонові перезапити встигали піти в мережу вже без cookie — `401`, а кожен
 * такий `401` ще й запускав власне перенаправлення з `?from=…`.
 *
 * ⛔ Мутаційний доказ: приберіть перевірку `signedOut` в `apiFetchRaw` (`client.ts`) — другий
 * тест падає: `fetch` викликано з `/api/v1/jobs`, а перенаправлення на вхід спрацювало.
 */
afterEach(() => {
  resetSignOutForTests();
  vi.unstubAllGlobals();
  setLoginRedirect(() => undefined);
});

function stubFetch(status: number): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () => new Response(null, { status }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('apiFetch після виходу (A2-06)', () => {
  it('контроль: до виходу запит іде в мережу', async () => {
    const fetchMock = stubFetch(204);

    await apiFetch('/api/v1/jobs?mine=true');

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('після beginSignOut запит не надсилається, викликач отримує 401 без перенаправлення', async () => {
    const fetchMock = stubFetch(401);
    const redirect = vi.fn();
    setLoginRedirect(redirect);

    beginSignOut();

    const failure = await apiFetch('/api/v1/jobs?mine=true').catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(EcrApiError);
    expect((failure as EcrApiError).problem.status).toBe(401);
    expect((failure as EcrApiError).problem.errorCode).toBe('ECR-AUTH-0401');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(redirect).not.toHaveBeenCalled();
  });

  it('сам запит виходу після beginSignOut іде', async () => {
    const fetchMock = stubFetch(204);

    beginSignOut();
    await apiFetch(LOGOUT_PATH, { method: 'POST' });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0]?.[0])).toBe(LOGOUT_PATH);
  });
});
