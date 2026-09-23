import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';
import { statusTable } from '@/shared/ui/StatusBadge';

/**
 * Колонка «State» переліку документів: позначка відсутності (F6) і стан, який
 * малює НАБІР, а не сама сторінка (UI-06).
 *
 * UI-walkthrough F6 — «колонка State порожня без пояснення». На
 * `/?periodKey=190001` (період, якого немає в календарі) рядок документа
 * показано, а клітинка стану — порожня. Нічого не відрізняє «за цей період
 * станів немає» від «не завантажилося»; порожнеча в таблиці читається двояко.
 *
 * ⛔ UI-06 — друга половина файлу. Тут стояв власний
 * `<Badge variant="light">{sheet}: {state}</Badge>`: код сервера як текст
 * інтерфейсу і ЖОДНОГО кольору. Тобто `Rejected` — аркуш повернено, робота
 * стоїть — виглядав точно так само, як `Approved`. Це п'ятий розбіжний спосіб
 * показу статусу, названий у шапці `StatusBadge.tsx` поіменно.
 *
 * ⚠ Тест дивиться саме на КЛІТИНКУ рядка, а не на сторінку загалом: «десь на
 * екрані є тире» (чи «десь на екрані є S1») — не те твердження, яке доводить
 * знахідку.
 *
 * ⚠ Тон читається з розмітки (`data-status-tone`), а не з кольору CSS.
 * Контраст самих тонів стереже `shared/ui/__tests__/statusBadgeContrast.test.ts`.
 */
const MissingPeriod = 190001;

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

const withoutStates = {
  id: 1,
  businessKey: 'DOC-000001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: {},
};

/*
 * ⚠ ВСІ чотири стани `DocumentStatus` одночасно, і саме в одному документі.
 * Тест на одному `Draft` лишався б зеленим і тоді, коли всі стани стали
 * однаковими: доказ — саме розрізнення. Аркуші названі по-різному навмисно —
 * пара «код аркуша + його стан» перевіряється нижче поіменно.
 */
const withStates = {
  id: 2,
  businessKey: 'DOC-000002',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 4,
  sheetStates: { S1: 'Draft', S2: 'Submitted', S3: 'Rejected', GEN: 'Approved' },
};

/*
 * ⚠ `Returned` — не вигаданий випадок: це стан із МАКЕТА, якого в домені немає
 * (повернення пише `Draft`, `ApprovalState.Reopen()`), і `sheetStates`
 * оголошено як `{ [key: string]: string }` — компілятор про такий стан не
 * скаже нічого.
 */
const withUnknown = {
  id: 3,
  businessKey: 'DOC-000003',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Returned' },
};

/*
 * ⚠ Один аркуш — і код у стані зайвий (знімок людини: `S99819007` перед
 * `SUBMITTED` на документі з одним аркушем).
 */
const singleSheet = {
  id: 4,
  businessKey: 'DOC-000004',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S99819007: 'Submitted' },
};

/*
 * ⚠ Аркушів ДВА, а рядок стану — лише в одного: код ще потрібен, інакше не
 * видно, чий це стан.
 */
