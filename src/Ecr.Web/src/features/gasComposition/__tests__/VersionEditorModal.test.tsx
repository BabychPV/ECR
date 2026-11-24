import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, configure, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import type { GasComponent, GasVersion } from '../api';
import { VersionEditorModal, type VersionEditorTarget } from '../VersionEditorModal';

/**
 * Редактор версії складу газу (D-NEW): жива Σ, підтвердження Σ поза 100 ± 0.5, колонка «Попередня» з позначкою змін і
 * значення `decimal` рядками повної точності — незмінене значення FLERT іде на сервер тим самим рядком.
 *
 * ⛔ Доказ: прибрати `(!off || accept)` з `canSave` → «Σ поза допуском…» червоний (кнопка доступна без підтвердження);
 * повернути `Number(...)` у `parse` → «незмінені значення…» червоний (`0.0169820827685202` → `0.0169820827685202` лише
 * випадково; `97.1234567890123456` округлюється до `97.12345678901235`).
 */
configure({ asyncUtilTimeout: 10_000 });

const Components: GasComponent[] = [
  { code: 'Methane', name: 'Methane', formula: 'CH4', mw: '16.043', group: 'hydrocarbon' },
  { code: 'Ethane', name: 'Ethane', formula: 'C2H6', mw: '30.07', group: 'hydrocarbon' },
  { code: 'H2S', name: 'H2S', formula: null, mw: null, group: 'sulphur' },
];

const version = (id: number, from: string, to: string | null, components: Array<[string, string]>): GasVersion => ({
  id,
  validFrom: from,
  validTo: to,
  temperatureC: '20',
  densityOp: null,
  mwHysys: null,
  z: null,
  ruleParse: null,
  legacyTypeId: null,
  legacyCompositionId: null,
  flertAliases: null,
  sum: String(components.reduce((s, [, v]) => s + Number(v), 0)),
  noComposition: components.length === 0,
  components: components.map(([code, molPct]) => ({ code, molPct })),
  locks: [],
});

beforeAll(async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: Catalog }))));
  await loadCatalog('en', 'private');
  vi.unstubAllGlobals();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function show(target: VersionEditorTarget, onSaved = vi.fn()): ReturnType<typeof vi.fn> {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient()}>
        <VersionEditorModal streamId={5} target={target} components={Components} onClose={vi.fn()} onSaved={onSaved} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return onSaved;
}

const input = (name: string): HTMLInputElement =>
  screen.getByRole('textbox', { name: `${name}, ${Catalog['gasComposition.editor.value']!}` });

const saveButton = (): HTMLButtonElement => screen.getByRole('button', { name: Catalog['gasComposition.editor.save']! });

describe('D-NEW: редактор версії складу газу', () => {
  it('Σ поза допуском блокує збереження до підтвердження, а підтвердження шле acceptSumDeviation', async () => {
    const calls: unknown[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) => {
        calls.push(JSON.parse(String(init?.body)));
        return new Response(JSON.stringify({ stream: null, queuedYears: [], failedYears: [] }), {
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );
    const previous = version(1, '2022-01-01', null, [['Methane', '97'], ['Ethane', '3']]);
    const onSaved = show({ kind: 'new', caseName: '370 Winter', copyFrom: previous });

    expect(screen.getByTestId('gas-sum').textContent).toContain('100');
    expect(saveButton().disabled).toBe(false);

    fireEvent.change(input('Methane'), { target: { value: '90' } });
    expect(screen.getByTestId('gas-sum').textContent).toContain('93');
    expect(saveButton().disabled).toBe(true);

    fireEvent.click(screen.getByRole('checkbox', { name: /93/ }));
    expect(saveButton().disabled).toBe(false);
    fireEvent.click(saveButton());

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(calls[0]).toMatchObject({ caseName: '370 Winter', acceptSumDeviation: true });
    expect((calls[0] as { components: unknown[] }).components).toEqual([
      { code: 'Methane', molPct: '90' },
      { code: 'Ethane', molPct: '3' },
    ]);
  });

  it('колонка «Попередня» і позначка зміненого компонента', () => {
    const previous = version(1, '2022-01-01', '2026-07-01', [['Methane', '97'], ['Ethane', '3']]);
    const current = version(2, '2026-07-01', null, [['Methane', '99'], ['H2S', '1']]);
    show({ kind: 'edit', caseName: '370 Winter', version: current, previous, readOnly: false });

    expect(screen.getAllByRole('columnheader', { name: Catalog['gasComposition.editor.previous']! }).length).toBeGreaterThan(0);
    const changed = document.querySelectorAll('[data-changed]');
    // Methane 97 → 99, Ethane 3 → 0, H2S 0 → 1.
    expect(changed).toHaveLength(3);
  });

  it('незмінені значення повної точності йдуть на сервер тим самим рядком', async () => {
    const calls: unknown[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) => {
        calls.push(JSON.parse(String(init?.body)));
        return new Response(JSON.stringify({ stream: null, queuedYears: [], failedYears: [] }), {
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );
    const exact = version(7, '2022-01-01', null, [['Methane', '97.1234567890123456'], ['Ethane', '2.8765432109876544']]);
    const onSaved = show({ kind: 'edit', caseName: 'SOR', version: exact, previous: undefined, readOnly: false });

    fireEvent.change(screen.getByRole('textbox', { name: Catalog['gasComposition.editor.temperature']! }), { target: { value: '21' } });
    fireEvent.click(saveButton());

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect((calls[0] as { components: unknown[] }).components).toEqual([
      { code: 'Methane', molPct: '97.1234567890123456' },
      { code: 'Ethane', molPct: '2.8765432109876544' },
    ]);
    expect(calls[0]).toMatchObject({ temperatureC: '21', validFrom: '2022-01-01' });
  });

  it('замкнена версія — лише перегляд: поля незмінні, кнопки збереження немає', () => {
    const locked = { ...version(3, '2025-01-01', null, [['Methane', '100']]), locks: [{ period: '2025-03', reason: 'approved' }] };
    show({ kind: 'edit', caseName: 'SOR', version: locked, previous: undefined, readOnly: true });

    expect(screen.getByTestId('gas-version-locked').textContent).toContain('2025-03');
    expect(input('Methane').readOnly).toBe(true);
    expect(screen.queryByRole('button', { name: Catalog['gasComposition.editor.save']! })).toBeNull();
  });
});
