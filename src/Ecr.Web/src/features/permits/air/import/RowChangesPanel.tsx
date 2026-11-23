import type { JSX } from 'react';
import { Text, Title } from '@mantine/core';
import { formatNumber } from '@/shared/format/number';
import { language, t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import type { AirAddedColumn, AirPreview, ImportRowChange } from '../airTypes';
import { fieldLabel, type ColumnNames } from './fieldLabels';

/*
 * D-569 (P-1123-1, людина 23.11: «загальне правило одна остання завантажена таблиця актуальна»): before the apply the user sees
 * exactly what the new table replaces — every row of the permit (all regions / areas) per NAE and substance as added, changed
 * (field: old → new) or removed, the counts, and the columns this file does not have (empty after the apply: no carry-over).
 */

const KindText: Readonly<Record<ImportRowChange['kind'], () => string>> = {
  Added: () => t('permits.air.import.changes.added'),
  Changed: () => t('permits.air.import.changes.changed'),
  Removed: () => t('permits.air.import.changes.removed'),
};

function shown(value: string | null): string {
  return value === null || value === '' ? t('permits.air.import.changes.empty') : value;
}

function columnName(column: AirAddedColumn): string {
  const names = column.nameL10n;

  return fieldLabel(column.code, new Map([[column.code.toUpperCase(), names[language()] ?? names['en'] ?? column.code]]));
}

export interface RowChangesPanelProps {
  readonly preview: AirPreview;
  readonly columnNames?: ColumnNames | undefined;
}

export function RowChangesPanel({ preview, columnNames }: RowChangesPanelProps): JSX.Element {
  const body = preview.preview;
  const { diff } = body;
  const rows = body.rows ?? [];
  const emptied = preview.emptiedColumns ?? [];

  return (
    <section className="pi-panel" aria-labelledby="pi-changes-title" data-testid="air-row-changes">
      <Title order={2} size="h4" id="pi-changes-title">
        {t('permits.air.import.changes.title')}
      </Title>
      <Text size="sm" c="dimmed">
        {t('permits.air.import.changes.hint')}
      </Text>
      <Text size="sm" data-testid="air-changes-sources">
        {t('permits.air.import.changes.sources', {
          added: formatNumber(diff.newParents),
          changed: formatNumber(diff.changedParents),
          removed: formatNumber(diff.removedParents),
        })}
      </Text>
      <Text size="sm" data-testid="air-changes-substances">
        {t('permits.air.import.changes.substances', {
          added: formatNumber(diff.newChildren),
          changed: formatNumber(diff.changedChildren),
          removed: formatNumber(diff.removedChildren),
        })}
      </Text>
      {emptied.length > 0 && (
        <Banner
          tone="warning"
          testId="air-emptied-columns"
          title={t('permits.air.import.changes.emptiedTitle')}
          text={t('permits.air.import.changes.emptiedText', { columns: emptied.map(columnName).join(', ') })}
        />
      )}
      {rows.length === 0 ? (
        <Text size="sm">{t('permits.air.import.changes.none')}</Text>
      ) : (
        <div className="pa-scroll" role="region" aria-label={t('permits.air.import.changes.aria')} tabIndex={0}>
          <table className="pi-table">
            <thead>
              <tr>
                <th scope="col">{t('permits.air.import.changes.kind')}</th>
                <th scope="col">{t('permits.air.import.changes.nae')}</th>
                <th scope="col">{t('permits.air.import.changes.substance')}</th>
                <th scope="col">{t('permits.air.import.changes.what')}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, index) => (
                <tr key={`${row.kind}-${row.key}-${row.childKey ?? ''}-${row.scope ?? ''}-${String(index)}`} data-kind={row.kind} data-testid="air-row-change">
                  <td>{KindText[row.kind]()}</td>
                  <th scope="row" className="pa-code">
                    {row.key}
                    {row.scope !== null && (
                      <Text span size="xs" c="dimmed">
                        {' '}
                        {t('permits.air.import.changes.otherScope', { scope: row.scope })}
                      </Text>
                    )}
                    {row.ownerLabel != null && (
                      <Text span size="xs" c="orange.8" data-testid="air-row-change-other-permit">
                        {' '}
                        {t('permits.air.import.changes.otherPermit', { permit: row.ownerLabel })}
                      </Text>
                    )}
                  </th>
                  <td className="pa-code">{row.childKey ?? t('permits.air.import.changes.wholeSource')}</td>
                  <td>
                    {row.fields.length > 0 && (
                      <ul className="pi-changes">
                        {row.fields.map((change) => (
                          <li key={change.field}>
                            {t('permits.air.import.changes.field', {
                              field: fieldLabel(change.field, columnNames),
                              old: shown(change.old),
                              new: shown(change.new),
                            })}
                          </li>
                        ))}
                      </ul>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {body.rowsDropped > 0 && (
        <Text size="sm" c="dimmed">
          {t('permits.air.import.changes.dropped', { count: formatNumber(body.rowsDropped) })}
        </Text>
      )}
    </section>
  );
}
