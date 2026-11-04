import { useState, type JSX } from 'react';
import { Button, Text } from '@mantine/core';
import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import type { ImportIssue } from '../airTypes';
import { sortIssues } from './importModel';

/** Скільки знахідок показано одразу: файл на 2 600 рядків може дати їх тисячі, а DOM — не безкінечний. */
export const IssuesPageSize = 100;

function severityLabel(issue: ImportIssue): string {
  return issue.severity === 'Error' ? t('permits.air.import.issues.error') : t('permits.air.import.issues.warning');
}

export interface IssuesTableProps {
  readonly issues: readonly ImportIssue[];
  /** Знахідки, що не вмістились у ліміт сервера (`droppedIssues`). */
  readonly dropped?: number | undefined;
}

/**
 * Знахідки розбору й планування: серйозність, рядок, колонка, поле, текст з каталогу (`messageKey` + підстановки з
 * сервера). Помилки першими; показується порція, решта — за «Show more».
 */
export function IssuesTable({ issues, dropped = 0 }: IssuesTableProps): JSX.Element {
  const [shown, setShown] = useState(IssuesPageSize);
  const sorted = sortIssues(issues);

  if (sorted.length === 0) {
    return (
      <Text size="sm" role="status">
        {t('permits.air.import.issues.none')}
      </Text>
    );
  }

  return (
    <div className="pi-issues">
      <div className="pa-scroll" role="region" aria-label={t('permits.air.import.issues.aria')} tabIndex={0}>
        <table className="pi-table">
          <thead>
            <tr>
              <th scope="col">{t('permits.air.import.issues.severity')}</th>
              <th scope="col" className="pa-num">
                {t('permits.air.import.issues.row')}
              </th>
              <th scope="col">{t('permits.air.import.issues.column')}</th>
              <th scope="col">{t('permits.air.import.issues.field')}</th>
              <th scope="col">{t('permits.air.import.issues.message')}</th>
            </tr>
          </thead>
          <tbody>
            {sorted.slice(0, shown).map((issue, index) => (
              <tr key={`${String(issue.row)}-${issue.column ?? ''}-${issue.messageKey}-${String(index)}`}>
                <td>
                  <span className="pi-sev" data-severity={issue.severity}>
                    {severityLabel(issue)}
                  </span>
                </td>
                <td className="pa-num">{issue.row ?? '—'}</td>
                <td>{issue.column ?? '—'}</td>
                <td>{issue.field ?? '—'}</td>
                <td>{serverMessage(issue.messageKey, issue.args ?? {})}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {sorted.length > shown && (
        <Button variant="default" onClick={() => setShown((count) => count + IssuesPageSize)}>
          {t('permits.air.import.issues.more', { shown: formatNumber(shown), total: formatNumber(sorted.length) })}
        </Button>
      )}
      {dropped > 0 && (
        <Text size="sm" c="dimmed">
          {t('permits.air.import.issues.dropped', { count: formatNumber(dropped) })}
        </Text>
      )}
    </div>
  );
}
