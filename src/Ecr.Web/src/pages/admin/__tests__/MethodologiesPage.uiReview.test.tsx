import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { methodologyMatches, methodologyTitle, versionModes } from '@/features/methodologies/versionModeLabels';
import { testTheme } from '@/test/render';

/**
 * ✎ DOCUX (огляд UI 27.11): перелік методологій — «ECW_C05_01 (ECW_C05_01)», сирі режими «LEGACY · ERRORSONLY», 100+
 * рядків без пошуку. Тепер код один раз, режими словами з поясненням, пошук за кодом і назвою (значення в адресі).
 */
function version(id: number): Record<string, unknown> {
  return {
    id,
    versionNumber: 1,
    engine: 'Script',
    level: 'Configuration',
    status: 'Published',
    effectiveFrom: null,
    numericMode: 'Legacy',
    calendarMode: 'Actual',
    traceLevel: 'ErrorsOnly',
  };
}

const methodologies = [
  { id: 1, code: 'ECW_C05_01', nameL10n: { values: { en: 'ECW_C05_01' } }, versions: [version(10)] },
  { id: 2, code: 'ECW_GAS', nameL10n: { values: { en: 'Gas flaring' } }, versions: [version(20)] },
];

const json = (body: unknown): Response => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/methodologies')) return json(methodologies);
      return json(null);
    }),
  );
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MethodologiesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('versionModeLabels', () => {
  it('режими словами й пояснення; невідоме значення — як є', () => {
    expect(versionModes({ numericMode: 'Legacy', traceLevel: 'ErrorsOnly' })).toEqual({
      label: '⟦methodologies.mode.numeric.Legacy⟧ · ⟦methodologies.mode.trace.ErrorsOnly⟧',
      hint: '⟦methodologies.mode.numeric.LegacyHint⟧ ⟦methodologies.mode.trace.ErrorsOnlyHint⟧',
    });
    expect(versionModes({ numericMode: 'Strict', traceLevel: 'Full', calendarMode: 'Fixed360' }).label).toBe(
      '⟦methodologies.mode.numeric.Strict⟧ · ⟦methodologies.mode.calendar.Fixed360⟧ · ⟦methodologies.mode.trace.Full⟧',
    );
    expect(versionModes({ numericMode: 'Future', traceLevel: 'Off' }).label).toBe('Future · ⟦methodologies.mode.trace.Off⟧');
  });

  it('назва = код (чи порожня) — код один раз; пошук за кодом і назвою без регістру', () => {
    expect(methodologyTitle('ECW_C05_01', 'ECW_C05_01')).toEqual({ name: 'ECW_C05_01', code: null });
    expect(methodologyTitle(' ', 'ECW_X')).toEqual({ name: 'ECW_X', code: null });
    expect(methodologyTitle('Gas flaring', 'ECW_GAS')).toEqual({ name: 'Gas flaring', code: 'ECW_GAS' });
    expect(methodologyMatches('c05', 'ECW_C05_01', 'ECW_C05_01')).toBe(true);
    expect(methodologyMatches('FLAR', 'Gas flaring', 'ECW_GAS')).toBe(true);
    expect(methodologyMatches('coal', 'Gas flaring', 'ECW_GAS')).toBe(false);
  });
});

describe('MethodologiesPage: огляд UI', () => {
  it('код один раз, режими словами, пошук звужує перелік', async () => {
    show();
    const first = await screen.findByText('ECW_C05_01');
    expect(first.closest('tr')?.textContent).not.toContain('(ECW_C05_01)');
    expect(screen.getByText('(ECW_GAS)')).toBeTruthy();
    const modes = document.querySelector('[data-version-modes]');
    expect(modes?.textContent).toBe('⟦methodologies.mode.numeric.Legacy⟧ · ⟦methodologies.mode.trace.ErrorsOnly⟧');
    expect(document.body.textContent).not.toMatch(/ErrorsOnly(?!⟧|Hint)/);

    fireEvent.change(screen.getByLabelText('⟦methodologies.search⟧'), { target: { value: 'flaring' } });
    await waitFor(() => expect(document.querySelectorAll('[data-methodology-row]')).toHaveLength(1));
    expect(document.querySelector('[data-methodology-row]')?.getAttribute('data-methodology-row')).toBe('ECW_GAS');

    fireEvent.change(screen.getByLabelText('⟦methodologies.search⟧'), { target: { value: 'nothing-like-this' } });
    await waitFor(() => expect(document.querySelector('[data-methodologies-no-match]')).not.toBeNull());
  });
});
