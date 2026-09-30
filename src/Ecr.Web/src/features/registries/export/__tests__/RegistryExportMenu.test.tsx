import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { loadCatalog } from '@/shared/i18n';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { RegistryExportButton } from '../RegistryExportButton';
import { registryExportErrorText } from '../exportError';

/**
 * Експорт довідника (RT-16) у клієнті: файл за форматом і датою чинності, ім'я — від сервера; відмови
 * `403` і `422` стелі — реченням, що каже, що робити.
 *
 * ⚠ Тексти — САМ `09-seed.sql` (`Catalog`): ключ, забутий у сіді, дав би на екрані `⟦…⟧`.
 *
 * ⚠ Мутаційні докази (перевірено руками, 2026-09-30; кожна мутація — червоний тест):
 *   - `asOf` не передається в `fetchRegistryExport` → «CSV: формат і дата…»;
 *   - ім'я файлу завжди запасне (без `file.fileName`) → «CSV: формат і дата…»;
 *   - гілку `403` прибрано → «403 — речення про право читання…»;
 *   - підказку стелі прибрано (`hint: null`) → «422 стелі — текст сервера і підказка…»;
 *   - `includeChildren` не передається четвертим аргументом → «з частинами композиції: includeChildren=true…»;
 *   - запасне ім'я ZIP без `.zip` → «з частинами композиції: includeChildren=true…».
 */

function json(body: unknown, status = 200, type = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });
}

let exportUrls: string[];
let exportReply: () => Response;
const downloads: string[] = [];

beforeEach(async () => {
  exportUrls = [];
  downloads.length = 0;
  exportReply = () =>
    new Response('code,T\r\nE1,1.5\r\n', {
      status: 200,
      headers: {
        'Content-Type': 'text/csv; charset=utf-8',
        'Content-Disposition': "attachment; filename=registry-GAS-20260930.csv; filename*=UTF-8''registry-GAS-20260930.csv",
      },
    });

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
      if (url.includes('/export')) {
        exportUrls.push(url);
        return exportReply();
      }
      return json(null);
    }),
  );
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:x', revokeObjectURL: () => undefined }));
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    downloads.push(this.download);
  });

  await loadCatalog('en', 'private');
  await loadCatalog('en', 'public');
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

function show(asOf: string | null = '2026-09-30'): void {
  render(
    <MantineProvider theme={testTheme}>
      <RegistryExportButton registryCode="GAS" asOf={asOf} />
    </MantineProvider>,
  );
}

/** Відкриває меню, коли лінивий чанк уже на місці (запасна кнопка — вимкнена). */
async function open(): Promise<void> {
  await waitFor(() => {
    expect(screen.getByRole('button', { name: 'Export' }).hasAttribute('disabled')).toBe(false);
  });
  fireEvent.click(screen.getByRole('button', { name: 'Export' }));
  await screen.findByRole('button', { name: 'CSV (can be imported back)' });
}

const problem = (status: number, extra: Record<string, unknown>): Response =>
  json({ title: 'err.http.requestFailed', status, errorCode: `ECR-REQ-0${String(status)}`, correlationId: 'c1', ...extra }, status, 'application/problem+json');

describe('Експорт довідника (RT-16)', () => {
  it('CSV: формат і дата чинності в запиті, файл — з ім\'ям від сервера', async () => {
    show();
    await open();
    expect(screen.getByText('Entries effective on 2026-09-30.')).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'CSV (can be imported back)' }));

    await waitFor(() => {
      expect(downloads).toEqual(['registry-GAS-20260930.csv']);
    });
    expect(exportUrls).toEqual(['/api/v1/registries/GAS/export?format=csv&asOf=2026-09-30']);
  });

  it('XLSX нетемпорального довідника — без asOf', async () => {
    show(null);
    await open();

    fireEvent.click(screen.getByRole('button', { name: 'Excel workbook (XLSX)' }));

    await waitFor(() => {
      expect(exportUrls).toEqual(['/api/v1/registries/GAS/export?format=xlsx']);
    });
  });

  it('403 — речення про право читання, а не загальне «доступ заборонено»', async () => {
    exportReply = () => problem(403, {});
    show();
    await open();

    fireEvent.click(screen.getByRole('button', { name: 'CSV (can be imported back)' }));

    const alert = await screen.findByTestId('registry-export-error');
    expect(alert.textContent).toContain('Export failed');
    expect(alert.textContent).toContain('You do not have read access to this registry');
    expect(downloads).toEqual([]);
  });

  it('422 стелі — текст сервера з числами і підказка, що файл не обрізається', async () => {
    exportReply = () =>
      problem(422, {
        detail: 'Registry "GAS" has 60000 entries to export, the limit is 50000.',
        messageKey: 'err.ECR-REQ-0422.registryExportTooLarge',
        registryCode: 'GAS',
        total: '60000',
        max: '50000',
      });
    show();
    await open();

    fireEvent.click(screen.getByRole('button', { name: 'Excel workbook (XLSX)' }));

    const alert = await screen.findByTestId('registry-export-error');
    expect(alert.textContent).toContain('Registry "GAS" has 60000 entries to export, the limit is 50000.');
    expect(alert.textContent).toContain('Registries:ExportMaxRows');
    expect(downloads).toEqual([]);
  });

  it('без прапорця частин композиції запит не містить includeChildren', async () => {
    show();
    await open();

    const box = screen.getByRole('checkbox', { name: 'With child parts (composition)' });
    expect(box.hasAttribute('disabled')).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'CSV (can be imported back)' }));
    await waitFor(() => {
      expect(exportUrls).toHaveLength(1);
    });
    expect(exportUrls[0]).not.toContain('includeChildren');
  });

  it('з частинами композиції: includeChildren=true в запиті, ZIP без імені від сервера — запасне .zip', async () => {
    exportReply = () => new Response('PK', { status: 200, headers: { 'Content-Type': 'application/zip' } });
    show();
    await open();

    fireEvent.click(screen.getByRole('checkbox', { name: 'With child parts (composition)' }));
    fireEvent.click(screen.getByRole('button', { name: 'CSV (can be imported back)' }));

    await waitFor(() => {
      expect(downloads).toEqual(['registry-GAS.zip']);
    });
    expect(exportUrls).toEqual(['/api/v1/registries/GAS/export?format=csv&asOf=2026-09-30&includeChildren=true']);
  });

  it('не наша відмова (мережа) — загальна назва, без підказки стелі', () => {
    const shown = registryExportErrorText(new TypeError('Failed to fetch'));
    expect(shown.hint).toBeNull();
    expect(shown.title).toBe('Export failed');
  });
});
