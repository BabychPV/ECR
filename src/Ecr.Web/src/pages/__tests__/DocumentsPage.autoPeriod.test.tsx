import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * `U-10` — єдиний доступний варіант має бути обраний.
 *
 * ⛔ Перелік документів відкривався з ПОРОЖНІМ полем «Period», тому колонка
 * «State» показувала «—» в кожному рядку — хоча відкритий період у проєкту
 * рівно один і система його знає (`GET /projects/{id}/periods` віддає
 * `202609 Open` з позначкою `isCurrent`).
 *
 * ⛔ Межа автовибору — предмет окремих випадків нижче, і вона вузька в дві
 * сторони: проєкт має бути рівно ОДИН, а період береться позначений сервером
 * (`isCurrent`), а не «перший-ліпший». Автовибір із десяти мовчки показав би
 * чужі дані.
 *
 * ⛔ Мутаційні докази (усі перевірені):
 *   1. прибрати `useEffect` автовибору в `DocumentsPage.tsx` → червоніє перший
 *      випадок (періоду в адресі немає, стан не показано);
 *   2. замінити `projects.data?.items.length === 1 ? …` на
 *      `projects.data?.items[0]` → червоніє випадок «проєктів два»;
 *   3. замінити вибір `current ?? (open.length === 1 ? open[0] : undefined)`
 *      на `periods[0]` → червоніє випадок «поточний не перший у списку».
 */

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };
const otherProject = { id: 2, code: 'SECOND_PRJ', status: 'Active' as const };

/** Календар: `202608` закритий і стоїть ПЕРШИМ, поточний — `202609`. */
const calendar = {
  timeZoneId: 'Asia/Aqtau',
  policy: { code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45 },
  periods: [
    {
      id: 10,
      periodKey: 202608,
      sequence: 8,
      state: 'Closed',
      isCurrent: false,
      startsAt: '2026-08-01T00:00:00Z',
      endsAt: '2026-09-15T00:00:00Z',
      graceEndsAt: null,
      reopenedUntil: null,
    },
    {
      id: 11,
      periodKey: 202609,
      sequence: 9,
      state: 'Open',
      isCurrent: true,
      startsAt: '2026-09-01T00:00:00Z',
      endsAt: '2026-10-15T00:00:00Z',
      graceEndsAt: null,
      reopenedUntil: null,
    },
  ],
};

const document1 = {
  id: 1,
  businessKey: 'DOC-000001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
  errorCount: null,
  modifiedAt: '2026-09-20T10:00:00Z',
  hasLateEdits: false,
};

const document2 = {
  id: 2,
  businessKey: 'DOC-000002',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Approved' },
  errorCount: null,
  modifiedAt: '2026-09-20T11:00:00Z',
  hasLateEdits: false,
};

/** Адреси, куди сторінка сходила — для перевірки «зайвого запиту немає». */
let requested: string[] = [];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function serve(projects: readonly unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      /*
       * ⚠ Календар перевіряється РАНІШЕ за перелік проєктів: його адреса
       * (`/api/v1/projects/1/periods`) містить адресу переліку як префікс, і
       * зворотний порядок віддав би календарю сторінку проєктів.
       *
       * ⛔ Перелік документів той самий НЕЗАЛЕЖНО від періоду в запиті — саме
       * так поводиться сервер (`DocumentsPage.tsx`: перелік періодом НЕ
       * фільтрується, період керує лише колонкою стану). Якби мок фільтрував,
       * випадок «автовибір не став фільтром» нижче доводив би мок, а не код.
       */
      if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) return json(calendar);
      if (url.includes('/api/v1/projects')) {
        return json({ items: projects, nextCursor: null, totalCount: projects.length });
      }

      if (url.includes('/api/v1/documents')) {
        return json({ items: [document1, document2], nextCursor: null, totalCount: 2 });
      }

      return json(null);
    }),
  );
}

/** Показує поточну адресу — щоб перевірити, що вибір ліг саме в неї. */
let search = '';

function LocationProbe(): JSX.Element {
  search = useLocation().search;

  return <span data-search={search} />;
}

