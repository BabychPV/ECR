import { Suspense, lazy, useState, type JSX } from 'react';
import { Button, Select, Skeleton, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import '@/features/permits/table/permitDefTable.css';
import './bindings.css';
import { previewPermitBinding } from '../api/bindingsApi';
import type { PermitBinding, PermitBindingPreview, PermitPreviewParams } from '../api/bindingsTypes';
import { listPermits } from '../api/permitsApi';
import { reasonKind, warningKind } from './bindingModel';
import { reasonText, warningText } from './bindingText';
import { bindingPreviewKey } from './bindingsKeys';

// ⚠ Поле дати — лише через DateInputWithStyles і import() (бюджет маршруту D-132; рідне 	ype=date` заборонене, D15-09).
const DateOnlyInput = lazy(async () => ({
  default: (await import('@/shared/dates/DateInputWithStyles')).DateOnlyInput,
}));

/** Скільки рядків показує перегляд (решта — лічильником). */
export const PreviewRowLimit = 8;

export interface BindingPreviewProps {
  readonly templateVersionId: number;
  readonly binding: PermitBinding;
}

const today = (): string => new Date().toISOString().slice(0, 10);

function roleLabel(role: string): string {
  switch (role) {
    case 'RowKey':
      return t('permits.sheetBinding.role.rowKey');
    case 'Limit':
      return t('permits.sheetBinding.role.limit');
    case 'Info':
      return t('permits.sheetBinding.role.info');
    case 'Formula':
      return t('permits.sheetBinding.role.formula');
    default:
      return t('permits.sheetBinding.role.fact');
  }
}

const cellText = (value: unknown): string =>
  value === null || value === undefined || value === '' ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value);

/**
 * Перегляд аркуша очима користувача (макет `Preview as a user sees it`): дозвіл, причина його відсутності,
 * попередження, рядки зі штрихуванням на комірках, які пише машина.
 *
 * ⚠ Перший запит іде одразу, без параметрів (покаже причину «немає об'єкта»); далі - лише за кнопкою «Update preview»:
 * параметри (об'єкт, кінець періоду, дозвіл) друкуються, і запит на кожен символ був би шумом. `403` — це не збій, а відсутність права на реєстри дозволу: показується
 * спокійним рядком, а не помилкою.
 */
export function BindingPreview({ templateVersionId, binding }: BindingPreviewProps): JSX.Element {
  const fromHeader = binding.selectionRule === 'FromHeaderLookup';
  const [facility, setFacility] = useState('');
  const [periodEnd, setPeriodEnd] = useState(today());
  const [entry, setEntry] = useState<string | null>(null);
  const [applied, setApplied] = useState<PermitPreviewParams>({});

  const permits = useQuery({
    queryKey: ['permits', 'by-type', binding.permitTypeCode],
    queryFn: () => listPermits({ type: binding.permitTypeCode }),
    enabled: fromHeader,
  });

  const preview = useQuery({
    queryKey: bindingPreviewKey(templateVersionId, binding.code, applied),
    queryFn: () => previewPermitBinding(templateVersionId, binding.code, applied),
    retry: false,
  });

  const updating = usePendingLoading(preview.isFetching && !preview.isPending);

  const update = (): void => {
    setApplied(
      fromHeader
        ? entry === null
          ? {}
          : { permitEntryId: Number(entry) }
        : { ...(facility.trim().length > 0 ? { facilityName: facility.trim() } : {}), ...(periodEnd.length > 0 ? { periodEnd } : {}) },
    );
  };

  const forbidden = preview.error instanceof EcrApiError && preview.error.problem.status === 403;

  return (
    <section aria-labelledby="pm-bind-preview-h" data-testid="binding-preview">
      <h3 id="pm-bind-preview-h">{t('permits.sheetBinding.preview.title')}</h3>

      <div className="pm-bind-fields">
        {fromHeader ? (
          <Select
            label={t('permits.sheetBinding.preview.permit')}
            placeholder={t('permits.sheetBinding.preview.permitPlaceholder')}
            data={(permits.data?.items ?? []).map((permit) => ({
              value: String(permit.id),
              label: `${permit.number}${permit.location ? ` · ${permit.location}` : ''}`,
            }))}
            value={entry}
            onChange={setEntry}
            clearable
          />
        ) : (
          <>
            <TextInput
              label={t('permits.sheetBinding.preview.facility')}
              value={facility}
              onChange={(event) => setFacility(event.currentTarget.value)}
            />
            <Suspense fallback={<Skeleton height={56} radius="sm" />}>
              <DateOnlyInput label={t('permits.sheetBinding.preview.periodEnd')} value={periodEnd} onChange={setPeriodEnd} />
            </Suspense>
          </>
        )}
        <Button variant="default" onClick={update} loading={updating}>
          {t('permits.sheetBinding.preview.update')}
        </Button>
      </div>

      {preview.isPending ? <Skeleton height={120} radius="sm" /> : null}
      {forbidden ? <Banner tone="info" text={t('permits.sheetBinding.preview.forbidden')} testId="binding-preview-forbidden" /> : null}
      {preview.error !== null && !forbidden ? <ErrorAlert error={preview.error} onRetry={() => void preview.refetch()} /> : null}
      {preview.data !== undefined ? <PreviewBody data={preview.data} /> : null}
    </section>
  );
}

function PreviewBody({ data }: { readonly data: PermitBindingPreview }): JSX.Element {
  const { permit } = data;
  const shown = data.rows.slice(0, PreviewRowLimit);

  return (
    <>
      {permit.reasonIfNone !== null ? (
        <Banner tone="info" text={reasonText(reasonKind(permit.reasonIfNone))} testId="binding-preview-reason" />
      ) : (
        <Text size="sm" c="dimmed" mb="xs" data-testid="binding-preview-permit">
          {t('permits.sheetBinding.preview.permitLine', {
            number: permit.number ?? '',
            from: permit.validFrom ?? '—',
            to: permit.validTo ?? '—',
          })}
        </Text>
      )}

      {data.warnings.length > 0 ? (
        <ul data-testid="binding-preview-warnings">
          {data.warnings.map((warning) => (
            <li key={`${warning.code}:${warning.text}`}>
              <Text size="sm">{warningText(warningKind(warning))}</Text>
            </li>
          ))}
        </ul>
      ) : null}

      {data.rows.length > 0 ? (
        <>
          <div className="pm-gridnote">
            <span>{t('permits.sheetBinding.preview.hatched')}</span>
          </div>
          <div className="pm-tw" tabIndex={0} role="region" aria-label={t('permits.sheetBinding.preview.title')}>
            <table className="pm-t">
              <thead>
                <tr className="pm-cols">
                  {data.columns.map((column) => (
                    <th key={column.columnCode} scope="col">
                      {column.header}
                      <Text span size="xs" c="dimmed" ml="xs">
                        {roleLabel(column.role)}
                        {column.unit !== null ? ` · ${column.unit}` : ''}
                      </Text>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {shown.map((row) => (
                  <tr key={row.rowKey}>
                    {data.columns.map((column) => (
                      <td key={column.columnCode} className={row.locked.includes(column.columnCode) ? 'is-locked' : undefined}>
                        {cellText(row.cells[column.columnCode])}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {data.rows.length > shown.length ? (
            <Text size="xs" c="dimmed" mt="xs">
              {t('permits.sheetBinding.preview.more', { shown: shown.length, total: data.rows.length })}
            </Text>
          ) : null}
        </>
      ) : null}
    </>
  );
}
