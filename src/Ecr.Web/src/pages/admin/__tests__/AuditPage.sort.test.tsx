import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { cellChangesQuery, workflowEventsQuery } from '@/features/audit/api';
import { directionOf, nextAuditSort, parseAuditSort } from '@/features/audit/auditSort';
import { clippedComment, CommentCellMaxChars } from '@/features/audit/WorkflowEventsPanel';
import { testTheme } from '@/test/render';

/**
 * Журнали аудиту: порядок — серверний. Типово найновіші першими (параметра `sort` у запиті немає), заголовки
 * «Changed at» і «By user» перемикають порядок через `?sort=` і запит, `aria-sort` стоїть на активній колонці,
 * «показати ще» несе курсор того самого порядку, зміна порядку скидає курсор. Експорт CSV є на всіх вкладках.
 *
 * ⛔ Мутація: прибрати `sort` з `applied` у `AuditPage` — червоніє «заголовок перемикає порядок у запиті»;
 * прибрати `aria-sort` у `SortableTh` — червоніє перевірка доступності.
 */
const SlowEnvTimeout = 400_000;

function cellPage(next: string | null = null): unknown {
  return {
    items: [
      {
        changedAt: '2026-01-05T10:00:00Z',
        periodKey: 202601,
        documentId: 7,
        rowKey: 'R1',
        columnDefId: 3,
        oldValue: '1',
        newValue: '2',
        changedByUserId: 41,
        origin: 'UserEdit',
        isLateEdit: false,
        isOutOfWindow: false,
        isOverwrite: false,
        fromArchive: false,
        changedByDisplayName: 'Ann',
        documentBusinessKey: 'DOC-7',
        columnCode: 'C3',
      },
    ],
    nextCursor: next,
    totalCount: 1,
  };
}

const workflowPage = {
  items: [
    {
      changedAt: '2026-01-05T10:00:00Z',
      action: 'Reject',
      fromStatus: 'Submitted',
      toStatus: 'Draft',
      comment: 'x'.repeat(CommentCellMaxChars + 30),
      changedByUserId: 41,
      changedByDisplayName: 'Boss',
      documentId: 7,
      documentBusinessKey: 'DOC-7',
      sheetDefId: 2,
      sheetCode: 'S2',
      periodKey: 202601,
      stepOrdinal: null,
    },
    {
      changedAt: '2026-01-04T10:00:00Z',
      action: 'Submit',
      fromStatus: 'Draft',
      toStatus: 'Submitted',
      comment: '<b>not html</b>',
      changedByUserId: null,
      changedByDisplayName: null,
      documentId: 7,
      documentBusinessKey: 'DOC-7',
      sheetDefId: 2,
      sheetCode: 'S2',
      periodKey: 202601,
      stepOrdinal: null,
    },
  ],
  nextCursor: null,
  totalCount: null,
};

const structurePage = {
  items: [
    {
      changedAt: '2026-01-05T10:00:00Z',
      entityType: 'ColumnDef',
      entityId: 36396,
      operation: 'Update',
      oldJson: null,
      newJson: null,
      changeReason: null,
      changedByUserId: 41,
      changedByDisplayName: 'Ann',
      entityLabel: 'BG_Total',
    },
    {
      changedAt: '2026-01-04T10:00:00Z',
      entityType: 'ColumnDef',
      entityId: 5,
      operation: 'Delete',
      oldJson: null,
      newJson: null,
      changeReason: null,
      changedByUserId: 41,
      changedByDisplayName: 'Ann',
      entityLabel: null,
    },
  ],
  nextCursor: null,
  totalCount: null,
};

