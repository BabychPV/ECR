import type { JSX, KeyboardEvent } from 'react';
import { t } from '@/shared/i18n';
import type { PermitTypeColumn, PermitTypeTable } from '../api/types';
import { columnTypeLabel, secondaryTextOf, textOf } from './labels';
import './typeEditor.css';

export interface ColumnsTableProps {
  readonly columns: readonly PermitTypeColumn[];
  /** Усі таблиці типу: для підпису «Link → таблиця». */
  readonly tables: readonly PermitTypeTable[];
  /** Код стовпця, відкритого в інспекторі. */
  readonly selectedCode: string | null;
  /** Одиниця за ідентифікатором; невідома — `''` (число замість одиниці не показуємо). */
  readonly unitCode: (unitId: number) => string;
  /** Поля шапки лише читаються: клік нічого не відкриває. */
  readonly readOnly?: boolean | undefined;
  readonly onSelect: (columnCode: string) => void;
}

const Mark = ({ on, label }: { on: boolean; label: string }): JSX.Element =>
  on ? (
    <span aria-label={label} role="img">
      ✓
    </span>
  ) : (
    <span aria-hidden="true" />
  );

/**
 * Таблиця стовпців (мокап: `.pm-t.pm-map`): назва з другою мовою, код, тип, одиниця, «обовʼязковий»,
 * «частина ключа», група. Рядок — кнопка за клавіатурою (Enter/Space) і за мишею; вибраний має
 * `aria-selected`.
 */
export function ColumnsTable({ columns, tables, selectedCode, unitCode, readOnly = false, onSelect }: ColumnsTableProps): JSX.Element {
  const onKey = (event: KeyboardEvent<HTMLTableRowElement>, code: string): void => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onSelect(code);
    }
  };

  return (
    // PA2-U-03: область прокручується (на вузькій ширині), а без рядків у ній немає фокусованого вмісту - axe
    // `scrollable-region-focusable`. Сама область береться в Tab (`tabIndex=0`) з іменем; із рядками вона не потрібна.
    <div
      className="pt-table-wrap"
      {...(columns.length === 0 ? { tabIndex: 0, role: 'region', 'aria-label': t('permits.editor.cols.region') } : {})}
    >
      <table className="pt-table">
        <thead>
          <tr>
            <th scope="col">{t('permits.editor.cols.column')}</th>
            <th scope="col">{t('permits.editor.cols.code')}</th>
            <th scope="col">{t('permits.editor.cols.type')}</th>
            <th scope="col">{t('permits.editor.cols.unit')}</th>
            <th scope="col" className="pt-c">
              {t('permits.editor.cols.required')}
            </th>
            <th scope="col" className="pt-c">
              {t('permits.editor.cols.key')}
            </th>
            <th scope="col">{t('permits.editor.cols.group')}</th>
          </tr>
        </thead>
        <tbody>
          {columns.map((column) => {
            const name = textOf(column.nameL10n) || column.code;
            const second = secondaryTextOf(column.nameL10n, name);
            return (
              <tr
                key={column.code}
                className={readOnly ? undefined : 'pt-row'}
                tabIndex={readOnly ? undefined : 0}
                aria-label={readOnly ? undefined : name}
                aria-selected={readOnly ? undefined : selectedCode === column.code}
                onClick={readOnly ? undefined : () => onSelect(column.code)}
                onKeyDown={readOnly ? undefined : (event) => onKey(event, column.code)}
              >
                <td className="pt-first">
                  <span className="pt-two-line">
                    {name}
                    <small>{second === '' ? '—' : second}</small>
                  </span>
                </td>
                <td className="pt-mono">{column.code}</td>
                <td>{columnTypeLabel(column, tables)}</td>
                <td className="pt-mono">{column.unitId === null ? '' : unitCode(column.unitId)}</td>
                <td className="pt-c">
                  <Mark on={column.required} label={t('permits.editor.cols.required')} />
                </td>
                <td className="pt-c">
                  <Mark on={column.key} label={t('permits.editor.cols.key')} />
                </td>
                <td>{column.group ?? ''}</td>
              </tr>
            );
          })}
          {columns.length === 0 && (
            <tr>
              <td colSpan={7} className="pt-empty">
                {t('permits.editor.cols.empty')}
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
