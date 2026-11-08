import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Стан документа в рядку переліку: позначка відсутності (F6), стан не лише кольором (UI-06), без внутрішніх кодів
 * аркушів і без сліду прихованих аркушів (P1).
 *
 * ✎ Projects/Documents 08.11 (макет `projects-documents-1108.html`, людина 08.11): перелік — лише за РІК. Колонок
 * «Sheets» (смужка аркушів одного місяця) і «State» (бейдж стану за місяць) більше немає — їхні тести прибрано; ті
 * самі гарантії тепер перевіряються на смузі 12 місяців (`DocumentYearStrip`, клітинки `[data-month]` з `data-cell`).
 *
 * ⚠ Тест дивиться саме на КЛІТИНКУ рядка, а не на сторінку загалом: «десь на екрані є тире» — не те твердження,
 * яке доводить знахідку.
 */
const Year = 2026;

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

/* Календар проєкту: січень заблоковано, лютий відкрито (поточний), березень ще не відкрито; решти місяців немає. */
const calendar = {
  periods: [
    { periodKey: 202601, year: Year, sequence: 1, state: 'Closed', isCurrent: false },
    { periodKey: 202602, year: Year, sequence: 2, state: 'Open', isCurrent: true },
    { periodKey: 202603, year: Year, sequence: 3, state: 'Scheduled', isCurrent: false },
  ],
};

const withoutStates = {
  id: 1,
  businessKey: 'DOC-000001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: {},
  periodStates: [],
};

/*
 * ⚠ Різні стани погодження в РІЗНИХ місяцях одного документа: тест на одному стані лишався б зеленим і тоді, коли всі
 * клітинки стали однаковими — доказ саме розрізнення.
 */
const withStates = {
  id: 2,
  businessKey: 'DOC-000002',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 4,
  sheetStates: { S1: 'Draft', S2: 'Submitted', S3: 'Rejected', GEN: 'Approved' },
  periodStates: [
    { periodKey: 202604, state: 'Approved' },
    { periodKey: 202605, state: 'Submitted' },
    { periodKey: 202606, state: 'Rejected' },
    { periodKey: 202607, state: 'PartlySubmitted' },
  ],
};

/* ⚠ `Returned` — стан із МАКЕТА, якого в домені немає; `state` у схемі — рядок, компілятор про нього не скаже. */
const withUnknown = {
  id: 3,
  businessKey: 'DOC-000003',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Returned' },
  periodStates: [{ periodKey: 202608, state: 'Returned' }],
};

/* ⚠ Внутрішній код аркуша (знімок людини: `S99819007` перед станом) — не текст інтерфейсу. */
const singleSheet = {
  id: 4,
  businessKey: 'DOC-000004',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S99819007: 'Submitted' },
  periodStates: [{ periodKey: 202602, state: 'Submitted' }],
};

/*
 * ⛔ Прихований аркуш (P1, безпека): роль бачить ОДИН аркуш із трьох; `errorCount` — `null`. `sheetCount` навмисно 3:
 * якщо рядок рахує щось від нього, число видає існування прихованих аркушів.
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
  periodStates: [{ periodKey: 202602, state: 'Submitted' }],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

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

      if (url.includes('/api/v1/documents/summary')) return json({ draft: 0, submitted: 0, approved: 0, rejected: 0, withIssues: 0 });
      if (url.includes('/api/v1/documents/template-versions')) return json({ items: [], withoutVersionCount: 0 });

      if (url.includes('/api/v1/documents')) {
        return json({
          items: [withoutStates, withStates, withUnknown, singleSheet, scopedToOneSheet],
          nextCursor: null,
          totalCount: 5,
        });
      }

      if (/\/api\/v1\/projects\/1\/periods/.test(url)) return json(calendar);
      if (url.includes('/api/v1/projects')) return json({ items: [project], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function show(entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Клітинка колонки рядка з таким бізнес-ключем (`data-column`). */
function cellOf(businessKey: string, column: 'months' | 'issues'): HTMLElement {
  const row = document.querySelector(`[data-document-row="${businessKey}"]`);
  if (row === null) throw new Error(`Рядок ${businessKey} не знайдено`);

  const cell = within(row as HTMLElement)
    .getAllByRole('cell')
    .find((node) => node.getAttribute('data-column') === column);
  if (cell === undefined) throw new Error(`Колонку ${column} у рядку ${businessKey} не знайдено`);

  return cell;
}

