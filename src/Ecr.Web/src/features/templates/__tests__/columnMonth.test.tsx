import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { formatMonthName } from '@/shared/format';
import { ColumnEditor } from '../ColumnEditor';
import { columnBody, columnDraftOf, emptyColumnDraft, whyCannotSaveColumn, type ColumnDefDto } from '../column';

/**
 * PS-P1C / D-PS-1: місяць колонки (`ColumnDef.IsMonthColumn`/`MonthNumber`) у чернетці
 * колонки, тілі `PUT` і панелі конструктора. Без цього правила вікна періоду
 * (`SourceWindow`/`OutsidePermitWindow`) не мали як отримати місяць колонки.
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 *
 * Мутації (перевірено вручну): прибрати `isMonthColumn`/`monthNumber` з `columnBody` →
 * червоні три перші тести; прибрати `monthNumber` з `columnDraftOf` → червоний перший.
 */

const full: ColumnDefDto = {
  id: 5,
  code: 'JAN',
  headerL10n: { values: { en: 'Jan' } },
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
};

describe('PS-P1C: місяць у чернетці колонки', () => {
  it('місяць з відповіді сервера їде в чернетку і назад у тіло PUT разом із прапором', () => {
    const draft = columnDraftOf({ ...full, isMonthColumn: true, monthNumber: 4 });

    expect(draft.monthNumber).toBe(4);
    expect(columnBody(draft)).toMatchObject({ isMonthColumn: true, monthNumber: 4 });
  });

  it('відповідь без нових полів (старий сервер) — колонка без місяця', () => {
    const draft = columnDraftOf(full);

    expect(draft.monthNumber).toBeNull();
    expect(columnBody(draft)).toMatchObject({ isMonthColumn: false, monthNumber: null });
    expect(emptyColumnDraft(0).monthNumber).toBeNull();
  });

  it('скинутий місяць — прапор false і null, а не прапор без місяця', () => {
    const body = columnBody({ ...columnDraftOf({ ...full, isMonthColumn: true, monthNumber: 4 }), monthNumber: null });

    expect(body.isMonthColumn).toBe(false);
    expect(body.monthNumber).toBeNull();
  });

  it('збереження блокується для місяця поза 1..12 або не цілого', () => {
    const base = { ...columnDraftOf(full), code: 'JAN', headerL10n: { en: 'Jan' } };

    expect(whyCannotSaveColumn({ ...base, monthNumber: 1 })).toBeNull();
    expect(whyCannotSaveColumn({ ...base, monthNumber: 12 })).toBeNull();
    expect(whyCannotSaveColumn({ ...base, monthNumber: null })).toBeNull();
    expect(whyCannotSaveColumn({ ...base, monthNumber: 0 })).toBe('Month');
    expect(whyCannotSaveColumn({ ...base, monthNumber: 13 })).toBe('Month');
    expect(whyCannotSaveColumn({ ...base, monthNumber: 2.5 })).toBe('Month');
  });
});

describe('PS-P1C: поле «Місяць колонки» в панелі колонки', () => {
  function mount(monthNumber: number | null, onChange = vi.fn()) {
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient()}>
          <ColumnEditor
            draft={{ ...columnDraftOf(full), monthNumber }}
            disabled={false}
            saving={false}
            templateVersionId={1}
            onChange={onChange}
            onSubmit={() => undefined}
            onCancel={() => undefined}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    return onChange;
  }

  it('показує обраний місяць назвою', () => {
    mount(3);

    expect((screen.getByLabelText(/columns\.month⟧/) as HTMLInputElement).value).toBe(formatMonthName(3));
  });

  it('вибір місяця кладе число у чернетку, очищення — null', () => {
    const onChange = mount(null);

    fireEvent.click(screen.getByLabelText(/columns\.month⟧/));
    fireEvent.click(screen.getByRole('option', { name: formatMonthName(5) }));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ monthNumber: 5 }));
  });
});
