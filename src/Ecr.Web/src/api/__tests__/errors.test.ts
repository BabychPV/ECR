import { describe, it, expect, vi, afterEach } from 'vitest';
import {
  CORRELATION_HEADER,
  EcrApiError,
  apiFetch,
  isRetryable,
  setLoginRedirect,
} from '@/api/client';

/**
 * Клієнт розрізняє причини **за кодом**, а не за текстом: текст локалізований
 * і може змінюватися, код — ні.
 */
function respond(status: number, body: unknown, ok = false): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    ),
  );

  void ok;
}

afterEach(() => {
  vi.unstubAllGlobals();
  setLoginRedirect(() => {});
});

describe('Обробка помилок API', () => {
  it('розбирає EcrProblemDetails і зберігає код помилки', async () => {
    respond(422, {
      title: 'Значення не відповідає типу',
      status: 422,
      detail: 'Колонка «Обсяг» очікує число.',
      errorCode: 'ECR-CELL-0422',
      correlationId: 'abc-123',
    });

    const error = await apiFetch('/api/v1/documents/1').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(EcrApiError);
    expect((error as EcrApiError).problem.errorCode).toBe('ECR-CELL-0422');
    expect((error as EcrApiError).problem.correlationId).toBe('abc-123');
  });

  it('ФВ-14.24: конфлікт розпізнається за кодом і містить перелік розбіжностей', async () => {
    // ⛔ Тіло — рівно те, що пише сервер: розширення лежать ПЛОСКО у верхньому
    // рівні (RFC 9457 §3.2), а не всередині поля `extensions2`.
    //
    // До цього фікстура загортала `conflicts` в `extensions2` — форму, якої
    // сервер не надсилає ніколи. Тест був зелений, а справжній шлях зламаний:
    // `error.conflicts` завжди повертав порожній масив, і при конфлікті
    // паралельного редагування grid не показував ані чиєї правки, ані якої.
    // Це та сама межа, що й у `A7-34`…`A7-36`, і той самий механізм —
    // **фікстура знала більше за систему**.
    respond(409, {
      title: 'Конфлікт',
      status: 409,
      errorCode: 'ECR-CELL-0409',
      correlationId: 'cid',
      conflicts: [{ rowKey: 'R1', columnCode: 'C1', theirUser: 'ivanov' }],
    });

    const error = (await apiFetch('/api/v1/cells').catch((e: unknown) => e)) as EcrApiError;

    // ⛔ «Перезаписати мовчки» не є опцією: користувач має побачити, чия
    // правка і яка саме.
    expect(error.isConflict).toBe(true);
    expect(error.conflicts).toHaveLength(1);
  });

  it('401 веде на сторінку входу без спроби мовчазного повторного входу', async () => {
    const redirect = vi.fn();
    setLoginRedirect(redirect);

    const attempts = vi.fn(async () =>
      Promise.resolve(new Response('', { status: 401 })),
    );
    vi.stubGlobal('fetch', attempts);

    const error = (await apiFetch('/api/v1/me').catch((e: unknown) => e)) as EcrApiError;

    expect(redirect).toHaveBeenCalledTimes(1);
    expect(error.problem.status).toBe(401);

    // ⚠ Рівно один запит: мовчазний повторний вхід приховав би причину —
    // користувач бачив би, що дані «іноді не зберігаються».
    expect(attempts).toHaveBeenCalledTimes(1);
  });

  it('4xx не повторюється автоматично', () => {
    const forbidden = new EcrApiError({
      title: 'Немає права',
      status: 403,
      errorCode: 'ECR-AUTH-0403',
      correlationId: 'cid',
    });

    const unavailable = new EcrApiError({
      title: 'Джерело недоступне',
      status: 503,
      errorCode: 'ECR-INT-0503',
      correlationId: 'cid',
    });

    expect(isRetryable(forbidden)).toBe(false);
    expect(isRetryable(unavailable)).toBe(true);
  });

  it('користувачеві показується текст сервера, а не власний узагальнений', async () => {
    respond(422, {
      title: 'Публікація без зеленого тесту',
      status: 422,
      detail: 'Методологія 7: тест не проходив після останньої правки.',
      errorCode: 'ECR-CALC-0422',
      correlationId: 'cid',
    });

    const error = (await apiFetch('/api/v1/methodologies/7/publish').catch(
      (e: unknown) => e,
    )) as EcrApiError;

    expect(error.message).toBe('Методологія 7: тест не проходив після останньої правки.');
    expect(error.message).not.toContain('щось пішло не так');
  });

  it('відповідь не у форматі problem+json усе одно дає код', async () => {
    // Проксі і балансувальник відповідають HTML. Екран, який уміє показати
    // лише EcrProblem, інакше показав би «щось пішло не так».
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Promise.resolve(new Response('<html>502</html>', { status: 502 }))),
    );

    const error = (await apiFetch('/api/v1/documents').catch((e: unknown) => e)) as EcrApiError;

    expect(error.problem.errorCode).toBe('HTTP-502');
    expect(error.problem.correlationId).not.toBe('');
  });

  it('запит несе ідентифікатор кореляції', async () => {
    const calls = vi.fn((_input: unknown, _init?: RequestInit) =>
      Promise.resolve(new Response(JSON.stringify({ ok: true }), { status: 200 })),
    );
    vi.stubGlobal('fetch', calls);

    await apiFetch('/api/v1/documents');

    const init = calls.mock.calls[0]?.[1];
    const headers = new Headers(init?.headers);

    // ⚠ Без цього заголовка скаргу «у мене не зберігається» неможливо звірити
    // з серверним журналом інакше, ніж пошуком за часом серед тисяч записів.
    expect(headers.get(CORRELATION_HEADER)).toBeTruthy();
    expect(init?.credentials).toBe('include');
  });
});

describe('Розширення помилки лежать плоско (RFC 9457 §3.2)', () => {
  it('стандартні поля формату розширеннями не вважаються', async () => {
    respond(422, {
      type: 'about:blank',
      title: 'Не пройшло',
      status: 422,
      detail: 'Подробиця',
      instance: '/api/v1/x',
      errorCode: 'ECR-CELL-0422',
      correlationId: 'cid-2',
    });

    const error = (await apiFetch('/api/v1/x').catch((e: unknown) => e)) as EcrApiError;

    // ⚠ Перелік стандартних полів закритий (RFC 9457 §3.1), решта —
    // розширення за визначенням. Але `title`/`detail`/`instance` НЕ мають
    // потрапити в розширення: інакше вони показувалися б як подробиці коду.
    const extensions = error.problem.extensions2 ?? {};

    expect(Object.keys(extensions)).not.toContain('title');
    expect(Object.keys(extensions)).not.toContain('detail');
    expect(Object.keys(extensions)).not.toContain('instance');
    expect(Object.keys(extensions)).toContain('errorCode');
  });

  it('відсутність розширень не створює порожнього словника', async () => {
    respond(500, { title: 'Внутрішня помилка', status: 500 });

    const error = (await apiFetch('/api/v1/x').catch((e: unknown) => e)) as EcrApiError;

    // ⚠ Під `exactOptionalPropertyTypes` «поля немає» і «поле є, воно
    // порожнє» — різні стани, і в журналі вони виглядають по-різному.
    expect(error.problem.extensions2).toBeUndefined();
  });
});
