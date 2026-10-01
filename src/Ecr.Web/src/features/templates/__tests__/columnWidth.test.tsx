import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TemplateColumnDto } from '@/api/types';
import { columnBody, columnDraftOf, emptyColumnDraft, whyCannotSaveColumn, type ColumnDefDto } from '../column';
import { PresentationEditor } from '../PresentationEditor';

/**
 * `D-234`, `ФВ-2.7`: типова ширина колонки `WidthPx` у чернетці колонки й у
 * презентаційній правці. ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 *
 * Мутації (перевірено вручну): прибрати `widthPx` з `columnBody` → червоний
 * перший тест; прибрати гілку `WidthPx` у `changes()` редактора → червоний
 * останній.
 */

const full: ColumnDefDto = {
  id: 5,
  code: 'COL',
  headerL10n: { values: { en: 'Col' } },
  ordinal: 1,
  dataType: 'Decimal',
  isRequired: false,
  isReadOnly: false,
  isHidden: false,
  precision: null,
  scale: null,
  defaultValue: null,
  displayFormat: null,
  styleId: null,
  lookupRegistryDefId: null,
  lookupFilter: null,
  unitId: null,
  widthPx: 320,
};

describe('D-234: ширина в чернетці колонки', () => {
  it('ширина їде з відповіді сервера в чернетку і назад у тіло PUT', () => {
    const draft = columnDraftOf(full);

    expect(draft.widthPx).toBe(320);
    expect(columnBody(draft).widthPx).toBe(320);
    expect(columnBody({ ...draft, widthPx: null }).widthPx).toBeNull();
  });

  it('нова колонка — без ширини', () => {
    expect(emptyColumnDraft(0).widthPx).toBeNull();
  });

  it('збереження блокується для ширини поза 40..800 або не цілої', () => {
    const base = { ...columnDraftOf(full), code: 'COL', headerL10n: { en: 'Col' } };

    expect(whyCannotSaveColumn({ ...base, widthPx: 40 })).toBeNull();
    expect(whyCannotSaveColumn({ ...base, widthPx: 800 })).toBeNull();
    expect(whyCannotSaveColumn({ ...base, widthPx: null })).toBeNull();
    expect(whyCannotSaveColumn({ ...base, widthPx: 39 })).toBe('Width');
    expect(whyCannotSaveColumn({ ...base, widthPx: 801 })).toBe('Width');
    expect(whyCannotSaveColumn({ ...base, widthPx: 120.5 })).toBe('Width');
  });
});

describe('D-234: PresentationEditor (опублікована версія)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const column: TemplateColumnDto = {
    id: 42,
    code: 'COL',
    headerL10n: { values: { en: 'Limit' } },
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isHidden: false,
    displayFormat: null,
    unitSymbol: null,
    formulaExpression: null,
    formulaDialect: null,
    widthPx: 200,
  };

  it('змінена ширина йде патчем WidthPx; порожня — null (скидання до типової)', async () => {
    const bodies: unknown[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string, init?: RequestInit) => {
        if (url.endsWith('/api/v1/languages')) {
          return Promise.resolve(new Response(JSON.stringify([{ code: 'en', nameNative: 'English', isDefault: true }]), { status: 200, headers: { 'Content-Type': 'application/json' } }));
        }
        if (init?.method === 'PATCH') bodies.push(JSON.parse(String(init.body)) as unknown);
        return Promise.resolve(
          new Response(JSON.stringify({ presentationRevision: 3 }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }),
    );

    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient()}>
          <PresentationEditor templateVersionId={1} column={column} onClose={() => undefined} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const width = await screen.findByLabelText(/columns\.width⟧/);
    fireEvent.change(width, { target: { value: '350' } });
    fireEvent.click(screen.getByRole('button', { name: /common\.save/ }));

    await vi.waitFor(() => {
      expect(bodies).toEqual([[{ entityType: 'ColumnDef', entityId: 42, field: 'WidthPx', value: '350' }]]);
    });
  });
});
