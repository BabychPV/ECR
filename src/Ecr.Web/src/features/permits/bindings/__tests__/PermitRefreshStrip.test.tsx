import type { JSX, ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import type { DocumentTableDto } from '@/api/types';
import { TestProviders } from '@/test/render';
import { showApiError } from '@/shared/ui/notify';
import * as bindingsApi from '../../api/bindingsApi';
import * as runtimeApi from '../../api/bindingsRuntimeApi';
import * as templateApi from '../../api/documentTemplateApi';
import { PermitRefreshStrip } from '../PermitRefreshStrip';
import { binding, k, kp } from './fixtures';

/**
 * «З дозволу» + «Оновити з дозволу» у документі (PM-11): показ, права, підсумок, відмови.
 *
 * Мутаційний доказ (виконано, відновлено): прибрати `disabled={readOnly}` валить «лише для читання»; у фільтрі
 * прив'язок прибрати `binding.mode === 'Rows'` валить «Formulas»; у `onSuccess` прибрати `invalidateQueries`
 * зрізу валить «скидає зріз»; у `onError` прибрати `showApiError` валить «відмова»; замінити
 * `keptKeys.length > 0` у тоні банера на `false` валить «збережені рядки».
 */
vi.mock('../../api/bindingsApi', () => ({ listPermitBindings: vi.fn() }));
vi.mock('../../api/bindingsRuntimeApi', () => ({ refreshPermitBinding: vi.fn() }));
vi.mock('../../api/documentTemplateApi', () => ({ getProjectDocumentTemplate: vi.fn() }));
vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showApiError: vi.fn() }));

const tableDto = (over: Partial<DocumentTableDto> = {}): DocumentTableDto =>
  ({
    allowsDynamicRows: true,
    maxDynamicRows: 200,
    sheetCode: 'Air',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Air' } },
    sheetOrdinal: 1,
    tableCode: 'Emissions',
    tableDefId: 41,
    tableInstanceId: 900,
    tableNameL10n: { values: { en: 'Emission sources' } },
    tableOrdinal: 1,
    ...over,
  }) as DocumentTableDto;

let client: QueryClient;
const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
  <TestProviders>
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  </TestProviders>
);

function mount(props: Partial<Parameters<typeof PermitRefreshStrip>[0]> = {}) {
  return render(
    <PermitRefreshStrip documentId={55} periodKey={202607} projectId={3} tables={[tableDto()]} readOnly={false} canView {...props} />,
    { wrapper },
  );
}

