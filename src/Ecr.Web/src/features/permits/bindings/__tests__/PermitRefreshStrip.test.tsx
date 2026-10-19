import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import type { DocumentTableDto } from '@/api/types';
import { invalidateSlices } from '@/features/grid/sliceCache';
import { TestProviders } from '@/test/render';
import { showApiError } from '@/shared/ui/notify';
import * as runtimeApi from '../../api/bindingsRuntimeApi';
import { PermitRefreshStrip } from '../PermitRefreshStrip';
import { k, kp } from './fixtures';

/**
 * «З дозволу» + «Оновити з дозволу» у документі (PM-11, PM-11r2): прив'язку несе `DocumentTableDto.permitBinding`,
 * додаткових запитів немає. Показ, права, підсумок, скидання зрізів аркуша, відмови.
 *
 * Мутаційний доказ (виконано, відновлено): прибрати `disabled={readOnly}` валить «лише для читання»; прибрати
 * перевірку `mode === RowsMode` валить «Formulas»; замінити `invalidateSlices` на порожній виклик валить «скидає
 * зрізи аркуша»; прибрати `showApiError` валить «відмова»; `keptKeys.length > 0` → `false` у тоні валить
 * «збережені рядки».
 */
vi.mock('../../api/bindingsRuntimeApi', () => ({ refreshPermitBinding: vi.fn() }));
vi.mock('@/features/grid/sliceCache', () => ({ invalidateSlices: vi.fn().mockResolvedValue(undefined) }));
vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showApiError: vi.fn() }));

const tableDto = (over: Partial<DocumentTableDto> = {}): DocumentTableDto =>
  ({
    allowsDynamicRows: true,
    maxDynamicRows: 200,
    sheetCode: 'Air',
    sheetDefId: 11,
    sheetNameL10n: { values: { en: 'Air' } },
    sheetOrdinal: 1,
    tableCode: 'Emissions',
    tableDefId: 41,
    tableInstanceId: 900,
    tableNameL10n: { values: { en: 'Emission sources' } },
    tableOrdinal: 1,
    permitBinding: { code: 'PB41_Emissions', mode: 'Rows', permitTypeCode: 'AirPermit' },
    ...over,
  }) as DocumentTableDto;

let client: QueryClient;
const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
  <TestProviders>
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  </TestProviders>
);

function mount(props: Partial<Parameters<typeof PermitRefreshStrip>[0]> = {}) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(<PermitRefreshStrip documentId={55} periodKey={202607} tables={[tableDto()]} readOnly={false} {...props} />, { wrapper });
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('PermitRefreshStrip — показ', () => {
  it('таблиця з permitBinding Rows: позначка «З дозволу», назва таблиці й кнопка', () => {
    mount();

    expect(screen.getByTestId('from-permit-badge')).toBeDefined();
    expect(screen.getByText(kp('doc.tableLine', { name: 'Emission sources' }))).toBeDefined();
    expect(screen.getByRole('button', { name: k('doc.refresh') })).toBeDefined();
  });

  it('Formulas, null і відсутній permitBinding смуги не дають', () => {
    const absent = tableDto({ tableInstanceId: 902 });
    delete absent.permitBinding;
    mount({
      tables: [
        tableDto({ permitBinding: { code: 'PB1', mode: 'Formulas', permitTypeCode: 'AirPermit' } }),
        tableDto({ tableInstanceId: 901, permitBinding: null }),
        absent,
      ],
    });

    expect(screen.queryByTestId('permit-refresh-strip')).toBeNull();
  });

  it('кожна прив’язана таблиця аркуша має свою кнопку', () => {
    mount({
      tables: [
        tableDto(),
        tableDto({
          tableInstanceId: 901,
          tableCode: 'Water',
          tableNameL10n: { values: { en: 'Water use' } },
          permitBinding: { code: 'PB2', mode: 'Rows', permitTypeCode: 'WaterPermit' },
        }),
      ],
    });

    expect(screen.getAllByRole('button', { name: k('doc.refresh') })).toHaveLength(2);
  });
});

