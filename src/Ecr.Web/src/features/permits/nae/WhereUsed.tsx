import type { JSX } from 'react';
import { Skeleton, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatDate } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PermitsPaths } from '../shell/permitPaths';
import { useEntryReferences, type EntryReference } from './referencesApi';

export interface WhereUsedProps {
  readonly registryCode: string;
  readonly entryId: number;
}

function validity(item: EntryReference): string {
  if (item.validFrom !== null && item.validFrom !== undefined && item.validTo !== null && item.validTo !== undefined) {
    return t('treeEditor.panel.fromTo', { from: formatDate(item.validFrom), to: formatDate(item.validTo) });
  }
  if (item.validFrom !== null && item.validFrom !== undefined) return t('treeEditor.panel.from', { from: formatDate(item.validFrom) });
  if (item.validTo !== null && item.validTo !== undefined) return t('treeEditor.panel.until', { to: formatDate(item.validTo) });

  return '';
}

/**
 * «Де використовується» вузол структури: дозволи, що на нього посилаються (макет N2: дозвіл · тип · термін · речовини).
 * Дані - `GET /registries/{code}/entries/{id}/references`.
 *
 * ⚠ Порожній перелік - це «жоден дозвіл ще не посилається», а не «невідомо»: до N3 (таблиці дозволів) сервер повертає
 * порожньо, і текст про це чесно каже «поки що». Збій читання - окрема відмова з «Повторити», не порожній стан.
 */
export function WhereUsed({ registryCode, entryId }: WhereUsedProps): JSX.Element {
  const references = useEntryReferences(registryCode, entryId);
  const slow = usePendingLoading(references.isPending);
  const items = references.data?.items ?? [];

  return (
    <section aria-labelledby="nae-used-title" data-nae-used={references.isSuccess ? (items.length === 0 ? 'empty' : 'list') : 'loading'}>
      <Title order={3} size="h5" id="nae-used-title">
        {t('permits.nae.used.title')}
      </Title>

      {references.error !== null && references.error !== undefined ? (
        <ErrorAlert error={references.error} onRetry={() => void references.refetch()} />
      ) : references.isPending ? (
        slow ? <Skeleton height={48} radius="sm" /> : <div aria-busy="true" />
      ) : items.length === 0 ? (
        <Text size="sm" c="dimmed" role="status" data-nae-used-state="empty">
          {t('permits.nae.used.empty')}
        </Text>
      ) : (
        <table className="nae-used-table">
          <thead>
            <tr>
              <th scope="col">{t('permits.nae.used.permit')}</th>
              <th scope="col">{t('permits.nae.used.type')}</th>
              <th scope="col">{t('permits.nae.used.valid')}</th>
              <th scope="col">{t('permits.nae.used.substances')}</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr key={`${item.kind}-${String(item.permitId)}`}>
                <td>
                  <Link to={`${PermitsPaths.permits}/${String(item.permitId)}`}>{item.permitNumber}</Link>
                </td>
                <td>{item.permitTypeCode}</td>
                <td>{validity(item)}</td>
                <td>{item.substanceCount}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
