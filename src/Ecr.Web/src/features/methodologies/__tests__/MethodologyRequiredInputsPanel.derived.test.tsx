import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * ✎ D-578 (рішення людини 26.11): редактор методології показує ВИВЕДЕНІ обов'язкові входи — поля, які читає скрипт
 * версії, — лише для читання, з винятком методолога (Optional/Warn/Block), а без автоматичного переліку — попередження.
 *
 * ⛔ Мутаційний доказ: прибрати `<DerivedInputsTable …/>` з `MethodologyRequiredInputsPanel` — обидва тести червоні;
 * у `DerivedInputsTable` прибрати гілку `status !== 'available'` — червоний другий.
 */
const Strings: Record<string, string> = {
  'methodologies.requiredInputs': 'Required input columns',
  'methodologies.noRequiredInputs': 'This version has no required input columns',
  'methodologies.noRequiredInputsHint': 'Without a required input, nothing is checked.',
  'methodologies.severityBlock': 'Block',
  'methodologies.severityWarn': 'Warn',
  'methodologies.severityOptional': 'Optional (may be empty)',
  'methodologies.derivedInputs': 'Read by the script (required automatically)',
  'methodologies.derivedInputsHint': 'Every field the script reads is required to submit.',
  'methodologies.derivedUnavailable': 'The automatic list is not available for this version.',
  'methodologies.derivedField': 'Field',
  'methodologies.derivedRequirement': 'Requirement',
  'methodologies.derivedRequired': 'Required to submit',
  'methodologies.derivedOtherMonths': 'Reads another month — not required yet',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
};

function mockApi(derived: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.endsWith('/api/v1/methodologies/1/versions/10/required-inputs')) return json([]);
      if (url.endsWith('/api/v1/methodologies/1/versions/10/derived-inputs')) return json(derived);
      throw new Error(`Немає мока для ${url}`);
    }),
  );
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { MethodologyRequiredInputsPanel } = await import('../MethodologyContentPanels');
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyRequiredInputsPanel methodologyId={1} versionId={10} editable={false} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyRequiredInputsPanel: виведені входи (D-578)', () => {
  it('поля скрипта — обов\'язкові для подання, виняток методолога й інший місяць підписані', async () => {
    mockApi({
      methodologyVersionId: 10,
      status: 'available',
      fields: [
        { fieldCode: 'Fuel', otherMonths: false, inScope: true, exceptionSeverity: null },
        { fieldCode: 'Gas', otherMonths: false, inScope: true, exceptionSeverity: 'Optional' },
        { fieldCode: 'Prev', otherMonths: true, inScope: false, exceptionSeverity: null },
      ],
    });
    await show();

    expect(await screen.findByText('Fuel')).toBeDefined();
    expect(screen.getByText('Required to submit')).toBeDefined();
    expect(screen.getByText('Optional (may be empty)')).toBeDefined();
    expect(screen.getByText('Reads another month — not required yet')).toBeDefined();
    expect(screen.queryByTestId('methodology-derived-unavailable')).toBeNull();
  });

  it('скрипт не скомпільовано — попередження замість переліку', async () => {
    mockApi({ methodologyVersionId: 10, status: 'notCompiled', fields: [] });
    await show();

    expect(await screen.findByTestId('methodology-derived-unavailable')).toBeDefined();
    expect(screen.getByText('The automatic list is not available for this version.')).toBeDefined();
  });
});