function show(path = '/'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Клітинка «State» рядка з таким бізнес-ключем (четверта колонка). */
function stateCellOf(businessKey: string): HTMLElement {
  const row = screen.getByText(businessKey).closest('tr');
  if (row === null) throw new Error(`Рядок ${businessKey} не знайдено`);

  const cell = within(row).getAllByRole('cell')[3];
  if (cell === undefined) throw new Error('Колонки «State» немає');

  return cell;
}

beforeEach(() => {
  requested = [];
  search = '';
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage: автовибір поточного періоду (U-10)', () => {
  it('проєкт один — поточний період потрапляє в адресу, і колонка стану ожила', async () => {
    serve([project]);

    show();

    /*
     * ⛔ Ядро випадку. Без автовибору `?periodKey=` в адресі немає, і саме це
     * робило колонку «State» прочерком у КОЖНОМУ рядку.
     *
     * ⚠ Саме АДРЕСА, а не локальний стан: посилання звідси надсилають далі, і
     * воно має відкрити той самий період.
     */
    await waitFor(() => {
      expect(new URLSearchParams(search).get('periodKey')).toBe('202609');
    });

    // ⚠ Рядки дочікуються ПІСЛЯ зміни адреси: вона змінює ключ запиту, тобто
    // перелік перезапитується, і до відповіді на місці таблиці скелет.
    await screen.findByText('DOC-000001');

    // Період видно і в посиланні на документ — інакше `DocumentPage`
    // підставив би поточний МІСЯЦЬ, а не обраний період (`UI-walkthrough F3`).
    expect(screen.getByText('DOC-000001').closest('a')?.getAttribute('href')).toContain(
      'periodKey=202609',
    );

    // Стан більше не «—»: бейдж аркуша на місці.
    // ⚠ Саме бейдж, а не код `S1`: у документа ОДИН аркуш, і код у стані
    // більше не друкується (`DocumentsPage.stateColumn.test.tsx`).
    expect(
      stateCellOf('DOC-000001').querySelector('[data-status-state="Draft"]'),
    ).not.toBeNull();
    expect(stateCellOf('DOC-000001').textContent).not.toBe('—');
  });

  it('автовибір НЕ став фільтром: обидва документи лишилися, звуження стану в адресу не потрапило', async () => {
    serve([project]);

    show();

    await waitFor(() => {
      expect(new URLSearchParams(search).get('periodKey')).toBe('202609');
    });

    /*
     * ⛔ Задокументована навмисна поведінка (`DocumentsPage.tsx`): перелік
     * періодом НЕ фільтрується — період керує лише колонкою стану. Автовибір
     * не має її перевернути, тож у переліку далі ОБИДВА документи, а
     * параметра `state` в адресі не з'явилося.
     */
    // ⚠ Після зміни адреси перелік перезапитується під НОВИМ ключем запиту,
    // тож рядки треба дочекатися заново, а не читати з-під скелета.
    await screen.findByText('DOC-000001');

    expect(screen.getByText('DOC-000002')).toBeDefined();
    expect(new URLSearchParams(search).get('state')).toBeNull();
  });

  it('проєктів два — вибір лишається людині, адреса чиста', async () => {
    serve([project, otherProject]);

    show();

    await screen.findByText('DOC-000001');

    /*
     * ⛔ Межа автовибору. Перелік документів наскрізний по проєктах: узяти
     * календар «першого-ліпшого» з двох означало б підставити період ЧУЖОГО
     * проєкту — і нічого на екрані не сказало б, що вибір зроблено за людину.
     */
    expect(new URLSearchParams(search).get('periodKey')).toBeNull();

    // І календаря ніхто не питав — запиту, який нічого не вирішує, немає.
    expect(requested.some((url) => /\/api\/v1\/projects\/\d+\/periods/.test(url))).toBe(false);
  });
});

/**
 * ⛔ Автовибір — лише при ВХОДІ без періоду, не на кожне спорожніле поле.
 *
 * Живий стенд (2026-09-24, `drop-repro.mjs`, звичайний процесор, 5 з 5):
 * Ctrl+A, Delete у «Period» на `/?periodKey=202609` і набір `202608` давали в
 * полі `202608202609` — автовибір дописував поточний період у щойно очищене
 * поле, в якому людина вже друкувала.
 *
 * ⛔ Мутаційний доказ (перевірено): у `DocumentsPage.tsx` прибрати
 * `!autoPick ||` з ефекту й `autoPick &&` з `enabled` календаря (тобто
 * автовибір щоразу, коли `periodKey === null`) — червоніють обидва випадки
 * нижче: адреса знову отримує `202609`, а поле після blur показує його ж.
 */
describe('DocumentsPage: очищене людиною поле «Period» автовибір не заповнює', () => {
  const periodsAsked = (): boolean =>
    requested.some((url) => /\/api\/v1\/projects\/\d+\/periods/.test(url));

  it('вхід із періодом → очистити поле: адреса й поле лишаються порожніми, набір дає рівно набране', async () => {
    serve([project]);
    const user = userEvent.setup();

    show('/?periodKey=202609');

    const input = await screen.findByRole<HTMLInputElement>('textbox', {
      name: '⟦documents.period⟧',
    });
    await screen.findByText('DOC-000001');

    await user.clear(input);
    expect(new URLSearchParams(search).get('periodKey')).toBeNull();

    // Дати автовибору всі шанси: проєкти відповіли, запити розв'язались.
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(periodsAsked()).toBe(false);
    expect(new URLSearchParams(search).get('periodKey')).toBeNull();
    expect(input.value).toBe('');

    // Людина відвернулась (blur) — порожнє поле так і лишається порожнім.
    fireEvent.blur(input);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(input.value).toBe('');

    await user.type(input, '202608');
    expect(input.value).toBe('202608');
    await waitFor(() => expect(new URLSearchParams(search).get('periodKey')).toBe('202608'));
  });

  it('вхід БЕЗ періоду → автовибір 202609 → очистити: другого автовибору немає', async () => {
    serve([project]);
    const user = userEvent.setup();

    show('/');

    await waitFor(() => expect(new URLSearchParams(search).get('periodKey')).toBe('202609'));
    const input = await screen.findByRole<HTMLInputElement>('textbox', {
      name: '⟦documents.period⟧',
    });
    await waitFor(() => expect(input.value).toBe('202609'));

    await user.clear(input);
    fireEvent.blur(input);
    await new Promise((resolve) => setTimeout(resolve, 150));

    expect(new URLSearchParams(search).get('periodKey')).toBeNull();
    expect(input.value).toBe('');
  });
});
