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

/*
 * ⚠ Сервер віддає `sheets` — аркуші В ПОРЯДКУ аркушів і з назвою. Порядок
 * `sheets` навмисно НЕ збігається з порядком ключів `sheetStates`: так видно,
 * що клітинка йде за `sheets`, а не за словником. У `WTR` назви немає жодною
 * мовою — підпис має впасти на код. Назва `GEN` різна для `uk` і `en`: мова
 * інтерфейсу тут `en` (`/me`), тож має бути саме англійська.
 */
const withNames = {
  id: 6,
  businessKey: 'DOC-000006',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 3,
  sheetStates: { WTR: 'Rejected', AIR: 'Submitted', GEN: 'Draft' },
  sheets: [
    { code: 'GEN', nameL10n: { values: { uk: 'Загальні відомості', en: 'General info' } }, state: 'Draft' },
    { code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, state: 'Submitted' },
    { code: 'WTR', nameL10n: { values: {} }, state: 'Rejected' },
  ],
};

/* ⚠ Один аркуш і `sheets` з назвою — підпису однаково немає: ні коду, ні назви. */
const singleNamed = {
  id: 7,
  businessKey: 'DOC-000007',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S42: 'Approved' },
  sheets: [{ code: 'S42', nameL10n: { values: { en: 'Only sheet' } }, state: 'Approved' }],
};

/*
 * ⛔ Прихований аркуш (P1, безпека): роль бачить ОДИН аркуш із трьох. Сервер
 * віддає лише видимий у `sheets`/`sheetStates`, а `errorCount` — `null` (по
 * всіх аркушах роль рахувати не може). `sheetCount` навмисно лишено 3: якщо
 * клієнт рахує «N of M» від нього, число видає існування прихованих аркушів.
 */