const partialStates = {
  id: 5,
  businessKey: 'DOC-000005',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 2,
  sheetStates: { S7: 'Draft' },
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

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

      if (url.includes('/api/v1/documents')) {
        return new Response(
          JSON.stringify({
            items: [withoutStates, withStates, withUnknown, singleSheet, partialStates],
            nextCursor: null,
            totalCount: 5,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/?periodKey=${String(MissingPeriod)}`]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Клітинка «State» рядка з таким бізнес-ключем. */
function stateCellOf(businessKey: string): HTMLElement {
  const row = screen.getByText(businessKey).closest('tr');
  if (row === null) throw new Error(`Рядок ${businessKey} не знайдено`);

  const cells = within(row).getAllByRole('cell');

  return cells[3] as HTMLElement;
}

/**
 * Бейдж стану В МЕЖАХ клітинки.
 *
 * ⛔ Пошук обмежений клітинкою навмисно: `Draft` є і в `DOC-000002`, і
 * (як стан, якого набір не знає, — ні) деінде на сторінці; глобальний
 * `document.querySelector` дозволив би тесту знайти чужий бейдж і лишитися
 * зеленим при зламаному розподілі станів по рядках.
 */
function badgeIn(cell: HTMLElement, state: string): HTMLElement {
  const node = cell.querySelector(`[data-status-state="${state}"]`);
  if (node === null) throw new Error(`Бейдж стану «${state}» у клітинці не знайдено`);

  return node as HTMLElement;
}

function toneIn(cell: HTMLElement, state: string): string | null {
  return badgeIn(cell, state).getAttribute('data-status-tone');
}

/**
 * Обгортка «код аркуша + його бейдж».
 *
 * ⚠ Саме ПАРА, а не «десь у клітинці є S3»: коли аркушів кілька, твердження
 * «на сторінці є S3» і «стан S3 — Rejected» — різні твердження, і зелене
 * перше нічого не каже про друге.
 */
function pairOf(cell: HTMLElement, state: string): HTMLElement {
  const pair = badgeIn(cell, state).parentElement;
  if (pair === null) throw new Error(`Бейдж стану «${state}» не має обгортки`);

  return pair;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('DocumentsPage: відсутність станів позначена видимо (F6)', () => {
  it(
    'документ без станів за обраний період показує позначку відсутності, а не порожнечу',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });

      // ⛔ Мутаційний доказ: прибери гілку з «—» — і клітинка знову порожня,
      // обидва очікування падають.
      const empty = stateCellOf('DOC-000001');
      expect(empty.textContent?.trim()).not.toBe('');
      expect(empty.textContent?.trim()).toBe('—');
    },
    SlowEnvTimeout,
  );

  it(
    'документ зі станами показує бадж, а не позначку відсутності',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });

      const filled = stateCellOf('DOC-000002');

      /*
       * ⚠ ЗМІНА ПОВЕДІНКИ, названа явно. Тут стояло
       * `expect(filled.textContent).toContain('S1: Draft')` — дослівний код
       * сервера. Підпис стану більше не є кодом сервера: його дає
       * `t('status.sheet.Draft')`, і в сіді це окремий рядок каталогу. Цей файл
       * каталогу не завантажує, тож `t()` віддає позначений ключ (`D-138`) —
       * і саме ключ є доказом, що підпис пройшов ЧЕРЕЗ каталог, а не через
       * `{state}`.
       */
      expect(badgeIn(filled, 'Draft').textContent).toBe('⟦status.sheet.Draft⟧');
      expect(badgeIn(filled, 'Draft').textContent).not.toBe('Draft');
      expect(filled.textContent).not.toContain('—');
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: тон стану аркуша приходить із набору (UI-06)', () => {
  it(
    'чотири стани документа — і тони РІЗНІ, а не «без кольору» на всіх',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });

      const cell = stateCellOf('DOC-000002');

      /*
       * ⛔ Мутаційний доказ №1 (повернути `<Badge>{sheet}: {state}</Badge>`):
       * власний бейдж не лишає в розмітці ані `data-status-state`, ані
       * `data-status-tone`, тож `badgeIn` кидає «Бейдж стану … не знайдено».
       */
      expect(toneIn(cell, 'Rejected')).toBe('danger');
      expect(toneIn(cell, 'Submitted')).toBe('info');
      expect(toneIn(cell, 'Draft')).toBe('neutral');
      expect(toneIn(cell, 'Approved')).toBe('neutral');

      /*
       * ⛔ Мутаційний доказ №2 (підставити константу — `state="Draft"` на всіх
       * аркушах): чотири стани дають ОДИН `data-status-state`, і множина
       * тонів схлопується. Рядок нижче — єдиний, який ловить саме це: кожне
       * окреме `toBe` вище можна задовольнити й одним кольором на всіх.
       *
       * ⚠ Три, а не чотири: `Draft` і `Approved` — обидва `neutral`, і це
       * рішення набору, а не недогляд. `KIT.md` §1.3: «зелений не вживається
       * для „все гаразд“» — затверджений аркуш це нормальний стан, а не
       * досягнення, яке треба підсвітити.
       */
      const tones = new Set(
        ['Draft', 'Submitted', 'Rejected', 'Approved'].map((state) => toneIn(cell, state)),
      );
      expect(tones.size).toBe(3);

      // ⛔ Головне, заради чого колонці потрібен колір: «робота стоїть» не
      // виглядає як «усе гаразд».
      expect(toneIn(cell, 'Rejected')).not.toBe(toneIn(cell, 'Draft'));
      expect(toneIn(cell, 'Rejected')).not.toBe(toneIn(cell, 'Approved'));
      expect(toneIn(cell, 'Submitted')).not.toBe(toneIn(cell, 'Approved'));
    },
    SlowEnvTimeout,
  );

  it(
    'код аркуша видно ПОРУЧ зі своїм станом, а не десь у клітинці',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });

      const cell = stateCellOf('DOC-000002');

      /*
       * ⛔ `StatusBadge` малює ЛИШЕ перекладений стан — коду аркуша в ньому
       * немає. Коли аркушів кілька, без коду незрозуміло, ЧИЙ це стан, тож
       * код лишається окремим текстом поруч. Перевіряється саме пара: обгортка
       * бейджа `Rejected` має містити `S3` — і НЕ містити коди інших аркушів.
       */
      const rejected = pairOf(cell, 'Rejected');
      expect(rejected.textContent).toContain('S3');
      expect(rejected.textContent).not.toContain('S1');
      expect(rejected.textContent).not.toContain('S2');
      expect(rejected.textContent).not.toContain('GEN');

      const approved = pairOf(cell, 'Approved');
      expect(approved.textContent).toContain('GEN');
      expect(approved.textContent).not.toContain('S3');

      // ⚠ Пара не розривається переносом рядка: код без стану поруч читався б
      // як стан СУСІДНЬОГО аркуша.
      expect(rejected.getAttribute('style') ?? '').toContain('nowrap');
    },
    SlowEnvTimeout,
  );

  it(
    'стан, якого набір не знає, позначений як невідомий, а не мовчки нейтральний',
    async () => {
      /*
       * ⛔ Фікстура мусить бути СПРАВДІ невідомим станом — інакше тест нижче
       * перевіряв би зовсім інше, і ніхто б цього не помітив.
       */
      expect(statusTable.sheet['Returned'], '`Returned` не має бути в таблиці набору').toBeUndefined();
      expect(statusTable.sheet['Draft'], '`Draft` — відомий стан домену').toBeDefined();

      mockFetch();
      show();

      await screen.findByText('DOC-000003', {}, { timeout: SlowEnvTimeout });

      const unknown = badgeIn(stateCellOf('DOC-000003'), 'Returned');

      expect(unknown.getAttribute('data-status-known')).toBe('false');
      expect(unknown.getAttribute('data-status-tone')).toBe('warning');

      // ⚠ Другий канал помітності (`ФВ-14.18`): рядка каталогу під цей стан
      // теж немає, тож підпис приїжджає позначеним ключем — пропуск видно, а
      // не лише в DevTools.
      expect(unknown.textContent).toBe('⟦status.sheet.Returned⟧');

      // ⛔ І це НЕ той самий вигляд, що у відомого стану.
      const known = badgeIn(stateCellOf('DOC-000002'), 'Draft');
      expect(known.getAttribute('data-status-known')).toBe('true');
      expect(unknown.getAttribute('data-status-tone')).not.toBe(
        known.getAttribute('data-status-tone'),
      );
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: код аркуша лише там, де аркушів кілька', () => {
  it(
    'документ з одним аркушем — лише бейдж, без внутрішнього коду',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000004', {}, { timeout: SlowEnvTimeout });

      const cell = stateCellOf('DOC-000004');

      // ⛔ Мутаційний доказ: прибери умову `!isSingleSheet(document)` — код
      // повернеться в клітинку, і цей рядок почервоніє.
      expect(cell.textContent).not.toContain('S99819007');
      expect(badgeIn(cell, 'Submitted').textContent).toBe('⟦status.sheet.Submitted⟧');
    },
    SlowEnvTimeout,
  );

  it(
    'аркушів кілька — код лишається поруч зі станом (назв у переліку немає)',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000005', {}, { timeout: SlowEnvTimeout });

      // ⛔ Мутаційний доказ: сховай код завжди — обидва рядки почервоніють.
      expect(pairOf(stateCellOf('DOC-000005'), 'Draft').textContent).toContain('S7');
      expect(pairOf(stateCellOf('DOC-000002'), 'Rejected').textContent).toContain('S3');
    },
    SlowEnvTimeout,
  );
});
