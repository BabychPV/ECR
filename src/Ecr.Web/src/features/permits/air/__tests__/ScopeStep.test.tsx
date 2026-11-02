import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import type { AirScopeFields } from '../airTypes';
import { EmptyDraft, type ImportDraft } from '../import/importModel';
import { ScopeStep } from '../import/ScopeStep';
import { areaChoice, scopeFormFields, scopeProblem } from '../import/scopeModel';
import { PreviewStep } from '../import/PreviewStep';
import { preview, scopeCodes, scopeFields } from './airFixtures';

/**
 * Крок «Region and area» майстра «Import parameters…» (AIR-ON-AIR): поле за видом колонки типу AIR (Registry — пікер довідника,
 * List — вибір значення, Text — поле вводу), Area лише для регіону з ділянками й лише його ділянки, попередження про відсутній
 * довідник і про регіон поза деревом НАЕ (не блокують). Тексти — справжні, з сіду.
 *
 * Мутаційні докази (вручну, відновлено): `areaChoice` без фільтра за `naeArea` валить «Area - лише ділянки обраного регіону»;
 * `scopeFormFields` шле `region` замість `regionEntryId` для Registry - валить «поля форми за видом».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

const text = (key: string): string => Catalog[key] ?? key;
afterEach(cleanup);

function mount(fields: AirScopeFields, draft: Partial<ImportDraft> = {}, onChange = vi.fn()): ReturnType<typeof vi.fn> {
  render(
    <TestProviders>
      <ScopeStep
        permitNumber="KZ87"
        fields={fields}
        codes={scopeCodes()}
        failure={null}
        onRetry={() => undefined}
        draft={{ ...EmptyDraft, ...draft }}
        onChange={onChange}
        problem={null}
      />
    </TestProviders>,
  );

  return onChange;
}

const regionBox = (): HTMLElement => screen.getByRole('textbox', { name: new RegExp(`^${text('permits.air.import.region.label')}`) });
const areaBox = (): HTMLElement | null => screen.queryByRole('textbox', { name: new RegExp(`^${text('permits.air.import.area.label')}`) });

describe('Air params: крок «Region and area» за видом колонки', () => {
  it('Registry: пікер довідника з підписом «назва (код)», значення - id запису', async () => {
    const onChange = mount(scopeFields('Registry', 'Registry'));
    expect(screen.getByDisplayValue('KZ87')).toBeTruthy();
    await userEvent.click(regionBox());
    expect(screen.getByRole('option', { name: 'Atyrau (REG_001)' })).toBeTruthy();
    await userEvent.click(screen.getByRole('option', { name: 'Mangistau (REG_002)' }));
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ region: '12', area: '' }));
  });

  it('List: вибір значення типу, значення - текст вибору', async () => {
    const onChange = mount(scopeFields('List', 'List'));
    await userEvent.click(regionBox());
    expect(screen.getByRole('option', { name: 'Atyrau' })).toBeTruthy();
    await userEvent.click(screen.getByRole('option', { name: 'Atyrau' }));
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ region: 'Atyrau' }));
  });

  it('Text: поле вводу без списку; Area теж текстом, коли регіон уведено', () => {
    const onChange = mount(scopeFields('Text', 'Text'), { region: 'Atyrau' });
    fireEvent.change(regionBox(), { target: { value: 'Atyrau oblast' } });
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ region: 'Atyrau oblast' }));
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(areaBox()).not.toBeNull();
  });

  it('Area - лише для регіону з ділянками і лише ділянки цього регіону', async () => {
    mount(scopeFields(), { region: '12' }); // Mangistau: у дереві НАЕ без ділянок
    expect(areaBox()).toBeNull();
    cleanup();
    mount(scopeFields(), { region: '11' }); // Atyrau: OFFSHORE, ONSHORE
    const area = areaBox();
    if (area === null) throw new Error('area');
    await userEvent.click(area);
    expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual(['Offshore complex (AREA_OFF)', 'Onshore complex (AREA_ON)']);
  });

  it('регіон поза деревом НАЕ - попередження, не блок', () => {
    mount(scopeFields(), { region: '13' });
    expect(screen.getByText(text('permits.air.import.scope.notInTreeTitle'))).toBeTruthy();
    expect(scopeProblem(scopeFields(), scopeCodes(), '13', '')).toBeNull();
  });

  it('довідника немає - попередження з кодом довідника і колонки; обов\'язковий Region блокує', () => {
    mount(scopeFields('Registry', 'Registry', { regionFound: false }));
    expect(screen.getByText(text('permits.air.import.scope.empty'))).toBeTruthy();
    expect(screen.getByText(/LAND_REGION/)).toBeTruthy();
    expect(scopeProblem(scopeFields('Registry', 'Registry', { regionFound: false }), scopeCodes(), '', '')).toBe('region');
  });

  it('обов\'язкова Area блокує лише коли показана', () => {
    const fields = scopeFields('Registry', 'Registry', { areaRequired: true });
    expect(scopeProblem(fields, scopeCodes(), '11', '')).toBe('area');
    expect(scopeProblem(fields, scopeCodes(), '12', '')).toBeNull();
    expect(areaChoice(fields, scopeCodes(), '12').show).toBe(false);
  });

  it('поля форми за видом: Registry - *EntryId, List/Text - region/area; прихована Area не їде', () => {
    expect(scopeFormFields(scopeFields(), scopeCodes(), '11', '21')).toEqual({ regionEntryId: '11', areaEntryId: '21' });
    expect(scopeFormFields(scopeFields(), scopeCodes(), '12', '21')).toEqual({ regionEntryId: '12' });
    expect(scopeFormFields(scopeFields('List', 'List'), scopeCodes(), 'Atyrau', 'Offshore complex')).toEqual({ region: 'Atyrau', area: 'Offshore complex' });
    expect(scopeFormFields(scopeFields('Text', 'Text'), scopeCodes(), ' Atyrau ', '')).toEqual({ region: 'Atyrau' });
  });

  it('перегляд із scopeInNaeTree = false показує попередження', () => {
    const noop = (): void => undefined;
    render(
      <TestProviders>
        <QueryClientProvider client={new QueryClient()}>
          <PreviewStep
            preview={{ ...preview({ missing: [] }), scopeInNaeTree: false }}
            draft={EmptyDraft}
            onDraft={noop}
            stale={false}
            onRerun={noop}
            rerunning={false}
            onApply={noop}
            applying={false}
            mayApply
          />
        </QueryClientProvider>
      </TestProviders>,
    );
    expect(screen.getByText(text('permits.air.import.scope.notInTreeTitle'))).toBeTruthy();
  });
});