describe('PermitRefreshStrip — оновлення', () => {
  it('викликає refresh за кодом з таблиці й періодом, показує підсумок і скидає зрізи САМЕ цього аркуша', async () => {
    const user = userEvent.setup();
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({ added: 2, updated: 1, removedFromPermit: [], warnings: [] });
    mount();

    await user.click(screen.getByRole('button', { name: k('doc.refresh') }));

    await waitFor(() => expect(runtimeApi.refreshPermitBinding).toHaveBeenCalledWith(55, 'PB41_Emissions', 202607));
    const result = await screen.findByTestId('permit-refresh-result');
    expect(result.textContent).toContain(kp('doc.result', { name: 'Emission sources', added: 2, updated: 1 }));
    expect(result.getAttribute('data-tone')).toBe('success');
    await waitFor(() => expect(invalidateSlices).toHaveBeenCalledWith(client, { documentId: 55, periodKey: 202607, sheetDefId: 11 }));
  });

  it('збережені рядки «поза дозволом» і попередження показуються, тон — попередження', async () => {
    const user = userEvent.setup();
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({
      added: 0,
      updated: 3,
      removedFromPermit: ['P7', 'P9'],
      warnings: [{ code: 'binding.warning.cellNotFromPermit', text: 'x' }],
    });
    mount();
    await user.click(screen.getByRole('button', { name: k('doc.refresh') }));

    const result = await screen.findByTestId('permit-refresh-result');
    expect(result.textContent).toContain(kp('doc.kept', { count: 2, keys: 'P7, P9' }));
    expect(result.textContent).toContain(k('warning.cellNotFromPermit'));
    expect(result.getAttribute('data-tone')).toBe('warning');
  });

  it('лише збережені рядки без попереджень — теж тон попередження (їх треба переглянути вручну)', async () => {
    const user = userEvent.setup();
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({ added: 1, updated: 0, removedFromPermit: ['P7'], warnings: [] });
    mount();
    await user.click(screen.getByRole('button', { name: k('doc.refresh') }));

    expect((await screen.findByTestId('permit-refresh-result')).getAttribute('data-tone')).toBe('warning');
  });

  it('нічого не змінилось — окреме речення, а не «0 додано, 0 оновлено»', async () => {
    const user = userEvent.setup();
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({ added: 0, updated: 0, removedFromPermit: [], warnings: [] });
    mount();
    await user.click(screen.getByRole('button', { name: k('doc.refresh') }));

    expect((await screen.findByTestId('permit-refresh-result')).textContent).toContain(kp('doc.unchanged', { name: 'Emission sources' }));
  });

  it('документ лише для читання: кнопка вимкнена з поясненням, запит не йде', async () => {
    const user = userEvent.setup();
    mount({ readOnly: true });

    const button = screen.getByRole('button', { name: k('doc.refresh') });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    expect(button.getAttribute('title')).toBe(k('doc.readOnly'));
    await user.click(button);
    expect(runtimeApi.refreshPermitBinding).not.toHaveBeenCalled();
  });

  it('відмова сервера (403 addRowDenied) — showApiError, підсумку й скидання зрізів немає', async () => {
    const user = userEvent.setup();
    const refusal = new EcrApiError({ title: 'Forbidden', status: 403, errorCode: 'ECR-ACCS-0403', correlationId: 'c' });
    vi.mocked(runtimeApi.refreshPermitBinding).mockRejectedValue(refusal);
    mount();
    await user.click(screen.getByRole('button', { name: k('doc.refresh') }));

    await waitFor(() => expect(showApiError).toHaveBeenCalledWith(refusal));
    expect(screen.queryByTestId('permit-refresh-result')).toBeNull();
    expect(invalidateSlices).not.toHaveBeenCalled();
  });
});
