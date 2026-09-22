import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentVersionCompare } from '@/features/documents/DocumentVersionCompare';
import type { DocumentCompare } from '@/features/documents/documentVersionsApi';
import { loadCatalog, resetMissingReports } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Порівняння версій документа на екрані (`ФВ-5.22`).
 *
 * ⛔ Перевіряються чотири твердження, кожне з яких при поломці робить екран
 * ТИХО НЕПРАВДИВИМ:
 *
 *  1. `truncated` НАЗВАНО — показана частина без попередження відповідає на
 *     питання «що змінилося» неправдою;
 *  2. додані й видалені рядки ВІДРІЗНЯЮТЬСЯ від звичайних змін — інакше новий
 *     рядок читається як зміна «порожньо → значення», тобто як твердження, що
 *     раніше там була порожня комірка;
 *  3. значення показано ЯК ПРИЙШЛО — `1.50` не перетворюється на `1.5`: сервер
 *     порівнює як `decimal`, і масштаб комірки — це дані, а не оформлення;
 *  4. відмова ≠ «версії однакові» (`L10`).
 *
 * ⚠ П'яте — дзеркало до першого й четвертого: коли усічення немає, банера немає
 * зовсім; коли різниця порожня, сказано словами, що версії однакові.
 *
 * ⚠ Каталог — СПРАВЖНІЙ (`loadCatalog` через заглушку `/ui-strings/`), як у
 * `RuleCoveragePanel.test.tsx`: інакше тест перевіряв би позначки `⟦ключ⟧` і
 * лишався б зеленим, якби компонент показував не той ключ.
 */
const Strings: Record<string, string> = {
  'document.compare': 'Compare versions',
  'document.compareFrom': 'From version',
  'document.compareTo': 'To version',
  'document.compareCurrent': 'Current state',
  'document.compareRun': 'Compare',
  'document.compareNoVersions': 'This document has never been submitted for this period.',
  'document.compareIdentical': 'The two versions are identical: nothing changed.',
  'document.compareRowKey': 'Row',
  'document.compareColumn': 'Column',
  'document.compareOldValue': 'Was',
  'document.compareNewValue': 'Became',
  'document.compareAddedTitle': 'Rows added',
  'document.compareAdded': 'new',
  'document.compareRemovedTitle': 'Rows removed',
  'document.compareRemoved': 'removed',
  'document.compareTruncatedTitle': 'Not everything is shown',
  'document.compareTruncatedHint':
    'The server stopped at {changes} changed cell(s), {added} added and {removed} removed row(s); more may exist.',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'common.retry': 'Retry',
};

const Versions = [
  { versionId: 12, sheetDefId: 1, periodKey: 202601, submittedAt: '2026-01-20T10:00:00Z', submittedBy: 'Petrov' },
  { versionId: 11, sheetDefId: 1, periodKey: 202601, submittedAt: '2026-01-10T10:00:00Z', submittedBy: 'Ivanov' },
];

function compareBody(patch: Partial<DocumentCompare>): DocumentCompare {
  return {
    documentId: 7,
    periodKey: 202601,
    fromVersionId: 11,
    toVersionId: null,
    changes: [],
    addedRows: [],
    removedRows: [],
    truncated: false,
    ...patch,
  };
}

/** Відмова сервера у форматі `EcrProblemDetails`. */
interface Refusal {
  readonly status: number;
  readonly errorCode: string;
}

