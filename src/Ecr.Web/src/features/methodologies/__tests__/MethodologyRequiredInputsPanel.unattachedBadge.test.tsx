import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 5 (`Q-337`): коли прив'язку (`CalculationBinding`)
 * деактивують, панель Required Input Columns і далі показувала
 * `Column: X, Severity: Block` БЕЗ жодного попередження, що ця колонка
 * більше не має активної прив'язки для цієї методології — вимога
 * лишалась «завислою» конфігурацією, що й далі блокувала б збереження
 * даних для колонки, яку методологія вже не пише.
 *
 * ⛔ Мутаційний доказ: сервер тепер повертає `hasActiveBinding` для кожної
 * вимоги (`ListMethodologyRequiredInputsHandler`), і панель показує бейдж
 * «unattached» лише коли це `false`. Без обчислення бейджа за цим полем
 * (напр. якщо хтось поверне його на статичне `false` чи `true`) один з двох
 * тестів нижче впаде.
 */
const Strings: Record<string, string> = {
  'methodologies.requiredInputs': 'Required input columns',
  'methodologies.addRequiredInput': 'Add required input',
  'methodologies.columnDefId': 'Column',
  'methodologies.severity': 'Severity',
  'methodologies.severityBlock': 'Block',
  'methodologies.severityWarn': 'Warn',
  'methodologies.hint': 'Hint',
  'methodologies.editFormula': 'Edit',
  'methodologies.noRequiredInputs': 'This version has no required input columns',
  'methodologies.noRequiredInputsHint': 'Without a required input, nothing is checked.',
  'methodologies.requiredInputUnattached': 'unattached',
  'methodologies.requiredInputUnattachedHint':
    'This column has no active binding for this methodology right now.',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
};

function mockApi(requiredInputs: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.endsWith('/api/v1/methodologies/1/versions/10/required-inputs')) {
        return json(requiredInputs);
      }

      if (url.includes('/api/v1/column-defs/search')) {
        return json([]);
      }

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

describe('MethodologyRequiredInputsPanel: позначка "завислої" вимоги (Q-337, lane5)', () => {
  it('колонка з активною прив\'язкою — без бейджа "unattached"', async () => {
    mockApi([
      { id: 1, columnDefId: 42, severity: 'Block', hintL10n: null, hasActiveBinding: true },
    ]);
    await show();

    await screen.findByText('42');
    expect(screen.queryByText('unattached')).toBeNull();
  });

  it('колонка БЕЗ активної прив\'язки (деактивовано чи ще не заведено) — бейдж "unattached"', async () => {
    mockApi([
      { id: 1, columnDefId: 42, severity: 'Block', hintL10n: null, hasActiveBinding: false },
    ]);
    await show();

    await screen.findByText('42');
    await waitFor(() => {
      expect(screen.getByText('unattached')).toBeDefined();
    });
  });

  /**
   * Пояснення бейджа було в Mantine `Tooltip` — лише під мишею. Бейдж не
   * фокусований, тож з клавіатури пояснення не було видно НІКОЛИ, а читач не
   * отримував опису.
   *
   * ⛔ Мутаційний доказ: повернути `Tooltip` у `MethodologyContentPanels.tsx`
   * — тест червоний (Tab не доходить до бейджа, немає опису, фокус не дає
   * `role="tooltip"`).
   */
  it('підказка бейджа "unattached" доступна з фокуса: Tab, опис, role="tooltip"', async () => {
    mockApi([
      { id: 1, columnDefId: 42, severity: 'Block', hintL10n: null, hasActiveBinding: false },
    ]);
    await show();

    const hint = 'This column has no active binding for this methodology right now.';
    const badge = (await screen.findByText('unattached')).closest('[aria-describedby]');

    expect(badge).not.toBeNull();
    expect(screen.getAllByRole('generic', { description: hint })).toContain(badge);

    // З клавіатури: Tab доводить до бейджа, а не лише програмний `focus()`.
    const user = userEvent.setup();
    for (let step = 0; step < 20 && document.activeElement !== badge; step += 1) {
      await user.tab();
    }
    expect(document.activeElement).toBe(badge);

    expect((await screen.findByRole('tooltip')).textContent).toBe(hint);
  });
});