const scopedToOneSheet = {
  id: 8,
  businessKey: 'DOC-000008',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 3,
  sheetStates: { AIR: 'Submitted' },
  sheets: [{ code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, state: 'Submitted' }],
  errorCount: null,
  warningCount: null,
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
            items: [withoutStates, withStates, withUnknown, singleSheet, partialStates, withNames, singleNamed, scopedToOneSheet],
            nextCursor: null,
            totalCount: 8,
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

/** Клітинка колонки рядка з таким бізнес-ключем (`data-column`). */
function cellOf(businessKey: string, column: 'sheets' | 'state' | 'issues'): HTMLElement {
  const row = screen.getByText(businessKey).closest('tr');
  if (row === null) throw new Error(`Рядок ${businessKey} не знайдено`);

  const cell = within(row)
    .getAllByRole('cell')
    .find((node) => node.getAttribute('data-column') === column);
  if (cell === undefined) throw new Error(`Колонку ${column} у рядку ${businessKey} не знайдено`);

  return cell;
}

/** Сегмент смужки аркушів (`SegmentBar`) — за кодом аркуша. */
function segmentOf(businessKey: string, code: string): HTMLElement {
  const node = cellOf(businessKey, 'sheets').querySelector(`[data-segment="${code}"]`);
  if (node === null) throw new Error(`Сегмент аркуша «${code}» у рядку ${businessKey} не знайдено`);

  return node as HTMLElement;
}

/** Бейдж стану документа в колонці «State». */
function stateBadgeOf(businessKey: string): HTMLElement {
  const node = cellOf(businessKey, 'state').querySelector('[data-status-state]');
  if (node === null) throw new Error(`Бейдж стану в рядку ${businessKey} не знайдено`);

  return node as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

async function shown(businessKey: string): Promise<void> {
  mockFetch();
  show();
  await screen.findByText(businessKey, {}, { timeout: SlowEnvTimeout });
}

/*
 * ✎ `UI-19` (2026-10-06): стани аркушів переїхали з колонки «State» у смужку
 * «Sheets» (`SegmentBar`, макет `10-docs-list-light.png`), а «State» тепер —
 * ОДИН бейдж стану документа (найгірший з аркушів, правило сервера
 * `DocumentListSummaryStore`). Твердження нижче ті самі, що були в UI-06/F6, —
 * змінилось лише, ДЕ вони перевіряються: пара «аркуш + стан» — це сегмент із
 * `data-segment` і `data-state`, назва аркуша — у його підказці.
 */
describe('DocumentsPage: відсутність станів позначена видимо (F6)', () => {
  it(
    'документ без станів за обраний період показує позначку відсутності, а не порожнечу',
    async () => {
      await shown('DOC-000001');

      // ⛔ Мутаційний доказ: прибери гілку з «—» — клітинки знову порожні.
      expect(cellOf('DOC-000001', 'sheets').textContent?.trim()).toBe('—');
      expect(cellOf('DOC-000001', 'state').textContent?.trim()).toBe('—');
    },
    SlowEnvTimeout,
  );

  it(
    'документ зі станами показує смужку й бейдж, а не позначку відсутності',
    async () => {
      await shown('DOC-000002');

      // Підпис — через каталог (`D-138`: незавантажений каталог дає ⟦ключ⟧).
      expect(stateBadgeOf('DOC-000002').textContent).toBe('⟦status.sheet.Rejected⟧');
      expect(cellOf('DOC-000002', 'sheets').querySelectorAll('[data-segment]')).toHaveLength(4);
      expect(cellOf('DOC-000002', 'sheets').textContent).not.toContain('—');
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: тон стану аркуша приходить із набору (UI-06)', () => {
  it(
    'чотири стани документа — і тони РІЗНІ, а не «без кольору» на всіх',
    async () => {
      await shown('DOC-000002');

      const tone = (code: string): string | null => segmentOf('DOC-000002', code).getAttribute('data-tone');

      expect(tone('S3')).toBe('danger');
      expect(tone('S2')).toBe('info');
      expect(tone('S1')).toBe('neutral');
      expect(tone('GEN')).toBe('neutral');

      // ⛔ Константний стан на всіх аркушах схлопнув би множину тонів.
      expect(new Set(['S1', 'S2', 'S3', 'GEN'].map(tone)).size).toBe(3);

      // Draft і Approved — обидва нейтральні (KIT §1.3), але різні ФОРМОЮ: стан у розмітці.
      expect(segmentOf('DOC-000002', 'S1').getAttribute('data-state')).toBe('Draft');
      expect(segmentOf('DOC-000002', 'GEN').getAttribute('data-state')).toBe('Approved');
    },
    SlowEnvTimeout,
  );

  it(
    'стан документа — найгірший з аркушів (Rejected > Draft > Submitted > Approved), як рахує сервер',
    async () => {
      await shown('DOC-000002');

      expect(stateBadgeOf('DOC-000002').getAttribute('data-status-state')).toBe('Rejected');
      expect(stateBadgeOf('DOC-000002').getAttribute('data-status-tone')).toBe('danger');
      expect(stateBadgeOf('DOC-000005').getAttribute('data-status-state')).toBe('Draft');
      expect(stateBadgeOf('DOC-000007').getAttribute('data-status-state')).toBe('Approved');
    },
    SlowEnvTimeout,
  );

  it(
    'аркуш і його стан — одна пара: підказка сегмента називає саме цей аркуш',
    async () => {
      await shown('DOC-000002');

      const rejected = segmentOf('DOC-000002', 'S3');
      expect(rejected.getAttribute('data-state')).toBe('Rejected');
      expect(rejected.getAttribute('title')).toBe('S3 — ⟦status.sheet.Rejected⟧');

      // Читалка отримує весь перелік пар одним текстом.
      const label = cellOf('DOC-000002', 'sheets').querySelector('[role="img"]')?.getAttribute('aria-label') ?? '';
      expect(label).toContain('S3: ⟦status.sheet.Rejected⟧');
      expect(label).toContain('GEN: ⟦status.sheet.Approved⟧');
    },
    SlowEnvTimeout,
  );

  it(
    'стан, якого набір не знає, позначений як невідомий, а не мовчки нейтральний',
    async () => {
      expect(statusTable.sheet['Returned'], '`Returned` не має бути в таблиці набору').toBeUndefined();
      expect(statusTable.sheet['Draft'], '`Draft` — відомий стан домену').toBeDefined();

      await shown('DOC-000003');

      expect(segmentOf('DOC-000003', 'S1').getAttribute('data-tone')).toBe('warning');

      const unknown = stateBadgeOf('DOC-000003');
      expect(unknown.getAttribute('data-status-known')).toBe('false');
      expect(unknown.getAttribute('data-status-tone')).toBe('warning');
      expect(unknown.textContent).toBe('⟦status.sheet.Returned⟧');
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: смужка аркушів — назва аркуша, а не код', () => {
  it(
    'внутрішній код аркуша не стоїть у видимому тексті клітинки',
    async () => {
      await shown('DOC-000004');

      expect(cellOf('DOC-000004', 'sheets').textContent).not.toContain('S99819007');
      expect(cellOf('DOC-000007', 'sheets').textContent).not.toContain('S42');
    },
    SlowEnvTimeout,
  );

  it(
    'назва аркуша мовою інтерфейсу — у підказці сегмента; назви немає — код',
    async () => {
      await shown('DOC-000006');

      expect(segmentOf('DOC-000006', 'GEN').getAttribute('title')).toContain('General info');
      expect(segmentOf('DOC-000006', 'GEN').getAttribute('title')).not.toContain('Загальні відомості');
      expect(segmentOf('DOC-000006', 'AIR').getAttribute('title')).toContain('Air emissions');
      // ⛔ Запасний варіант: прибери його — підказка без назви.
      expect(segmentOf('DOC-000006', 'WTR').getAttribute('title')).toContain('WTR');
    },
    SlowEnvTimeout,
  );

  it(
    'аркуші йдуть у порядку `sheets` (порядок аркушів), а не ключів `sheetStates`',
    async () => {
      await shown('DOC-000006');

      const states = [...cellOf('DOC-000006', 'sheets').querySelectorAll('[data-segment]')].map((node) =>
        node.getAttribute('data-state'),
      );

      expect(states).toEqual(['Draft', 'Submitted', 'Rejected']);
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: прихований аркуш не видно ні рядком, ні числом (P1)', () => {
  it(
    'роль зі scope на 1 аркуш: один сегмент, «0 of 1», помилки «—», жодного сліду інших аркушів',
    async () => {
      await shown('DOC-000008');

      const sheets = cellOf('DOC-000008', 'sheets');
      expect(sheets.querySelectorAll('[data-segment]')).toHaveLength(1);

      // ⛔ Мутаційний доказ: передай у `SegmentBar` `total={document.sheetCount}` —
      // тут стане «0/3», тобто число видасть два приховані аркуші.
      expect(sheets.querySelector('[data-segment-summary]')?.getAttribute('data-segment-summary')).toBe('0/1');

      // `errorCount: null` — «—», а не «0».
      expect(cellOf('DOC-000008', 'issues').textContent?.trim()).toBe('—');

      // Стан документа — лише з видимого аркуша.
      expect(stateBadgeOf('DOC-000008').getAttribute('data-status-state')).toBe('Submitted');

      // Партій аркуша з частковими станами (DOC-000005: 1 стан при sheetCount 2) — так само.
      expect(
        cellOf('DOC-000005', 'sheets').querySelector('[data-segment-summary]')?.getAttribute('data-segment-summary'),
      ).toBe('0/1');
    },
    SlowEnvTimeout,
  );
});
