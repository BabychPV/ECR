import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  CORRELATION_HEADER,
  EcrApiError,
  apiFetchResponse,
  setLoginRedirect,
  setRequestLanguageTag,
} from '@/api/client';

/**
 * `apiFetchResponse` — сирий `Response` для тіл, що не є JSON (CSV-вивантаження).
 *
 * ⛔ Предмет — що він іде ТИМ САМИМ шляхом, що й `apiFetch`: кореляція, мова,
 * cookie, перенаправлення на 401 і розбір `problem+json`. Власний `fetch` поза
 * клієнтом втратив би все це мовчки, і відмова експорту стала б «HTTP 422».
 */
afterEach(() => {
  vi.unstubAllGlobals();
  setRequestLanguageTag(null);
});

describe('apiFetchResponse', () => {
  it('200 → той самий Response, з кореляцією, мовою і cookie', async () => {
    const fetchMock = vi.fn(async () =>
      Promise.resolve(new Response('a,b\n1,2\n', { status: 200, headers: { 'Content-Type': 'text/csv' } })),
    );
    vi.stubGlobal('fetch', fetchMock);
    setRequestLanguageTag('kk');

    const response = await apiFetchResponse('/api/v1/x.csv');

    expect(await response.text()).toBe('a,b\n1,2\n');
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    const headers = new Headers(init.headers);
    expect(headers.get(CORRELATION_HEADER)).not.toBeNull();
    expect(headers.get('Accept-Language')).toBe('kk');
    expect(init.credentials).toBe('include');
  });

  it('422 problem+json → EcrApiError з кодом і розширеннями сервера', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response(
            JSON.stringify({
              title: 'Too large',
              status: 422,
              errorCode: 'ECR-REQ-0422',
              correlationId: 'cid-1',
              messageKey: 'err.ECR-REQ-0422.auditExportTooLarge',
              total: 900000,
              max: 500000,
            }),
            { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
          ),
        ),
      ),
    );

    const error = await apiFetchResponse('/api/v1/x.csv').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(EcrApiError);
    const problem = (error as EcrApiError).problem;
    expect(problem.errorCode).toBe('ECR-REQ-0422');
    expect(problem.extensions2?.['total']).toBe(900000);
  });

  it('401 → перенаправлення на вхід, як у apiFetch', async () => {
    const redirect = vi.fn();
    setLoginRedirect(redirect);
    vi.stubGlobal('fetch', vi.fn(async () => Promise.resolve(new Response(null, { status: 401 }))));

    const error = await apiFetchResponse('/api/v1/x.csv').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(EcrApiError);
    expect(redirect).toHaveBeenCalledTimes(1);
  });
});
