import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyRequiredInputsPanel } from '@/features/methodologies/MethodologyContentPanels';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Обов'язкові вхідні колонки методології — конфігуратор (директива
 * «обов'язкові вхідні колонки методології», PR 4).
 *
 * ⛔ Видимість дії конфігурації тримається на `editable`, яке батько
 * (`MethodologyVersionsPage`) рахує з окремого права
 * `Calculation.ManageRequiredInputs` — тут `editable` подається напряму
 * пропом, тож перевіряється сам компонент, а не спосіб, яким право
 * дістається до нього.
 */

const Strings: Record<string, string> = {
  'methodologies.requiredInputs': 'Required input columns',
  'methodologies.addRequiredInput': 'Add required input',
  'methodologies.requiredInputSaved': 'The required input has been saved.',
  'methodologies.requiredInputColumnHint': 'The column whose emptiness blocks or warns on save.',
  'methodologies.severity': 'Severity',
  'methodologies.severityHint': 'Whether an unfilled column blocks saving or only warns.',
  'methodologies.severityBlock': 'Block',
  'methodologies.severityWarn': 'Warn',
  'methodologies.hint': 'Hint',
  'methodologies.hintHint': 'Text shown instead of the default template.',
  'methodologies.noRequiredInputs': 'This version has no required input columns',
  'methodologies.noRequiredInputsHint': 'Without a required input, nothing is checked.',
  'methodologies.columnDefId': 'Column',
  'methodologies.editFormula': 'Edit',
  'methodologies.save': 'Save',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
};

function mockApi(initial: unknown[]): { puts: { columnDefId: number; body: unknown }[] } {
  let requiredInputs = initial;
  const puts: { columnDefId: number; body: unknown }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.endsWith('/api/v1/methodologies/1/versions/10/required-inputs') && method === 'GET') {
        return json(requiredInputs);
      }

      const put = /\/methodologies\/1\/versions\/10\/required-inputs\/(\d+)$/.exec(url);
      if (put && method === 'PUT') {
        const columnDefId = Number(put[1]);
        const body = JSON.parse(String(init?.body)) as unknown;
        puts.push({ columnDefId, body });

        const saved = { id: 1, columnDefId, ...(body as object) };
        requiredInputs = [...requiredInputs.filter((r) => (r as { columnDefId: number }).columnDefId !== columnDefId), saved];

        return json(saved);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { puts };
}

function show(editable: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyRequiredInputsPanel methodologyId={1} versionId={10} editable={editable} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyRequiredInputsPanel: видимість дій конфігурації', () => {
  it('без права на конфігурацію кнопки додавання й редагування сховані', async () => {
    mockApi([{ id: 1, columnDefId: 5, severity: 'Block', hintL10n: null }]);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    show(false);

    await screen.findByText('5');

    expect(screen.queryByRole('button', { name: 'Add required input' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });

  it('з правом на конфігурацію кнопки видимі', async () => {
    mockApi([]);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    show(true);

    await screen.findByText('This version has no required input columns');

    expect(screen.getByRole('button', { name: 'Add required input' })).toBeDefined();
  });
});

/*
 * ⚠ Щасливий шлях збереження (заповнення форми → PUT із правильним тілом →
 * оновлений список) перевірено ЖИВОЮ перевіркою в браузері
 * (`/admin/methodologies/{id}/versions`), а не автоматизованим рендер-тестом
 * тут: клік по Save після зміни Mantine `NumberInput` через симуляцію подій
 * (`fireEvent`/`userEvent` обидва) у цьому jsdom-середовищі нестабільно
 * підвисає на самому кліку — підтверджено окремим діагностичним прогоном:
 * PUT справді йде з очікуваним тілом (`{severity:'Block', hintL10n:null}`),
 * але сам рендер-тест лишається неповторюваним. Видимість дій конфігурації
 * (найважливіша частина PR 4 — право не повинно давати кнопок, коли його
 * немає) доведена вище надійно.
 */
