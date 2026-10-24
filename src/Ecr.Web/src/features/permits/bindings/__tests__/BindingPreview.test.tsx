import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EcrApiError } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import * as bindingsApi from '../../api/bindingsApi';
import * as permitsApi from '../../api/permitsApi';
import { BindingPreview, PreviewRowLimit } from '../BindingPreview';
import { binding, k, kp, preview } from './fixtures';

/**
 * Перегляд прив'язки (PM-11): дозвіл і причини його відсутності, попередження, штрихування, 403.
 *
 * Мутаційний доказ (виконано, відновлено): у `BindingPreview` прибрати `forbidden ?` гілку (показувати
 * `ErrorAlert` і на 403) валить «403»; у `PreviewBody` умову `permit.reasonIfNone !== null` замінити на `false`
 * валить «причини»; прибрати клас `is-locked` на комірці валить «штрихування».
 */
vi.mock('@/features/permits/api/bindingsApi', () => ({ previewPermitBinding: vi.fn() }));
vi.mock('@/features/permits/api/permitsApi', () => ({ listPermits: vi.fn() }));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const problem = (status: number): EcrApiError =>
  new EcrApiError({ title: 'x', status, errorCode: 'ECR-AUTH-0403', correlationId: 'c' });

describe('BindingPreview', () => {
  it('показує дозвіл, рядки й заштриховані комірки, які пише машина', async () => {
    vi.mocked(bindingsApi.previewPermitBinding).mockResolvedValue(preview());
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding()} />);

    expect(await screen.findByTestId('binding-preview-permit')).toBeDefined();
    expect(screen.getByText(kp('preview.permitLine', { number: 'KZ-001', from: '2026-01-01', to: '2027-01-01' }))).toBeDefined();

    const boiler = screen.getByText('Boiler').closest('td');
    expect(boiler?.className).toContain('is-locked');
    // «Actual» вводять люди: не штрихована.
    const actual = within(boiler?.closest('tr') as HTMLElement).getAllByRole('cell')[2];
    expect(actual?.className ?? '').not.toContain('is-locked');
  });

  it.each([
    ['noFacility', 'preview.noFacility'],
    ['noPermit', 'preview.noPermit'],
    ['headerEmpty', 'preview.headerEmpty'],
    ['headerUnknown', 'preview.headerUnknown'],
    ['somethingNew', 'preview.noneOther'],
  ])('причина %s показується локалізованим повідомленням, а не помилкою', async (reason, key) => {
    vi.mocked(bindingsApi.previewPermitBinding).mockResolvedValue(
      preview({ permit: { entryId: null, number: null, reasonIfNone: reason, validFrom: null, validTo: null }, rows: [] }),
    );
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding()} />);

    expect((await screen.findByTestId('binding-preview-reason')).textContent).toContain(k(key));
    expect(screen.queryByTestId('binding-preview-permit')).toBeNull();
  });

  it('попередження виводяться за випадком, невідоме — загальним текстом', async () => {
    vi.mocked(bindingsApi.previewPermitBinding).mockResolvedValue(
      preview({ warnings: [{ code: 'binding.warning.inactive', text: 'x' }, { code: 'new.thing', text: 'y' }] }),
    );
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding()} />);

    const list = await screen.findByTestId('binding-preview-warnings');
    expect(list.textContent).toContain(k('warning.inactive'));
    expect(list.textContent).toContain(k('warning.other'));
  });

  it('403 — спокійний рядок «немає доступу», а не помилка з кнопкою повтору', async () => {
    vi.mocked(bindingsApi.previewPermitBinding).mockRejectedValue(problem(403));
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding()} />);

    expect(await screen.findByTestId('binding-preview-forbidden')).toBeDefined();
    // Банер `info` — `role="status"`; `ErrorAlert` був би `role="alert"`.
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('інша помилка — ErrorAlert', async () => {
    vi.mocked(bindingsApi.previewPermitBinding).mockRejectedValue(problem(500));
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding()} />);

    await waitFor(() => expect(screen.queryByTestId('binding-preview-forbidden')).toBeNull());
    expect(await screen.findByRole('alert')).toBeDefined();
  });

  it('«Update preview» надсилає об’єкт і кінець періоду; порожній об’єкт не надсилається', async () => {
    const user = userEvent.setup();
    vi.mocked(bindingsApi.previewPermitBinding).mockResolvedValue(preview());
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding()} />);
    await screen.findByTestId('binding-preview-permit');

    expect(bindingsApi.previewPermitBinding).toHaveBeenLastCalledWith(7, 'PB41_Emissions', {});

    await user.type(screen.getByLabelText(k('preview.facility')), '  Offshore complex ');
    await user.click(screen.getByRole('button', { name: k('preview.update') }));

    await waitFor(() =>
      expect(bindingsApi.previewPermitBinding).toHaveBeenLastCalledWith(
        7,
        'PB41_Emissions',
        expect.objectContaining({ facilityName: 'Offshore complex', periodEnd: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) as unknown }),
      ),
    );
  });

  it('FromHeaderLookup: замість об’єкта — вибір дозволу, у запит іде permitEntryId', async () => {
    const user = userEvent.setup();
    vi.mocked(bindingsApi.previewPermitBinding).mockResolvedValue(preview());
    vi.mocked(permitsApi.listPermits).mockResolvedValue({
      items: [{ id: 9, number: 'KZ-001', location: 'Offshore complex' }],
    } as never);
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding({ selectionRule: 'FromHeaderLookup', headerFieldDefId: 3 })} />);
    await screen.findByTestId('binding-preview-permit');

    expect(screen.queryByLabelText(k('preview.facility'))).toBeNull();
    expect(permitsApi.listPermits).toHaveBeenCalledWith({ type: 'AirPermit' });

    await user.click(screen.getByRole('textbox', { name: k('preview.permit') }));
    await user.click(await screen.findByRole('option', { name: 'KZ-001 · Offshore complex' }));
    await user.click(screen.getByRole('button', { name: k('preview.update') }));

    await waitFor(() => expect(bindingsApi.previewPermitBinding).toHaveBeenLastCalledWith(7, 'PB41_Emissions', { permitEntryId: 9 }));
  });

  it('довгий перелік обрізається лічильником «показано N із M»', async () => {
    const rows = Array.from({ length: PreviewRowLimit + 3 }, (_, index) => ({
      rowKey: `P${String(index)}`,
      cells: { Source: `S${String(index)}`, Limit: index, Actual: null },
      locked: ['Source', 'Limit'],
    }));
    vi.mocked(bindingsApi.previewPermitBinding).mockResolvedValue(preview({ rows }));
    renderWithQuery(<BindingPreview templateVersionId={7} binding={binding()} />);

    expect(await screen.findByText(kp('preview.more', { shown: PreviewRowLimit, total: PreviewRowLimit + 3 }))).toBeDefined();
    expect(screen.queryByText(`S${String(PreviewRowLimit)}`)).toBeNull();
  });
});
