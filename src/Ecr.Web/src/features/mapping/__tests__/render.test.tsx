import type { JSX } from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MappingPreview } from '@/api/types';
import { MappingGaps } from '@/features/mapping/MappingGaps';
import { MappingRows } from '@/features/mapping/MappingRows';

/**
 * Подання перегляду мапінгу (`ФВ-13.14`).
 *
 * ⛔ Перевіряються **компоненти**, а не сторінка цілком. Причина та сама, що
 * й у `D1-12`: рендер `Select` Mantine у jsdom іде хвилинами, і тест на
 * сторінку довелося б або вимкнути, або дати йому дві хвилини на кожен
 * випадок. Сторінка як ціле лишається під a11y-прогоном, який уже платить цю
 * ціну один раз.
 *
 * ⚠ Каталог рядків тут не завантажений, тому підписи приходять ключами в
 * `⟦…⟧`. Перевіряються **дані**: шлях тега, число, код колонки — тобто саме
 * те, заради чого екран існує.
 */
const Preview: MappingPreview = {
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: 'Факел 01',
  fromUtc: '2026-09-01T00:00:00Z',
  toUtc: '2026-09-08T00:00:00Z',
  pointsSeen: 3,
  isTruncated: false,
  fields: [
    {
      fieldMapId: 1,
      sourceField: 'Flare_01_CO',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 100,
      targetColumnCode: 'CO_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 2,
      foldedValue: 42.5,
    },
    {
      fieldMapId: 2,
      sourceField: 'Flare_1_CO',
      outcome: 'NoData',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 101,
      targetColumnCode: 'CO_ALT',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 0,
      foldedValue: null,
    },
  ],
  rows: [
    {
      sourcePath: 'Flare_01_CO',
      timestamp: '2026-09-02T01:00:00Z',
      valueNumeric: 10,
      valueString: null,
      quality: 'Good',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnCode: 'CO_MASS',
      aggregation: 'Sum',
    },
  ],
  unmappedSourceFields: [
    { sourcePath: 'Flare_01_NOx', pointCount: 7, lastSeenUtc: '2026-09-02T02:00:00Z' },
  ],
  uncoveredColumns: [
    {
      tableDefId: 50,
      columnDefId: 102,
      code: 'CH4_MASS',
      header: 'CH4 mass',
      isRequired: true,
      isUnfillable: true,
    },
  ],
};

function show(node: JSX.Element): void {
  render(<MantineProvider>{node}</MantineProvider>);
}

describe('Перегляд мапінгу: реальні рядки', () => {
  it('показує рядок джерела разом із адресою, куди він лягає', () => {
    // ⛔ Дослівна вимога: перегляд НА РЕАЛЬНИХ РЯДКАХ. Без адреси поруч це
    // був би просто дамп точок.
    show(<MappingRows preview={Preview} />);

    // ⛔ Адреса шукається В МЕЖАХ того самого рядка, а не будь-де на екрані:
    // той самий текст стоїть і в підсумку мапінгу, тому пошук по всьому
    // документу лишався б зеленим, навіть якби колонку адреси з рядків
    // прибрали цілком.
    const cell = screen.getByText('2026-09-02T01:00:00Z');
    const row = cell.closest('tr');

    expect(row).not.toBeNull();
    expect(within(row!).getByText('Flare_01 · CO_MASS')).toBeDefined();
    expect(within(row!).getByText('10')).toBeDefined();
  });

  it('показує число, яке ляже в комірку, і обидві одиниці', () => {
    // ⚠ Одиниці обидві: розбіжність між одиницею джерела і цільовою — це
    // «найчастіше джерело мовчазних розбіжностей у числах» (`ФВ-16.9`).
    show(<MappingRows preview={Preview} />);

    expect(screen.getByText('42.5')).toBeDefined();
    expect(screen.getAllByText(/kg\s*→\s*t/).length).toBe(Preview.fields.length);
  });
});

describe('Перегляд мапінгу: розриви', () => {
  it('показує поле джерела, яке не лягає нікуди', () => {
    show(<MappingGaps preview={Preview} />);

    expect(screen.getByText('Flare_01_NOx')).toBeDefined();
    expect(screen.getByText('7')).toBeDefined();
  });

  it('показує мапінг, під який у джерелі немає жодного рядка', () => {
    // ⛔ Найдорожчий розрив: друкарська помилка в шляху AF, яку інакше
    // знаходять через місяць порожнім збором.
    show(<MappingGaps preview={Preview} />);

    expect(screen.getByText('Flare_1_CO')).toBeDefined();
  });

  it('показує колонку, за якою не стоїть нічого', () => {
    show(<MappingGaps preview={Preview} />);

    expect(screen.getByText('CH4_MASS')).toBeDefined();
  });

  it('без розривів каже про це прямо, а не мовчить', () => {
    // ⚠ `ФВ-14.22`: порожній перелік без слів читався б як «перевірка не
    // відпрацювала».
    show(
      <MappingGaps
        preview={{
          ...Preview,
          fields: [Preview.fields[0]!],
          unmappedSourceFields: [],
          uncoveredColumns: [],
        }}
      />,
    );

    expect(screen.getByText(/mapping\.noGaps/)).toBeDefined();
    expect(screen.queryByText('Flare_01_NOx')).toBeNull();
  });
});
