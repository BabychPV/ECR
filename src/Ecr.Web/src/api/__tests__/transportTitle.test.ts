import { describe, it, expect, vi, afterEach } from 'vitest';
import { notifications } from '@mantine/notifications';
import { apiFetch, EcrApiError } from '@/api/client';
import { problemText } from '@/shared/ui/problemText';
import { showApiError } from '@/shared/ui/notify';

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

/**
 * `R-19`/`X-09`: відповідь БЕЗ тіла `problem+json` (шлюз, проксі, неіснуючий
 * маршрут) показувала людині «HTTP 502» чи «HTTP 404 · HTTP-404» — число
 * протоколу замість пояснення.
 *
 * ⚠ Каталог у тесті не завантажено, тож людський текст видно як `⟦ключ⟧` —
 * саме це й доводить, що заголовок іде з каталогу, а не літералом.
 */

async function refusalOf(status: number): Promise<EcrApiError> {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => Promise.resolve(new Response('<html>Bad Gateway</html>', { status }))),
  );

  try {
    await apiFetch('/api/v1/documents/1');
  } catch (error) {
    if (error instanceof EcrApiError) return error;
    throw error;
  }

  throw new Error('запит мав відмовити');
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
});

describe('Відповідь без тіла — людський заголовок із каталогу', () => {
  it.each([
    [404, '⟦err.http.notFound⟧'],
    [403, '⟦err.http.forbidden⟧'],
    [502, '⟦err.http.unavailable⟧'],
    [503, '⟦err.http.unavailable⟧'],
    [504, '⟦err.http.timeout⟧'],
    [500, '⟦err.http.serverError⟧'],
    [413, '⟦err.http.requestFailed⟧'],
  ])('%i → %s', async (status, shown) => {
    const error = await refusalOf(status);

    // ⛔ Мутація «повернути `HTTP ${status}`» дає тут «HTTP 502».
    expect(problemText(error).title).toBe(shown);
    expect(problemText(error).title).not.toContain('HTTP');

    // Код для підтримки лишається — число статусу нікуди не зникло.
    expect(error.problem.errorCode).toBe(`HTTP-${status}`);
    expect(error.problem.status).toBe(status);
  });

  it('тост не повторює назву числом протоколу («… · HTTP-404»)', async () => {
    showApiError(await refusalOf(404));

    const call = vi.mocked(notifications.show).mock.calls[0]?.[0] as { message: string };

    // ⛔ Мутація «лишити `${title} · ${code}` для транспортного коду» дає
    // «⟦err.http.notFound⟧ · HTTP-404».
    expect(call.message).toBe('⟦err.http.notFound⟧');
  });

  it('код СЕРВЕРА в тості лишається — він і є зачіпка для підтримки', () => {
    showApiError(
      new EcrApiError({
        title: 'Conflict',
        status: 409,
        errorCode: 'ECR-PRD-4223',
        correlationId: 'cid-1',
        detail: 'Період закрито.',
      }),
    );

    const call = vi.mocked(notifications.show).mock.calls[0]?.[0] as { message: string };
    expect(call.message).toBe('Conflict · ECR-PRD-4223');
  });
});
