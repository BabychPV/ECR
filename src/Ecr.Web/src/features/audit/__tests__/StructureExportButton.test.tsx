import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { fileNameOf } from '@/features/audit/api';
import { testTheme } from '@/test/render';

/**
 * `BE-16` ч.2: «Експорт CSV» журналу структурних змін.
 *
 * ⛔ Предмет — те, що бачить людина: файл із ТИМИ фільтрами, що на екрані, з
 * ім'ям від сервера; а на відмову — банер із текстом сервера, не тиша.
 */
const page = { items: [], nextCursor: null, totalCount: null };

type ExportReply = () => Response;

let exportReply: ExportReply;
let exportUrls: string[];
let permissions: string[];

function csvReply(disposition: string | null): ExportReply {
  return () =>
    new Response('ChangedAt,Entity\n', {
      status: 200,
      headers: {
        'Content-Type': 'text/csv; charset=utf-8',
        ...(disposition === null ? {} : { 'Content-Disposition': disposition }),
      },
    });
}

function json(body: unknown, status = 200, type = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });
}

const created: string[] = [];
const revoked: string[] = [];
const downloads: string[] = [];

beforeEach(() => {
  exportUrls = [];
  permissions = ['Security.ViewAudit'];
  exportReply = csvReply('attachment; filename=audit-structure-20260101-20260108.csv');
  created.length = 0;
  revoked.length = 0;
  downloads.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) {
        return json({ userId: 1, userName: 'a', language: 'en', permissions });
      }
      if (url.includes('/api/v1/audit/structure/export.csv')) {
        exportUrls.push(url);
        return exportReply();
      }
      if (url.includes('/api/v1/audit/structure?')) return json(page);
      return json(null);
    }),
  );

  let n = 0;
  URL.createObjectURL = vi.fn(() => {
    const url = `blob:test/${++n}`;
    created.push(url);
    return url;
  });
  URL.revokeObjectURL = vi.fn((url: string) => {
    revoked.push(url);
  });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    downloads.push(this.download);
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const SlowEnvTimeout = 400_000;
const Filters =
  '/admin/audit?view=structure&from=2026-01-01&to=2026-01-08&entityType=cfg.RegistryDef&changedBy=41';
const ButtonName = /audit\.exportCsv|CSV/;

function renderPage(entry = Filters): void {
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

async function exportButton(): Promise<HTMLButtonElement> {
  return (await screen.findByRole('button', { name: ButtonName }, { timeout: SlowEnvTimeout })) as HTMLButtonElement;
}

function currentButton(): HTMLButtonElement {
  return screen.getByRole('button', { name: ButtonName }) as HTMLButtonElement;
}

describe('AuditPage: експорт журналу структурних змін у CSV', () => {
  it(
    '200 → файл з іменем сервера, фільтри екрана в запиті, URL звільнено, банера немає',
    async () => {
      renderPage();
      fireEvent.click(await exportButton());

      await waitFor(() => expect(downloads).toHaveLength(1), { timeout: SlowEnvTimeout });

      // ⛔ Мутація: не передати entityType/changedBy у запит — червоне тут.
      expect(exportUrls).toHaveLength(1);
      const query = new URLSearchParams(exportUrls[0]!.split('?')[1]);
      expect(query.get('from')).toBe('2026-01-01');
      expect(query.get('to')).toBe('2026-01-08');
      expect(query.get('entityType')).toBe('cfg.RegistryDef');
      expect(query.get('changedByUserId')).toBe('41');
      expect(query.has('limit')).toBe(false);
      expect(query.has('cursor')).toBe(false);

      // ⛔ Мутація: ігнорувати Content-Disposition — червоне тут.
      expect(downloads[0]).toBe('audit-structure-20260101-20260108.csv');
      // ⛔ Мутація: не викликати revokeObjectURL — червоне тут.
      expect(created).toHaveLength(1);
      expect(revoked).toStrictEqual(created);
      expect(screen.queryByRole('alert')).toBeNull();
      await waitFor(() => expect(currentButton().disabled).toBe(false));
    },
    SlowEnvTimeout,
  );

  it(
    "без Content-Disposition → запасне ім'я audit-structure.csv",
    async () => {
      exportReply = csvReply(null);
      renderPage();
      fireEvent.click(await exportButton());

      await waitFor(() => expect(downloads).toStrictEqual(['audit-structure.csv']), {
        timeout: SlowEnvTimeout,
      });
    },
    SlowEnvTimeout,
  );

  it(
    '422 auditExportTooLarge → банер із кодом сервера, файлу немає, кнопка знову доступна',
    async () => {
      exportReply = () =>
        json(
          {
            title: 'Unprocessable',
            status: 422,
            errorCode: 'ECR-REQ-0422',
            correlationId: 'cid-export-422',
            messageKey: 'err.ECR-REQ-0422.auditExportTooLarge',
            total: 900000,
            max: 500000,
          },
          422,
          'application/problem+json',
        );
      renderPage();
      fireEvent.click(await exportButton());

      // ⛔ Мутація: не показувати ErrorAlert — червоне тут.
      const alert = await screen.findByRole('alert', {}, { timeout: SlowEnvTimeout });
      expect(alert.textContent).toContain('ECR-REQ-0422');
      expect(alert.textContent).toContain('cid-export-422');
      expect(downloads).toHaveLength(0);
      expect(created).toHaveLength(0);
      await waitFor(() => expect(currentButton().disabled).toBe(false));
    },
    SlowEnvTimeout,
  );

  it(
    'поки файл готується — кнопка в стані loading і повторно не шле запит',
    async () => {
      let release: (r: Response) => void = () => undefined;
      const pending = new Promise<Response>((resolve) => {
        release = resolve;
      });
      const base = globalThis.fetch;
      vi.stubGlobal(
        'fetch',
        vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
          if (String(input).includes('/export.csv')) {
            exportUrls.push(String(input));
            return pending;
          }
          return base(input, init);
        }),
      );

      renderPage();
      const button = await exportButton();
      fireEvent.click(button);

      await waitFor(() => expect(button.hasAttribute('data-loading')).toBe(true));
      fireEvent.click(button);
      expect(exportUrls).toHaveLength(1);

      release(csvReply(null)());
      await waitFor(() => expect(downloads).toHaveLength(1), { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );

  it(
    'вікно навпаки (from > to) → кнопка недоступна',
    async () => {
      renderPage('/admin/audit?view=structure&from=2026-02-01&to=2026-01-08');
      expect((await exportButton()).disabled).toBe(true);
    },
    SlowEnvTimeout,
  );

  it(
    'без Security.ViewAudit кнопки немає',
    async () => {
      permissions = [];
      renderPage();
      await waitFor(() => expect(fetch).toHaveBeenCalled());
      await new Promise((r) => setTimeout(r, 50));
      expect(screen.queryByRole('button', { name: ButtonName })).toBeNull();
    },
    SlowEnvTimeout,
  );
});

describe('fileNameOf', () => {
  it('filename, filename* і відсутність', () => {
    expect(fileNameOf('attachment; filename=a.csv')).toBe('a.csv');
    expect(fileNameOf('attachment; filename="b c.csv"')).toBe('b c.csv');
    expect(fileNameOf("attachment; filename=x.csv; filename*=UTF-8''%D0%B6.csv")).toBe('ж.csv');
    expect(fileNameOf('attachment; filename=../../evil.csv')).toBe('.._.._evil.csv');
    expect(fileNameOf(null)).toBeNull();
    expect(fileNameOf('attachment')).toBeNull();
  });
});
