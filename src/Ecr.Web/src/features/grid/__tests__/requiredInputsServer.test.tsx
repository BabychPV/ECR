import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import type { DocumentTableDto } from '@/api/types';
import { needsInputCells } from '../needsInput';
import { allNeedsInput, needsInputSummary, resetNeedsInput } from '../needsInputStore';
import { isRequiredInputsRefusal, refreshRequiredInputsAfterSave, requiredInputsKey } from '../requiredInputsServer';
import { SheetTables } from '../SheetTables';

vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: ({ tableInstanceId }: { tableInstanceId: number }) => <div data-testid={`grid-${String(tableInstanceId)}`} />,
}));

/**
 * ✎ DOCUX L6-клієнт (контракт doc-ux-server `GET …/tables/{id}/required-inputs`): лічильник, F8 і «Submit» бачать порожні
 * обов'язкові входи ВСІХ рядків і ВСІХ таблиць аркуша (і тих, яких ще не змонтовано прокруткою); модель сітки бере той
 * самий перелік; після збереження рядка з переліку — перечитується; відмова подання через входи — не тост, а перехід.
 */
function table(id: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'S' } },
    sheetOrdinal: 0,
    tableCode: `T${String(id)}`,
    tableDefId: id * 10,
    tableInstanceId: id,
    tableNameL10n: { values: { en: `T${String(id)}` } },
    tableOrdinal: id,
  } as unknown as DocumentTableDto;
}

const json = (body: unknown): Response => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

afterEach(() => {
  resetNeedsInput();
  vi.unstubAllGlobals();
});

describe('required-inputs сервера', () => {
  it('незмонтовані таблиці аркуша — їхні порожні обов’язкові входи в обході аркуша (Submit, F8); обрізаний перелік — «не перевірено до кінця»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.endsWith('/tables/7/required-inputs')) {
          return json({ tableInstanceId: 7, total: 2, truncated: false, cells: [
            { rowKey: 'r1', columnCode: 'FUEL', level: 'block', methodologies: ['M1'], message: 'm', derived: false },
            { rowKey: 'r9', columnCode: 'QTY', level: 'warn', methodologies: ['M2'], message: 'm', derived: false },
          ] });
        }
        // Стеля відповіді: перелік таблиці 8 обрізано — вона «перевірена не до кінця».
        if (url.endsWith('/tables/8/required-inputs')) return json({ tableInstanceId: 8, total: 900, truncated: true, cells: [] });
        throw new Error(`unexpected ${url}`);
      }),
    );
    // Спостерігача прокрутки немає — жодну таблицю не змонтовано.
    vi.stubGlobal('IntersectionObserver', class { observe(): void {} unobserve(): void {} disconnect(): void {} });

    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SheetTables documentId={1} periodKey={202609} readOnly={false} tables={[table(7), table(8)]} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => expect(needsInputSummary()).toMatchObject({ count: 2, blocking: 1, unmountedTables: 1 }));
    expect(allNeedsInput().map((entry) => `${String(entry.table.tableInstanceId)}:${entry.cell.rowKey}:${entry.cell.level}`)).toEqual(['7:r1:block', '7:r9:warn']);
  });

  it('модель сітки бере перелік сервера (рядки, яких не правили); набране значення знімає позначку одразу', () => {
    const base = {
      columns: [{ code: 'FUEL', isRequired: false }],
      rows: [{ rowKey: 'r1', rowKind: 'Item' }],
      blocked: [],
      warnings: [],
      issues: [],
      editable: () => true,
      server: [{ rowKey: 'r1', columnCode: 'FUEL', level: 'block', methodologies: ['M1'] }],
    };
    expect(needsInputCells({ ...base, shown: [{ __rowKey: 'r1' }] }).map((cell) => `${cell.level}:${cell.source}`)).toEqual(['block:methodology']);
    expect(needsInputCells({ ...base, shown: [{ __rowKey: 'r1', FUEL: 'Diesel' }] })).toHaveLength(0);
  });

  it('збережено рядок із переліку — перелік таблиці перечитується; інший рядок — ні', () => {
    const client = new QueryClient();
    client.setQueryData(requiredInputsKey(1, 7), { tableInstanceId: 7, total: 1, truncated: false, cells: [{ rowKey: 'r1', columnCode: 'A', level: 'block', methodologies: [], message: '', derived: false }] });
    refreshRequiredInputsAfterSave(client, 1, 7, ['r2']);
    expect(client.getQueryState(requiredInputsKey(1, 7))?.isInvalidated).toBe(false);
    refreshRequiredInputsAfterSave(client, 1, 7, ['r1']);
    expect(client.getQueryState(requiredInputsKey(1, 7))?.isInvalidated).toBe(true);
  });

  it('відмова подання через порожні входи — розпізнана (ECR-SUB-4221 validationBlocked, ECR-CALC-0437); інші — ні', () => {
    const refusal = (code: string, messageKey: string): EcrApiError =>
      new EcrApiError({ status: 422, title: 't', errorCode: code, extensions2: { messageKey } } as never);
    expect(isRequiredInputsRefusal(refusal('ECR-SUB-4221', 'err.ECR-SUB-4221.validationBlocked'))).toBe(true);
    expect(isRequiredInputsRefusal(refusal('ECR-CALC-0437', 'x'))).toBe(true);
    expect(isRequiredInputsRefusal(refusal('ECR-SUB-4221', 'err.ECR-SUB-4221.orphanedRows'))).toBe(false);
    expect(isRequiredInputsRefusal(new Error('x'))).toBe(false);
  });
});
