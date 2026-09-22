import type { JSX } from 'react';
import { Badge, Group, Text } from '@mantine/core';
import type { TableSliceDto } from '@/api/types';
import { CodeText } from '@/shared/ui/CodeText';
import { t } from '@/shared/i18n';
import { useFocusedCell } from './focusStore';
import { formulaBarModel } from './formulaBar';
import type { TotalsEdit } from './gridTotals';

/**
 * Рядок формули над сіткою документа (`UI-08`, `DIRECTIVE-15-FRONTEND.md` §3).
 *
 * ⛔ ЛИШЕ читання. Вираз шаблону правиться там, де він заведений, — у версії
 * шаблону (`TemplateVersionPage`, вкладка формул) і в методології; поле вводу
 * тут обіцяло б редагування, якого цей екран не робить і робити не повинен
 * (`decide()` віддає таку комірку як `CalculatedCell`).
 *
 * ⛔ Компонент читає фокус ІЗ СХОВИЩА (`focusStore.ts`), а не з пропу-стану:
 * інакше кожен рух курсора перемальовував би `DocumentGrid`, а з ним і весь
 * опис колонок на таблиці 500×60. Сюди приходять лише стабільні посилання —
 * зріз, колонки, моделі рядків, — тож перемальовується рівно цей рядок.
 *
 * ⚠ Чому текст виразу може бути відсутній — у коментарі `formulaBar.ts`:
 * сервер його зрізом не віддає. Рядок формули каже це словами, а не показує
 * порожнє місце, яке читається як «формули немає».
 */
export interface GridFormulaBarProps {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly slice: TableSliceDto;
  /** Колонки, які отримала сітка (з колонкою підпису, якщо вона є). */
  readonly columns: readonly { readonly prop?: string | number }[];
  /** Моделі рядків, які отримала сітка. */
  readonly rows: readonly Record<string, unknown>[];
  /** Ім'я властивості моделі, під яким лежить підпис рядка. */
  readonly labelProp: string;
  /** Незбережені правки зрізу, ключ — `rowKey:columnCode`. */
  readonly pending?: ReadonlyMap<string, TotalsEdit>;
  /** Тексти виразів за кодом колонки; порожня, доки сервер їх не віддає. */
  readonly expressionByColumnCode?: ReadonlyMap<string, string>;
}

export function GridFormulaBar(props: GridFormulaBarProps): JSX.Element {
  const focus = useFocusedCell(props.tableInstanceId, props.periodKey);

  const model = formulaBarModel({
    slice: props.slice,
    columns: props.columns,
    rows: props.rows,
    labelProp: props.labelProp,
    focus,
    ...(props.pending === undefined ? {} : { pending: props.pending }),
    ...(props.expressionByColumnCode === undefined
      ? {}
      : { expressionByColumnCode: props.expressionByColumnCode }),
  });

  return (
    <Group
      gap="xs"
      wrap="nowrap"
      role="group"
      aria-label={t('grid.formulaBarLabel')}
      data-formula-bar={model === null ? 'empty' : 'cell'}
    >
      {model === null ? (
        <Text size="xs" c="dimmed">
          {t('grid.formulaBarEmpty')}
        </Text>
      ) : (
        <>
          {/* Адреса комірки — те саме «де я», що й поле імені зліва від
              рядка формул Excel: підпис рядка плюс заголовок колонки. */}
          <Text size="xs" c="dimmed" data-formula-bar-address="">
            {t('grid.formulaBarAddress', {
              row: model.rowLabel,
              column:
                model.unitSymbol === null
                  ? model.columnHeader
                  : `${model.columnHeader}, ${model.unitSymbol}`,
            })}
          </Text>

          {model.isCalculated && (
            <Badge size="xs" variant="light" data-formula-bar-calculated="">
              {t('grid.formulaBarCalculated')}
            </Badge>
          )}

          {/*
            ⛔ Три різні твердження, і жодне не підміняє інше:
              — вираз є → показати його;
              — комірка обчислювана, а виразу немає → сказати, що його не
                віддає сервер (`formulaBar.ts`), а не мовчати;
              — звичайна комірка → показати значення, як поле вводу Excel.
          */}
          {model.expression !== null ? (
            <span data-formula-bar-expression="">
              <CodeText>{model.expression}</CodeText>
            </span>
          ) : model.isCalculated ? (
            <Text size="xs" c="dimmed" data-formula-bar-no-expression="">
              {t('grid.formulaBarNoExpression')}
            </Text>
          ) : null}

          <Text size="xs" data-formula-bar-value="">
            {model.value.length === 0
              ? t('grid.formulaBarNoValue')
              : t('grid.formulaBarValue', { value: model.value })}
          </Text>
        </>
      )}
    </Group>
  );
}
