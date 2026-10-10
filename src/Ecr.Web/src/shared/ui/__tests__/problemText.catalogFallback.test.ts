import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import { loadCatalog } from '@/shared/i18n';
import { problemText } from '@/shared/ui/problemText';

/**
 * X5-02: сервер НЕ прочитав каталог (тимчасовий збій БД — каталог живе в ній же;
 * збій читання) і прислав код заголовком та `messageKey` без подробиці. Текст мовою
 * користувача бере власний каталог клієнта.
 *
 * ⛔ Було: 503 `databaseBusy` показував «ECR-SYS-0503» заголовком і українське речення
 * подробицею — для інтерфейсу en/ru/kz, у якому української немає (`D-95`).
 */

const Strings: Record<string, string> = {
  'err.ECR-SYS-0503.databaseBusy.title': 'Database temporarily unavailable',
  'err.ECR-SYS-0503.databaseBusy': 'The database is temporarily busy. Try again in a few seconds.',
  'err.ECR-USR-0409': 'User name already in use',
  'err.ECR-USR-0409.windowsSidTaken': 'SID {sid} is already linked to the account "{userName}".',
};

beforeEach(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: Strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"private-en-1"' },
        }),
      ),
    ),
  );

  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('problemText: сервер без каталогу — текст із каталогу клієнта', () => {
  it('503 databaseBusy: назва й подробиця мовою користувача, а не код і не українське речення', () => {
    const shown = problemText(
      new EcrApiError({
        title: 'ECR-SYS-0503',
        status: 503,
        errorCode: 'ECR-SYS-0503',
        correlationId: 'cid-1',
        extensions2: { messageKey: 'err.ECR-SYS-0503.databaseBusy', errorCode: 'ECR-SYS-0503' },
      }),
    );

    // ⛔ Мутація: прибрати гілку `title === errorCode` у titleOf — «ECR-SYS-0503».
    expect(shown.title).toBe('Database temporarily unavailable');
    // ⛔ Мутація: прибрати `catalogDetailOf` — `null`, людина без пояснення, що робити.
    expect(shown.detail).toBe('The database is temporarily busy. Try again in a few seconds.');
    expect(shown.code).toBe('ECR-SYS-0503');
  });

  it('без `<messageKey>.title` — назва за `err.<код>`, підстановки — з розширень відповіді', () => {
    const shown = problemText(
      new EcrApiError({
        title: 'ECR-USR-0409',
        status: 409,
        errorCode: 'ECR-USR-0409',
        correlationId: 'cid-2',
        extensions2: { messageKey: 'err.ECR-USR-0409.windowsSidTaken', sid: 'S-1-5-21-1', userName: 'CORP\\petro' },
      }),
    );

    expect(shown.title).toBe('User name already in use');
    expect(shown.detail).toBe('SID S-1-5-21-1 is already linked to the account "CORP\\petro".');
  });

  it('ключа немає й у клієнта — лишається код і жодного `⟦…⟧`', () => {
    const shown = problemText(
      new EcrApiError({
        title: 'ECR-X-0500',
        status: 500,
        errorCode: 'ECR-X-0500',
        correlationId: 'cid-3',
        extensions2: { messageKey: 'err.ECR-X-0500.unknown' },
      }),
    );

    expect(shown.title).toBe('ECR-X-0500');
    expect(shown.detail).toBeNull();
  });

  it('подробиця, яку сервер уже локалізував, не підміняється', () => {
    const shown = problemText(
      new EcrApiError({
        title: 'User name already in use',
        status: 409,
        errorCode: 'ECR-USR-0409',
        correlationId: 'cid-4',
        detail: 'Already localized by the server.',
        extensions2: { messageKey: 'err.ECR-USR-0409.windowsSidTaken', sid: 'S-1-5-21-1', userName: 'x' },
      }),
    );

    expect(shown.title).toBe('User name already in use');
    expect(shown.detail).toBe('Already localized by the server.');
  });
});
