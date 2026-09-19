import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { cellChangesQuery, isSingleCell } from '@/features/audit/api';
import { testTheme } from '@/test/render';

/**
 * `BE-03`: фільтри журналу аудиту доїжджають до сервера.
 *
 * ⛔ Предмет перевірки — САМЕ АДРЕСА запиту, а не вигляд полів. Фільтр, який
 * намальований на екрані й не потрапив у запит, виглядає працюючим: список
 * просто не змінюється, і це читається як «таких змін немає». Рівно цей клас
 * дефекту вже ловили на `DocumentsPage` (`useUrlParamsSetter`): поле набирало
 * значення, а жоден запит його не бачив.
 */
/*
 * ⚠ Рівно один рядок, а не порожня сторінка: на порожній `AsyncBoundary`
 * показує стан «нічого не знайдено» БЕЗ таблиці, і тест чекав би на `table`
 * до самого таймауту — падав би на зовсім іншій причині, ніж перевіряє.
 */
const page = {
  items: [
    {
      changedAt: '2026-01-05T10:00:00Z',
      changedByUserId: 41,
      columnDefId: 11,
      documentId: 7,
      isLateEdit: false,
      newValue: '2',
      oldValue: '1',
      origin: 'Import',
      periodKey: 202601,
      rowKey: 'R1',
    },
  ],
  nextCursor: null,
  totalCount: null,
};

/** Адреси журналу, які клієнт справді запитав. */
function mockFetch(): string[] {
  const seen: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/audit/cells')) {
        seen.push(url);

        return new Response(JSON.stringify(page), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );

  return seen;
}

function show(search: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/admin/audit${search}`]}>
        <QueryClientProvider client={client}>
          <AuditPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('cellChangesQuery — рядок запиту журналу', () => {
  it('несе вікно завжди, а решту — лише коли задано', () => {
    const query = new URLSearchParams(cellChangesQuery({ from: '2026-01-01', to: '2026-01-08' }));

    expect(query.get('from')).toBe('2026-01-01');
    expect(query.get('to')).toBe('2026-01-08');

    /*
     * ⛔ Порожні фільтри НЕ надсилаються зовсім. `?rowKey=` сервер трактує як
     * відсутній фільтр, але адреса з порожніми хвостами накопичує їх і стає
     * нечитабельною — а саме її людина надсилає колезі.
     */
    expect(query.has('rowKey')).toBe(false);
    expect(query.has('author')).toBe(false);
    expect(query.has('origin')).toBe(false);
    expect(query.has('documentId')).toBe(false);

    // ⚠ `lateOnly=false` теж відсутній: булеве за замовчуванням і так `false`,
    // а параметр у адресі виглядав би як свідомо обраний фільтр.
    expect(query.has('lateOnly')).toBe(false);
  });

  it('кодує ключ рядка, а не обриває адресу на ньому', () => {
    /*
     * ⛔ `#` в URL починає фрагмент — усе після нього на сервер НЕ їде. Саме
     * на цьому падав `smoke.ps1` на кроці 17 (ідентифікатор фонової задачі з
     * `#`), і шістнадцять кроків перед ним проходили. `RowKey` — довільний
     * текст із шаблону, тож сюди той самий дефект приходить тим самим шляхом.
     */
    const query = cellChangesQuery({
      from: '2026-01-01',
      to: '2026-01-08',
      documentId: 7,
      rowKey: 'R#1&2',
      columnDefId: 11,
    });

    expect(query).toContain('rowKey=R%231%262');
    expect(new URLSearchParams(query).get('rowKey')).toBe('R#1&2');
  });

  it('повна адреса комірки впізнається, неповна — ні', () => {
    const window = { from: '2026-01-01', to: '2026-01-08' };

    // ⚠ Межа доступу, не косметика: на повну адресу сервер приймає
    // `Document.View` і вікно в 13 місяців, на будь-яку неповну — ні.
    expect(isSingleCell({ ...window, documentId: 7, rowKey: 'R1', columnDefId: 11 })).toBe(true);
    expect(isSingleCell({ ...window, documentId: 7, rowKey: 'R1' })).toBe(false);
    expect(isSingleCell({ ...window, documentId: 7, columnDefId: 11 })).toBe(false);
    expect(isSingleCell({ ...window, rowKey: 'R1', columnDefId: 11 })).toBe(false);

    // Порожній рядок — це НЕ ключ рядка: поле щойно очистили.
    expect(isSingleCell({ ...window, documentId: 7, rowKey: '', columnDefId: 11 })).toBe(false);
  });
});

describe('AuditPage: фільтри з адреси доїжджають до сервера', () => {
  it(
    'author, origin, lateOnly і адреса комірки потрапляють у запит',
    async () => {
      const seen = mockFetch();
      show(
        '?from=2026-01-01&to=2026-01-08&documentId=7'
          + '&rowKey=R1&columnDefId=11&author=41&origin=Import&lateOnly=true',
      );

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      expect(seen.length).toBeGreaterThan(0);
      const query = new URLSearchParams(seen[0]!.split('?')[1]);

      // ⛔ Мутація: прибрати `params.set('author', …)` у `cellChangesQuery` —
      // падає саме цей рядок, а не «таблиця порожня».
      expect(query.get('author')).toBe('41');
      expect(query.get('origin')).toBe('Import');
      expect(query.get('lateOnly')).toBe('true');
      expect(query.get('rowKey')).toBe('R1');
      expect(query.get('columnDefId')).toBe('11');
      expect(query.get('documentId')).toBe('7');

      // ⛔ Вікно лишається ОБОВ'ЯЗКОВИМ у кожному запиті: таблиця
      // партиційована за `ChangedAt`, і запит без меж пішов би по всіх
      // партиціях. Це не оптимізація — це «сервер зайнятий».
      expect(query.get('from')).toBe('2026-01-01');
      expect(query.get('to')).toBe('2026-01-08');
    },
    SlowEnvTimeout,
  );

  it(
    'без фільтрів у адресі запит несе лише вікно й розмір сторінки',
    async () => {
      const seen = mockFetch();
      show('?from=2026-01-01&to=2026-01-08');

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      const query = new URLSearchParams(seen[0]!.split('?')[1]);

      expect([...query.keys()].sort()).toStrictEqual(['from', 'limit', 'to']);
    },
    SlowEnvTimeout,
  );
});