/** Клітинка смуги місяця в рядку документа. */
function monthOf(businessKey: string, periodKey: number): HTMLElement {
  const node = cellOf(businessKey, 'months').querySelector(`[data-month="${String(periodKey)}"]`);
  if (node === null) throw new Error(`Місяць ${String(periodKey)} у рядку ${businessKey} не знайдено`);

  return node as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

async function shown(businessKey: string, entry = `/?year=${String(Year)}`): Promise<void> {
  mockFetch();
  show(entry);
  await screen.findByText(businessKey, {}, { timeout: SlowEnvTimeout });
}

describe('DocumentsPage: відсутність станів позначена видимо (F6)', () => {
  it(
    // ✎ Projects/Documents 08.11: колонок «Sheets»/«State» немає — «—» замість порожнечі тепер дає смуга (стан місяця текстом) і «Issues».
    'документ без станів погодження: кожна з 12 клітинок має стан текстом, а не порожнечу',
    async () => {
      await shown('DOC-000001');

      const cells = [...cellOf('DOC-000001', 'months').querySelectorAll('[data-month]')];
      expect(cells).toHaveLength(12);
      // ⛔ Порожня клітинка без підпису читалась би двояко («немає» чи «не завантажилось»).
      for (const cell of cells) {
        expect(cell.getAttribute('data-cell')).not.toBeNull();
        expect(cell.getAttribute('aria-label') ?? '').toMatch(/^⟦documents\.monthState \(month=\w+ 2026, state=⟦documents\.strip\.\w+⟧\)⟧$/);
      }
      // Стан неподаного місяця — з календаря проєкту; місяця поза календарем — «unknown», а не вигаданий.
      expect(monthOf('DOC-000001', 202612).getAttribute('data-cell')).toBe('unknown');
      expect(cellOf('DOC-000001', 'issues').textContent?.trim()).toBe('—');
    },
    SlowEnvTimeout,
  );

  it(
    // ✎ Projects/Documents 08.11: стара адреса з місяцем поза календарем (F6: `?periodKey=190001`) — рік 1900, клітинки «unknown», не порожні.
    'стара адреса з місяцем без календаря: рядок є, клітинки року — «unknown» з підписом',
    async () => {
      await shown('DOC-000001', '/?periodKey=190001');

      const cells = [...cellOf('DOC-000001', 'months').querySelectorAll('[data-month]')];
      expect(cells.map((cell) => cell.getAttribute('data-month'))[0]).toBe('190001');
      expect(new Set(cells.map((cell) => cell.getAttribute('data-cell')))).toEqual(new Set(['unknown']));
      expect(cells[0]?.getAttribute('aria-label')).toBe(
        '⟦documents.monthState (month=January 1900, state=⟦documents.strip.unknown⟧)⟧',
      );
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: стан місяця — не лише кольором (UI-06)', () => {
  it(
    // ✎ Projects/Documents 08.11: тони сегментів аркушів прибрано разом із колонкою «Sheets» — розрізнення станів тепер у клітинках смуги.
    'різні стани погодження дають РІЗНІ клітинки, і кожна названа текстом',
    async () => {
      await shown('DOC-000002');

      expect(monthOf('DOC-000002', 202604).getAttribute('data-cell')).toBe('approved');
      expect(monthOf('DOC-000002', 202605).getAttribute('data-cell')).toBe('submitted');
      expect(monthOf('DOC-000002', 202606).getAttribute('data-cell')).toBe('returned');
      // Частково подано — теж «подано» (людина бачить, що робота пішла).
      expect(monthOf('DOC-000002', 202607).getAttribute('data-cell')).toBe('submitted');

      // ⛔ Константний стан схлопнув би множину.
      const states = [202604, 202605, 202606].map((key) => monthOf('DOC-000002', key).getAttribute('data-cell'));
      expect(new Set(states).size).toBe(3);

      // Не лише колір: знак у клітинці різний (✓ — затверджено, ✕ — повернуто), і стан — у підписі.
      expect(monthOf('DOC-000002', 202604).textContent).toBe('✓');
      expect(monthOf('DOC-000002', 202606).textContent).toBe('✕');
      expect(monthOf('DOC-000002', 202606).getAttribute('aria-label')).toBe(
        '⟦documents.monthState (month=June 2026, state=⟦documents.strip.returned⟧)⟧',
      );
    },
    SlowEnvTimeout,
  );

  it(
    'неподаний місяць бере стан ПЕРІОДУ з календаря: заблоковано, відкрито (поточний), ще не відкрито',
    async () => {
      await shown('DOC-000001');

      // Календар приїжджає окремим запитом — чекаємо, доки клітинка перестане бути «unknown».
      await vi.waitFor(() => expect(monthOf('DOC-000001', 202601).getAttribute('data-cell')).toBe('locked'), {
        timeout: SlowEnvTimeout,
      });
      expect(monthOf('DOC-000001', 202602).getAttribute('data-cell')).toBe('open');
      expect(monthOf('DOC-000001', 202602).getAttribute('data-current')).toBe('true');
      expect(monthOf('DOC-000001', 202603).getAttribute('data-cell')).toBe('notOpen');
    },
    SlowEnvTimeout,
  );

  it(
    // ✎ Projects/Documents 08.11: бейджа «State» немає — невідомий стан не має видаватись за затверджений чи поданий.
    'стан, якого домен не знає (`Returned`), не видається за затверджений чи поданий',
    async () => {
      await shown('DOC-000003');

      const cell = monthOf('DOC-000003', 202608).getAttribute('data-cell');
      expect(['approved', 'submitted']).not.toContain(cell);
      expect(cell).toBe('unknown');
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: рядок — без внутрішнього коду аркуша', () => {
  it(
    // ✎ Projects/Documents 08.11: колонки «Sheets» немає — твердження «коду аркуша немає у видимому тексті» перевіряється на всьому рядку.
    'внутрішній код аркуша не стоїть у видимому тексті рядка',
    async () => {
      await shown('DOC-000004');

      const row = document.querySelector('[data-document-row="DOC-000004"]');
      expect(row?.textContent).not.toContain('S99819007');
    },
    SlowEnvTimeout,
  );
});

describe('DocumentsPage: прихований аркуш не видно ні рядком, ні числом (P1)', () => {
  it(
    // ✎ Projects/Documents 08.11: «0 of 1» жило в колонці «Sheets» (прибрана) — лишається: помилки «—» і жодного сліду інших аркушів.
    'роль зі scope на 1 аркуш: помилки «—», у рядку немає ні числа аркушів, ні назв/кодів інших',
    async () => {
      await shown('DOC-000008');

      // `errorCount: null` — «—», а не «0».
      expect(cellOf('DOC-000008', 'issues').textContent?.trim()).toBe('—');

      const row = document.querySelector('[data-document-row="DOC-000008"]');
      // ⛔ Мутація «рахувати від sheetCount» дала б «/3» або «of 3» у рядку.
      expect(row?.textContent ?? '').not.toMatch(/\b3\b/);
      expect(row?.textContent ?? '').not.toMatch(/segments\.approvedOf/);
      // Стан лютого — лише з видимого аркуша (сервер уже порахував за роллю).
      expect(monthOf('DOC-000008', 202602).getAttribute('data-cell')).toBe('submitted');
    },
    SlowEnvTimeout,
  );

  it(
    // ✎ Live-прохід 08.11 (P3, «видимий текст, а не підказки»): власника приховано доступом — рядок над таблицею.
    'власник прихований доступом (немає ні owner, ні modifiedAt) — видимий приглушений рядок над таблицею',
    async () => {
      await shown('DOC-000008');

      // ⛔ Без фіксу рядка немає: причина жила лише в підказці клітинки.
      const note = document.querySelector('[data-owner-hidden-note]');
      expect(note?.textContent).toBe('⟦documents.ownerHiddenNote⟧');
      expect(cellOf('DOC-000008', 'issues').closest('tr')?.querySelector('[data-owner="hidden"]')).not.toBeNull();
    },
    SlowEnvTimeout,
  );
});