beforeEach(() => {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  vi.mocked(templateApi.getProjectDocumentTemplate).mockResolvedValue({ templateVersionId: 7 } as never);
  vi.mocked(bindingsApi.listPermitBindings).mockResolvedValue({ items: [binding()], isEditable: true });
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const forbidden = (): EcrApiError => new EcrApiError({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'c' });

describe('PermitRefreshStrip — показ', () => {
  it('без Template.View — нічого і жодного запиту', () => {
    mount({ canView: false });

    expect(screen.queryByTestId('permit-refresh-strip')).toBeNull();
    expect(templateApi.getProjectDocumentTemplate).not.toHaveBeenCalled();
  });

  it('403 на шаблон проєкту чи перелік — смуга мовчить, помилки на екрані немає', async () => {
    vi.mocked(templateApi.getProjectDocumentTemplate).mockRejectedValue(forbidden());
    mount();

    await waitFor(() => expect(templateApi.getProjectDocumentTemplate).toHaveBeenCalled());
    expect(screen.queryByTestId('permit-refresh-strip')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('таблиця з прив’язкою Rows: позначка «З дозволу», назва таблиці й кнопка', async () => {
    mount();

    expect(await screen.findByTestId('from-permit-badge')).toBeDefined();
    expect(screen.getByText(kp('doc.tableLine', { name: 'Emission sources' }))).toBeDefined();
    expect(screen.getByRole('button', { name: k('doc.refresh') })).toBeDefined();
    expect(bindingsApi.listPermitBindings).toHaveBeenCalledWith(7);
  });

  it('Formulas, вимкнена й чужого аркуша прив’язки не дають смуги', async () => {
    vi.mocked(bindingsApi.listPermitBindings).mockResolvedValue({
      items: [
        binding({ mode: 'Formulas', columns: [] }),
        binding({ code: 'PB2', sheetTableCode: 'Emissions', isActive: false }),
        binding({ code: 'PB3', sheetTableCode: 'OtherSheetTable' }),
      ],
      isEditable: true,
    });
    mount();

    await waitFor(() => expect(bindingsApi.listPermitBindings).toHaveBeenCalled());
    expect(screen.queryByTestId('permit-refresh-strip')).toBeNull();
  });
});

describe('PermitRefreshStrip — оновлення', () => {
  it('викликає refresh за кодом прив’язки й періодом, показує підсумок і скидає зріз сітки', async () => {
    const user = userEvent.setup();
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({ added: 2, updated: 1, removedFromPermit: [], warnings: [] });
    mount();

    await user.click(await screen.findByRole('button', { name: k('doc.refresh') }));

    await waitFor(() => expect(runtimeApi.refreshPermitBinding).toHaveBeenCalledWith(55, 'PB41_Emissions', 202607));
    const result = await screen.findByTestId('permit-refresh-result');
    expect(result.textContent).toContain(kp('doc.result', { name: 'Emission sources', added: 2, updated: 1 }));
    expect(result.getAttribute('data-tone')).toBe('success');
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['table-slice'] });
  });

  it('збережені рядки «поза дозволом» і попередження показуються, тон — попередження', async () => {
    const user = userEvent.setup();
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({
      added: 0,
      updated: 3,
      removedFromPermit: ['P7', 'P9'],
      warnings: [{ code: 'binding.warning.fieldUnreadable', text: 'x' }],
    });
    mount();
    await user.click(await screen.findByRole('button', { name: k('doc.refresh') }));

    const result = await screen.findByTestId('permit-refresh-result');
    expect(result.textContent).toContain(kp('doc.kept', { count: 2, keys: 'P7, P9' }));
    expect(result.textContent).toContain(k('warning.fieldUnreadable'));
    expect(result.getAttribute('data-tone')).toBe('warning');
  });

  it('лише збережені рядки без попереджень — теж тон попередження (їх треба переглянути вручну)', async () => {
    const user = userEvent.setup();
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({ added: 1, updated: 0, removedFromPermit: ['P7'], warnings: [] });
    mount();
    await user.click(await screen.findByRole('button', { name: k('doc.refresh') }));

    expect((await screen.findByTestId('permit-refresh-result')).getAttribute('data-tone')).toBe('warning');
  });

  it('нічого не змінилось — окреме речення, а не «0 додано, 0 оновлено»', async () => {
    const user = userEvent.setup();
    vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({ added: 0, updated: 0, removedFromPermit: [], warnings: [] });
    mount();
    await user.click(await screen.findByRole('button', { name: k('doc.refresh') }));

    expect((await screen.findByTestId('permit-refresh-result')).textContent).toContain(kp('doc.unchanged', { name: 'Emission sources' }));
  });

  it('документ лише для читання: кнопка вимкнена з поясненням, запит не йде', async () => {
    const user = userEvent.setup();
    mount({ readOnly: true });

    const button = await screen.findByRole('button', { name: k('doc.refresh') });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    expect(button.getAttribute('title')).toBe(k('doc.readOnly'));
    await user.click(button);
    expect(runtimeApi.refreshPermitBinding).not.toHaveBeenCalled();
  });

  it('відмова сервера (403 addRowDenied) — showApiError, підсумку немає', async () => {
    const user = userEvent.setup();
    const refusal = forbidden();
    vi.mocked(runtimeApi.refreshPermitBinding).mockRejectedValue(refusal);
    mount();
    await user.click(await screen.findByRole('button', { name: k('doc.refresh') }));

    await waitFor(() => expect(showApiError).toHaveBeenCalledWith(refusal));
    expect(screen.queryByTestId('permit-refresh-result')).toBeNull();
  });
});
