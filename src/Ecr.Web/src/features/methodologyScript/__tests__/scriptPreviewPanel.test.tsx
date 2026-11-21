import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithQuery } from '@/test/render';
import { ScriptPreviewPanel } from '../ScriptPreviewPanel';
import { outline as outlineOf, previewRow, previewTable, previewValue } from './fixtures';

const preview = vi.hoisted(() => vi.fn());

vi.mock('@/features/methodologyScript/api', () => ({ previewMethodologyScript: preview }));
vi.mock('@/features/documents/api', () => ({
  listDocuments: () =>
    Promise.resolve({
      items: [
        {
          id: 7,
          businessKey: 'DOC-7',
          nameL10n: {},
          periodStates: [{ periodKey: 202601, state: 'Open' }],
        },
      ],
    }),
}));
vi.mock('@/api/client', async (original) => ({
  ...(await original<typeof import('@/api/client')>()),
  apiFetch: () => Promise.resolve([]),
}));
vi.mock('@/shared/ui/PeriodPicker', () => ({
  PeriodPicker: (props: { value: number | null; onChange: (value: number | null) => void }) => (
    <input
      data-testid="period"
      value={props.value ?? ''}
      onChange={(event) => {
        props.onChange(event.currentTarget.value === '' ? null : Number(event.currentTarget.value));
      }}
    />
  ),
}));

const result = {
  diagnostics: [],
  tables: [
    previewTable({
      tableCode: 'T3_00',
      tableInstanceId: 5,
      rows: [previewRow({ rowKey: 'a', rowNumber: 1, values: [previewValue({ name: 'OUT', text: '1' })] })],
    }),
  ],
};

async function runPreview(onCreateTest?: () => void): Promise<void> {
  renderWithQuery(
    <ScriptPreviewPanel
      methodologyId={1}
      versionId={2}
      getText={() => 'rows from T3_00;'}
      outline={outlineOf({ outputs: [{ code: 'OUT', unit: 't', perSubstance: false }] })}
      substanceEntryIds={{}}
      onJump={() => undefined}
      {...(onCreateTest === undefined ? {} : { onCreateTest })}
    />,
  );
  fireEvent.click(screen.getByPlaceholderText(/preview\.documentPlaceholder/));
  fireEvent.click(await screen.findByText('DOC-7'));
  await waitFor(() => {
    expect((screen.getByTestId('period') as HTMLInputElement).value).toBe('202601');
  });
}

describe('F13: перегляд — результат не переживає зміну періоду', () => {
  beforeEach(() => {
    preview.mockReset();
  });

  it('після зміни періоду старі числа зникають, а відповідь, що запізнилась, не показується', async () => {
    preview.mockResolvedValue(result);
    await runPreview();

    fireEvent.click(screen.getByRole('button', { name: /preview\.run/ }));
    expect(await screen.findByTestId('script-preview')).toBeTruthy();
    expect(preview.mock.calls[0]?.[2]).toMatchObject({ documentId: 7, periodKey: 202601 });

    fireEvent.change(screen.getByTestId('period'), { target: { value: '202602' } });
    expect(screen.queryByTestId('script-preview')).toBeNull();

    // запит у польоті, період змінили до відповіді
    let resolve: (value: typeof result) => void = () => undefined;
    preview.mockReturnValue(
      new Promise<typeof result>((done) => {
        resolve = done;
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: /preview\.run/ }));
    fireEvent.change(screen.getByTestId('period'), { target: { value: '202603' } });
    resolve(result);
    await waitFor(() => {
      expect(preview).toHaveBeenCalledTimes(2);
    });
    await new Promise((done) => setTimeout(done, 20));
    expect(screen.queryByTestId('script-preview')).toBeNull();
  });
});

describe('PSS-21: межі перегляду пояснено окремо від помилок скрипта', () => {
  beforeEach(() => {
    preview.mockReset();
  });

  it('вичерпаний бюджет перегляду й урізані величини — окремі пояснення; рядок позначено', async () => {
    preview.mockResolvedValue({
      ...result,
      budgetExhausted: true,
      tables: [
        previewTable({
          tableCode: 'T3_00',
          tableInstanceId: 5,
          rows: [
            previewRow({
              rowKey: 'a',
              rowNumber: 1,
              values: [previewValue({ name: 'OUT', text: '1' })],
              valuesTruncated: true,
            }),
            previewRow({
              rowKey: 'b',
              rowNumber: 2,
              issue: 'stepError',
              values: [previewValue({ name: 'OUT', text: '', error: '#BUDGET' })],
            }),
          ],
        }),
      ],
    });
    await runPreview();

    fireEvent.click(screen.getByRole('button', { name: /preview\.run/ }));
    expect(await screen.findByTestId('script-preview')).toBeTruthy();

    // Мутація: прибрати блок `budgetExhausted` з панелі — #BUDGET рядка 2 виглядає як помилка скрипта без пояснення.
    expect(screen.getByTestId('script-preview-budget-exhausted').textContent).toMatch(/preview\.budgetExhausted/);
    expect(screen.getByTestId('script-preview-values-truncated').textContent).toMatch(/preview\.valuesTruncated/);
    expect(screen.getAllByTestId('script-preview-row-truncated')).toHaveLength(1);
  });

  it('без прапорців відповіді пояснень немає', async () => {
    preview.mockResolvedValue({ ...result, budgetExhausted: false });
    await runPreview();

    fireEvent.click(screen.getByRole('button', { name: /preview\.run/ }));
    expect(await screen.findByTestId('script-preview')).toBeTruthy();

    expect(screen.queryByTestId('script-preview-budget-exhausted')).toBeNull();
    expect(screen.queryByTestId('script-preview-values-truncated')).toBeNull();
    expect(screen.queryByTestId('script-preview-row-truncated')).toBeNull();
  });
});
