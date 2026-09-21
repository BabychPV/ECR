import { afterEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError, apiFetch } from '@/api/client';

/**
 * `Retry-After` відмови `429` доходить до викликача як `problem.retryAfterSeconds`.
 *
 * ⚠ Сервер (`LoginRateLimiting.RejectAsync`) пише строк ЛИШЕ заголовком — у
 * тілі `problem+json` його немає. Тіло тут рівно таке, як пише сервер.
 */
function respond(status: number, headers: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            title: 'Too many requests',
            status,
            errorCode: status === 429 ? 'ECR-REQ-0429' : 'ECR-SYS-0500',
            correlationId: 'cid',
          }),
          { status, headers: { 'Content-Type': 'application/problem+json', ...headers } },
        ),
      ),
    ),
  );
}

async function problemOf(status: number, headers: Record<string, string>) {
  respond(status, headers);
  const error = await apiFetch('/api/v1/search?q=ab').catch((e: unknown) => e);
  expect(error).toBeInstanceOf(EcrApiError);

  return (error as EcrApiError).problem;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Retry-After у відмові 429', () => {
  it('429 з `Retry-After: 7` → 7 секунд', async () => {
    const problem = await problemOf(429, { 'Retry-After': '7' });

    expect(problem.retryAfterSeconds).toBe(7);
    expect(problem.errorCode).toBe('ECR-REQ-0429');
  });

  it('429 без заголовка → поля немає зовсім', async () => {
    const problem = await problemOf(429, {});

    // ⚠ Під `exactOptionalPropertyTypes` «немає поля» ≠ «поле є й порожнє».
    expect('retryAfterSeconds' in problem).toBe(false);
  });

  it('429 з некоректним `Retry-After: abc` → поля немає', async () => {
    const problem = await problemOf(429, { 'Retry-After': 'abc' });

    expect('retryAfterSeconds' in problem).toBe(false);
  });

  it('429 з HTTP-date → поля немає (дату свідомо не розбираємо)', async () => {
    const problem = await problemOf(429, { 'Retry-After': 'Wed, 21 Oct 2026 07:28:00 GMT' });

    expect('retryAfterSeconds' in problem).toBe(false);
  });

  it('429 з від’ємним чи дробовим значенням → поля немає', async () => {
    expect('retryAfterSeconds' in (await problemOf(429, { 'Retry-After': '-3' }))).toBe(false);
    expect('retryAfterSeconds' in (await problemOf(429, { 'Retry-After': '1.5' }))).toBe(false);
  });

  it('500 із заголовком → поля немає: строк має сенс лише для 429', async () => {
    const problem = await problemOf(500, { 'Retry-After': '7' });

    expect('retryAfterSeconds' in problem).toBe(false);
  });
});