function mockApi(
  compare: DocumentCompare | Refusal,
  versions: unknown = Versions,
): { calls: string[] } {
  const calls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (payload: unknown): Response =>
        new Response(JSON.stringify(payload), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.includes('/versions?') && method === 'GET') {
        calls.push(url);

        return json(versions);
      }

      if (url.includes('/compare?') && method === 'GET') {
        calls.push(url);

        if ('status' in compare) {
          return new Response(
            JSON.stringify({
              title: 'Unprocessable',
              status: compare.status,
              errorCode: compare.errorCode,
              correlationId: 'corr-1',
            }),
            { status: compare.status, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        return json(compare);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { calls };
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <DocumentVersionCompare documentId={7} periodKey={202601} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Розгортає блок, обирає версію-джерело і замовляє порівняння. */
async function runCompare(): Promise<void> {
  fireEvent.click(screen.getByRole('button', { name: 'Compare versions' }));

  fireEvent.click(await screen.findByLabelText('From version'));
  fireEvent.click(await screen.findByRole('option', { name: /Ivanov/ }));

  fireEvent.click(screen.getByRole('button', { name: 'Compare' }));
}

/** Текст кожного рядка таблиці змін — саме те, що бачить людина. */
function changeRows(): string[] {
  return Array.from(document.querySelectorAll('[data-testid="document-compare-changes"] tbody tr'))
    .map((tr) => Array.from(tr.querySelectorAll('td')).map((td) => td.textContent ?? '').join('|'));
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetMissingReports();
});

describe('Порівняння версій: що запитує клієнт', () => {
  it('згорнутий блок не робить ЖОДНОГО запиту (L2)', async () => {
    const { calls } = mockApi(compareBody({}));
    await show();

    await screen.findByRole('button', { name: 'Compare versions' });

    /*
     * ⛔ Мутація «питати версії одразу, щоб список був готовий» валить саме це.
     * Сторінка документа вже робить чотири запити на відкриття, а порівнювати
     * версії приходить одиниця з тих, хто її відкрив.
     */
    expect(calls).toEqual([]);
  }, 60_000);

  it('запити йдуть РІВНО за контрактом — без жодного зайвого параметра', async () => {
    const { calls } = mockApi(compareBody({}));
    await show();
    await runCompare();

    await waitFor(() => {
      expect(calls).toHaveLength(2);
    });

    /*
     * ⛔ Побуквено. Стелю переліку (200 версій) і стелю кожного переліку
     * різниці (1000 записів) ставить СЕРВЕР; `&limit=200`, доданий клієнтом
     * «щоб не тягнути зайвого», мовчки змінив би поведінку контракту, і
     * перевірка на входження пропустила б його.
     */
    expect(calls[0]).toBe('/api/v1/documents/7/versions?periodKey=202601');
    expect(calls[1]).toBe('/api/v1/documents/7/compare?from=11&to=current');
  }, 60_000);
});

describe('Порівняння версій: що показано', () => {
  it('«показано не все» названо банером, коли truncated', async () => {
    mockApi(
      compareBody({
        truncated: true,
        changes: [
          { tableCode: 'T1', rowKey: 'r1', columnCode: 'C1', oldValue: '1', newValue: '2' },
        ],
      }),
    );
    await show();
    await runCompare();

    // ⛔ Мутація «`truncated` не показано» валить саме цей рядок.
    const banner = await screen.findByTestId('document-compare-truncated');
    expect(banner.textContent ?? '').toContain('Not everything is shown');
  }, 60_000);

  it('дзеркало: без усічення банера немає ЗОВСІМ', async () => {
    mockApi(
      compareBody({
        changes: [
          { tableCode: 'T1', rowKey: 'r1', columnCode: 'C1', oldValue: '1', newValue: '2' },
        ],
      }),
    );
    await show();
    await runCompare();

    await screen.findByTestId('document-compare-result');

    /*
     * ⚠ Без цього дзеркала попередній тест задовольняється банером, який
     * малюється ЗАВЖДИ, — тобто попередженням, що нічого не означає.
     */
    expect(screen.queryByTestId('document-compare-truncated')).toBeNull();
  }, 60_000);

  it('додані й видалені рядки — окремі блоки з позначкою, а не рядки таблиці змін', async () => {
    mockApi(
      compareBody({
        changes: [
          { tableCode: 'T1', rowKey: 'r1', columnCode: 'C1', oldValue: '1', newValue: '2' },
        ],
        addedRows: [{ rowId: 5, tableCode: 'T1', rowKey: 'rNew' }],
        removedRows: [{ rowId: 6, tableCode: 'T1', rowKey: 'rGone' }],
      }),
    );
    await show();
    await runCompare();

    const added = await screen.findByTestId('document-compare-added');
    const removed = screen.getByTestId('document-compare-removed');

    // ⛔ Позначки РІЗНІ. Мутація, що дасть обом однаковий підпис, валить це.
    expect(added.textContent ?? '').toContain('new');
    expect(added.textContent ?? '').toContain('rNew');
    expect(removed.textContent ?? '').toContain('removed');
    expect(removed.textContent ?? '').toContain('rGone');

    /*
     * ⛔ І головне: новий рядок НЕ став рядком таблиці змін. Саме ця мутація
     * («злити три переліки в один») виглядає як спрощення і робить із появи
     * рядка твердження про порожню комірку, якої не було.
     */
    expect(changeRows()).toEqual(['r1|C1|1|2']);
  }, 60_000);

  it('значення показано ЯК ПРИЙШЛО: «1.50» лишається «1.50»', async () => {
    mockApi(
      compareBody({
        changes: [
          { tableCode: 'T1', rowKey: 'r1', columnCode: 'C1', oldValue: '1.50', newValue: '2.500' },
          { tableCode: 'T1', rowKey: 'r2', columnCode: 'C1', oldValue: null, newValue: '3' },
        ],
      }),
    );
    await show();
    await runCompare();

    await screen.findByTestId('document-compare-changes');

    /*
     * ⛔ Мутація «форматувати число на клієнті» (`formatDecimal`, `Number(v)`,
     * обрізання хвостових нулів) валить саме це: `1.50` стало б `1.5`, тобто
     * екран показав би масштаб, якого в документі немає.
     */
    expect(changeRows()).toEqual(['r1|C1|1.50|2.500', 'r2|C1|—|3']);
  }, 60_000);
});

describe('Порівняння версій: порожнє і відмова — різні твердження (L10)', () => {
  it('порожня різниця — сказано словами, що версії однакові', async () => {
    mockApi(compareBody({}));
    await show();
    await runCompare();

    const same = await screen.findByTestId('document-compare-identical');
    expect(same.textContent ?? '').toContain('identical');

    // ⚠ І жодної порожньої таблиці поруч: «нічого» і «нічого не змінилося» —
    // різні екрани.
    expect(screen.queryByTestId('document-compare-result')).toBeNull();
  }, 60_000);

  it('відмова сервера — це НЕ «версії однакові»', async () => {
    mockApi({ status: 422, errorCode: 'ECR-DOC-0422' });
    await show();
    await runCompare();

    // Код відмови показаний — інакше звернення в підтримку перетворюється на
    // листування (`ErrorAlert`).
    expect(await screen.findByText('ECR-DOC-0422')).toBeTruthy();

    /*
     * ⛔ Головне твердження дзеркала: мутація «порожні переліки ⇒ однакові», яка
     * не розрізняє відсутність даних і звірену рівність, валить саме цей рядок.
     */
    expect(screen.queryByTestId('document-compare-identical')).toBeNull();
  }, 60_000);

  it('документ без жодного подання — сказано словами, а не порожнім списком', async () => {
    mockApi(compareBody({}), []);
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'Compare versions' }));

    const none = await screen.findByTestId('document-compare-no-versions');
    expect(none.textContent ?? '').toContain('never been submitted');

    // Полів вибору немає: обирати нема з чого.
    expect(screen.queryByLabelText('From version')).toBeNull();
  }, 60_000);
});