let seen: string[];
let exportUrls: string[];
let cellsNext: string | null;
let downloads: string[];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  seen = [];
  exportUrls = [];
  downloads = [];
  cellsNext = null;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({ userId: 1, userName: 'a', language: 'en', permissions: ['Security.ViewAudit'] });
      }

      if (url.includes('/export.csv')) {
        exportUrls.push(url);
        const name = /audit\/(\w+)\/export/.exec(url)?.[1] ?? 'x';

        return new Response('a,b\r\n', {
          status: 200,
          headers: { 'Content-Type': 'text/csv; charset=utf-8', 'Content-Disposition': `attachment; filename=audit-${name}.csv` },
        });
      }

      if (url.includes('/api/v1/audit/')) {
        seen.push(url);
        if (url.includes('/audit/workflow?')) return json(workflowPage);
        if (url.includes('/audit/structure?')) return json(structurePage);
        if (url.includes('/audit/security?')) {
          return json({
            items: [
              {
                changedAt: '2026-01-05T10:00:00Z',
                eventType: 'AccessDenied',
                targetUserId: null,
                targetRoleId: null,
                detailsJson: null,
                changedByUserId: 41,
                changedByDisplayName: 'Intruder',
                correlationId: 'corr-1',
              },
            ],
            nextCursor: null,
            totalCount: null,
          });
        }

        return json(cellPage(cellsNext));
      }

      return json(null);
    }),
  );

  URL.createObjectURL = vi.fn(() => 'blob:test/1');
  URL.revokeObjectURL = vi.fn();
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    downloads.push(this.download);
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderPage(entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <AuditPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const Base = 'from=2026-01-01&to=2026-01-08';

function header(name: RegExp | string): HTMLElement {
  return screen.getByRole('columnheader', { name });
}

describe('порядок: чисті функції', () => {
  it('типовий — найновіші першими; зміна напрямку й колонки', () => {
    expect(parseAuditSort(null)).toBe('ChangedAtDesc');
    expect(parseAuditSort('Nonsense')).toBe('ChangedAtDesc');
    expect(parseAuditSort('UserDesc')).toBe('UserDesc');

    expect(directionOf('ChangedAtDesc', 'time')).toBe('desc');
    expect(directionOf('ChangedAtDesc', 'user')).toBeNull();

    expect(nextAuditSort('ChangedAtDesc', 'time')).toBe('ChangedAtAsc');
    expect(nextAuditSort('ChangedAtAsc', 'time')).toBe('ChangedAtDesc');
    expect(nextAuditSort('ChangedAtDesc', 'user')).toBe('UserAsc');
    expect(nextAuditSort('UserAsc', 'user')).toBe('UserDesc');
    expect(nextAuditSort('UserDesc', 'user')).toBe('UserAsc');
    expect(nextAuditSort('UserAsc', 'time')).toBe('ChangedAtDesc');
  });

  it('запит несе sort лише коли порядок не типовий', () => {
    expect(new URLSearchParams(cellChangesQuery({ from: '2026-01-01', to: '2026-01-08' })).has('sort')).toBe(false);
    expect(
      new URLSearchParams(cellChangesQuery({ from: '2026-01-01', to: '2026-01-08', sort: 'UserAsc' })).get('sort'),
    ).toBe('UserAsc');
    expect(
      new URLSearchParams(workflowEventsQuery({ from: '2026-01-01', to: '2026-01-08', sort: 'ChangedAtAsc' })).get('sort'),
    ).toBe('ChangedAtAsc');
  });

  it('довгий коментар обрізається, короткий — ні', () => {
    expect(clippedComment('short')).toBe('short');
    expect(clippedComment('y'.repeat(CommentCellMaxChars + 1))).toHaveLength(CommentCellMaxChars + 1);
    expect(clippedComment('y'.repeat(CommentCellMaxChars + 1)).endsWith('…')).toBe(true);
  });
});

describe('AuditPage: журнал комірок', () => {
  it(
    'типово без sort у запиті; aria-sort=descending на «Changed at»; заголовок перемикає порядок у запиті',
    async () => {
      renderPage(`/admin/audit?${Base}`);
      await screen.findByText('Ann', {}, { timeout: SlowEnvTimeout });

      const first = new URLSearchParams(seen[0]!.split('?')[1]);
      expect(first.has('sort')).toBe(false);

      expect(header(/audit\.when/).getAttribute('aria-sort')).toBe('descending');
      expect(header(/audit\.who/).getAttribute('aria-sort')).toBe('none');

      fireEvent.click(within(header(/audit\.when/)).getByRole('button'));
      await waitFor(() => expect(header(/audit\.when/).getAttribute('aria-sort')).toBe('ascending'));
      await waitFor(() => expect(seen.some((u) => u.includes('sort=ChangedAtAsc'))).toBe(true));

      fireEvent.click(within(header(/audit\.who/)).getByRole('button'));
      await waitFor(() => expect(header(/audit\.who/).getAttribute('aria-sort')).toBe('ascending'));
      await waitFor(() => expect(seen.some((u) => u.includes('sort=UserAsc'))).toBe(true));
      expect(header(/audit\.when/).getAttribute('aria-sort')).toBe('none');

      fireEvent.click(within(header(/audit\.who/)).getByRole('button'));
      await waitFor(() => expect(header(/audit\.who/).getAttribute('aria-sort')).toBe('descending'));
    },
    SlowEnvTimeout,
  );

  it(
    '«показати ще» несе курсор того самого порядку, а зміна порядку скидає курсор',
    async () => {
      cellsNext = 'CURSOR-1';
      renderPage(`/admin/audit?${Base}&sort=UserAsc`);
      await screen.findByText('Ann', {}, { timeout: SlowEnvTimeout });

      expect(header(/audit\.who/).getAttribute('aria-sort')).toBe('ascending');

      fireEvent.click(screen.getByRole('button', { name: /documents\.more|more/i }));
      await waitFor(() => expect(seen.some((u) => u.includes('cursor=CURSOR-1'))).toBe(true));
      const withCursor = seen.find((u) => u.includes('cursor=CURSOR-1'))!;
      expect(new URLSearchParams(withCursor.split('?')[1]).get('sort')).toBe('UserAsc');

      // Зміна порядку: наступний запит — без старого курсора.
      const before = seen.length;
      fireEvent.click(within(header(/audit\.when/)).getByRole('button'));
      await waitFor(() => expect(seen.length).toBeGreaterThan(before));
      const after = new URLSearchParams(seen[seen.length - 1]!.split('?')[1]);
      expect(after.has('cursor')).toBe(false);
      expect(after.get('sort')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'експорт CSV на вкладці комірок: серверний запит із фільтрами й порядком, без limit/cursor, файл завантажується',
    async () => {
      renderPage(`/admin/audit?${Base}&origin=Import&sort=ChangedAtAsc`);
      const button = await screen.findByRole('button', { name: /audit\.exportCsv|CSV/ }, { timeout: SlowEnvTimeout });

      fireEvent.click(button);
      await waitFor(() => expect(downloads).toEqual(['audit-cells.csv']));

      const query = new URLSearchParams(exportUrls[0]!.split('?')[1]);
      expect(exportUrls[0]).toContain('/api/v1/audit/cells/export.csv?');
      expect(query.get('origin')).toBe('Import');
      expect(query.get('sort')).toBe('ChangedAtAsc');
      expect(query.has('limit')).toBe(false);
      expect(query.has('cursor')).toBe(false);
    },
    SlowEnvTimeout,
  );
});

describe('AuditPage: решта вкладок', () => {
  it(
    'структура: сортується, показує код сутності з номером другим рядком, видалену — номером; експорт є',
    async () => {
      renderPage(`/admin/audit?view=structure&${Base}`);
      await screen.findByText(/BG_Total/, {}, { timeout: SlowEnvTimeout });

      expect(screen.getByText('#36396')).toBeTruthy();
      expect(screen.getByText(/· 5$/)).toBeTruthy();
      expect(header(/audit\.when/).getAttribute('aria-sort')).toBe('descending');

      fireEvent.click(within(header(/audit\.who/)).getByRole('button'));
      await waitFor(() => expect(seen.some((u) => u.includes('/audit/structure?') && u.includes('sort=UserAsc'))).toBe(true));

      fireEvent.click(screen.getByRole('button', { name: /audit\.exportCsv|CSV/ }));
      await waitFor(() => expect(exportUrls).toHaveLength(1));
      expect(exportUrls[0]).toContain('/api/v1/audit/structure/export.csv?');
      expect(new URLSearchParams(exportUrls[0]!.split('?')[1]).get('sort')).toBe('UserAsc');
    },
    SlowEnvTimeout,
  );

  it(
    'безпека: заголовки сортують, експорт CSV є',
    async () => {
      renderPage(`/admin/audit?view=security&${Base}`);
      const button = await screen.findByRole('button', { name: /audit\.exportCsv|CSV/ }, { timeout: SlowEnvTimeout });
      await screen.findByText('Intruder', {}, { timeout: SlowEnvTimeout });
      expect(header(/audit\.when/).getAttribute('aria-sort')).toBe('descending');

      fireEvent.click(within(header(/audit\.when/)).getByRole('button'));
      await waitFor(() => expect(seen.some((u) => u.includes('/audit/security?') && u.includes('sort=ChangedAtAsc'))).toBe(true));

      fireEvent.click(button);
      await waitFor(() => expect(downloads).toEqual(['audit-security.csv']));
      expect(exportUrls[0]).toContain('/api/v1/audit/security/export.csv?');
    },
    SlowEnvTimeout,
  );

  it(
    'погодження: дія словом, коментар текстом (не HTML) і обрізаний, система без автора, експорт CSV є',
    async () => {
      renderPage(`/admin/audit?view=workflow&${Base}`);
      await screen.findByText(/audit\.workflowAction\.Reject/, {}, { timeout: SlowEnvTimeout });

      // Коментар — звичайний текст: розмітка не інтерпретується.
      expect(screen.getByText('<b>not html</b>')).toBeTruthy();
      expect(document.querySelector('b')).toBeNull();

      // Довгий — обрізаний у клітинці, повний у title.
      const clipped = screen.getByText(`${'x'.repeat(CommentCellMaxChars)}…`);
      expect(clipped.closest('td')?.getAttribute('title')).toHaveLength(CommentCellMaxChars + 30);

      expect(screen.getByText(/audit\.systemActor|System/)).toBeTruthy();
      expect(screen.getByText('Boss')).toBeTruthy();
      expect(seen[0]).toContain('/api/v1/audit/workflow?');

      fireEvent.click(screen.getByRole('button', { name: /audit\.exportCsv|CSV/ }));
      await waitFor(() => expect(downloads).toEqual(['audit-workflow.csv']));
      expect(exportUrls[0]).toContain('/api/v1/audit/workflow/export.csv?');
    },
    SlowEnvTimeout,
  );
});
