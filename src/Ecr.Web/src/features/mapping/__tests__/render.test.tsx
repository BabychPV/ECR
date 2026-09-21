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
      // ⚠ Рядок, як і на дроті: `decimal` у відповідях їде рядком
      // (`e470777a`), тому й фікстура має бути такою — інакше вона перевіряла
      // б форму, якої сервер уже не надсилає.
      foldedValue: '42.5',
      isActive: true,
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
      isActive: true,
    },
  ],
  rows: [
    {
      sourcePath: 'Flare_01_CO',
      timestamp: '2026-09-02T01:00:00Z',
      valueNumeric: '10',
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

/**
 * Те саме, плюс призупинений мапінг (`BE-27`) на поле, яке сервер через це
 * поклав у «йде нікуди»: адреси й розриви він рахує лише за діючими.
 */
const WithPaused: MappingPreview = {
  ...Preview,
  fields: [
    ...Preview.fields,
    {
      fieldMapId: 3,
      sourceField: 'Flare_01_NOx',
      // ⚠ Стан «лягає в комірку» навмисно: сервер рахує його й для
      // призупиненого, і саме тому екран не має права його показувати.
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 103,
      targetColumnCode: 'NOX_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 7,
      foldedValue: '9.5',
      isActive: false,
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

    /*
     * ⛔ Адреса шукається В МЕЖАХ того самого рядка, а не будь-де на екрані:
     * той самий текст стоїть і в підсумку мапінгу, тому пошук по всьому
     * документу лишався б зеленим, навіть якби колонку адреси з рядків
     * прибрали цілком.
     *
     * ✎ 2026-09-19, ЗМІНА ПОВЕДІНКИ, а не підгін локатора. Тут стояло
     * `getByText('2026-09-02T01:00:00Z')` — пошук СИРОГО рядка сервера як
     * видимого тексту. Відколи мітку малює `Timestamp` (`UI-07`), на екрані
     * читабельна форма з секундами, а сирий рядок лишився в `dateTime`.
     * Твердження тесту незмінне — рядок джерела видно разом з адресою, куди
     * він лягає; змінився лише спосіб знайти цей рядок, і атрибут
     * прив'язаний до значення точніше, ніж збіг тексту.
     */
    const cell = document.querySelector('time[datetime="2026-09-02T01:00:00Z"]');
    expect(cell, 'мітку рядка не намальовано елементом <time>').not.toBeNull();

    const row = cell?.closest('tr') ?? null;

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

describe('Перегляд мапінгу: призупинений мапінг', () => {
  function mapRow(sourceField: string): HTMLElement {
    const table = screen.getByText('⟦mapping.maps⟧').parentElement!.querySelector('table')!;
    const cell = within(table).getByText(sourceField);

    return cell.closest('tr')!;
  }

  it('призупинений мапінг позначено і без адреси', () => {
    show(<MappingRows preview={WithPaused} />);

    const row = mapRow('Flare_01_NOx');

    expect(within(row).getByText('⟦mapping.paused⟧')).toBeDefined();
    expect(row.getAttribute('data-mapping-state')).toBe('paused');
    // Приглушення — токеном, а не літералом кольору.
    expect(row.style.color).toBe('var(--ecr-muted)');
    // ⛔ Він нікуди не пише: ні адреси, ні числа «в комірці», ні стану.
    expect(screen.queryByText('Flare_01 · NOX_MASS')).toBeNull();
    expect(screen.queryByText('9.5')).toBeNull();
    expect(within(row).queryByText('⟦mapping.materialized⟧')).toBeNull();
    // Те, що пояснює вже зібране, лишається.
    expect(within(row).getByText('7')).toBeDefined();
  });

  it('діючий поруч лишається діючим: адреса і стан на місці', () => {
    show(<MappingRows preview={WithPaused} />);

    const row = mapRow('Flare_01_CO');

    expect(within(row).getByText('Flare_01 · CO_MASS')).toBeDefined();
    expect(within(row).queryByText('⟦mapping.paused⟧')).toBeNull();
    expect(row.hasAttribute('data-mapping-state')).toBe(false);
  });

  it('лічильник рахує діючі окремо від призупинених', () => {
    show(<MappingRows preview={WithPaused} />);

    expect(screen.getByTestId('mapping-counts').textContent).toBe(
      '⟦mapping.mapsSummary (active=2, paused=1)⟧',
    );
  });

  it('дзеркало: усі діючі — жодної позначки паузи й лічильника', () => {
    show(<MappingRows preview={Preview} />);

    expect(screen.queryByText('⟦mapping.paused⟧')).toBeNull();
    expect(document.querySelector('[data-mapping-state="paused"]')).toBeNull();
    expect(screen.queryByTestId('mapping-counts')).toBeNull();
  });

  it('на екрані поле лише з призупиненим мапінгом стоїть у «йде нікуди»', () => {
    show(<MappingGaps preview={WithPaused} />);

    const table = screen.getByText('⟦mapping.unmapped⟧').parentElement!.querySelector('table')!;

    expect(within(table).getByText('Flare_01_NOx')).toBeDefined();
  });

  it('призупинений без рядків розривом не значиться', () => {
    show(
      <MappingGaps
        preview={{
          ...Preview,
          fields: [
            Preview.fields[0]!,
            { ...Preview.fields[1]!, isActive: false },
          ],
          unmappedSourceFields: [],
          uncoveredColumns: [],
        }}
      />,
    );

    expect(screen.queryByText('Flare_1_CO')).toBeNull();
    expect(screen.getByText(/mapping\.noGaps/)).toBeDefined();
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
