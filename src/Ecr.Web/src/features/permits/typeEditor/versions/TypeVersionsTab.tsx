import { useMemo, type JSX } from 'react';
import { Stack, Text } from '@mantine/core';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { DataTable } from '@/shared/ui/DataTable/DataTable';
import type { DataTableColumn } from '@/shared/ui/DataTable/types';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';

/**
 * Вкладка «Versions» конструктора типу (мокап §4, `versions`; Permits, PM-08c).
 *
 * ⛔ API історії версій ПОКИ немає (`PermitTypeDto` несе лише опубліковану версію й, якщо є,
 * чернетку), тому вкладка показує рівно відоме: опубліковану `vN` з датою публікації та відкриту
 * чернетку `vN+1`. Колонки «Author» і «What changed» мокапу не малюються — даних для них немає, а
 * вигадані рядки історії були б неправдою (`D15-06`). Коли з'явиться історія, рядки додаються
 * сюди без зміни форми таблиці.
 */
export interface TypeVersionsTabProps {
  readonly type: PermitType;
}

/** Рядок вкладки. */
export interface VersionRow {
  readonly key: string;
  readonly version: number;
  readonly state: 'Draft' | 'Published';
  readonly date: string | null;
}

/** Відомі версії типу: нова зверху. */
export function versionRows(type: PermitType): VersionRow[] {
  const rows: VersionRow[] = [];

  if (type.status === 'Draft') {
    rows.push({
      key: 'draft',
      version: type.draftVersion ?? type.version,
      state: 'Draft',
      date: type.updatedAt,
    });

    return rows;
  }

  if (type.hasUnpublishedChanges) {
    rows.push({
      key: 'draft',
      version: type.draftVersion ?? type.version + 1,
      state: 'Draft',
      date: type.updatedAt,
    });
  }

  rows.push({ key: 'published', version: type.version, state: 'Published', date: type.publishedAt });

  return rows;
}

export function TypeVersionsTab({ type }: TypeVersionsTabProps): JSX.Element {
  const rows = useMemo(() => versionRows(type), [type]);

  const columns: readonly DataTableColumn<VersionRow>[] = [
    {
      key: 'version',
      label: t('permits.editor.versions.colVersion'),
      mono: true,
      sortable: false,
      render: (row) => `v${String(row.version)}`,
    },
    {
      key: 'state',
      label: t('permits.editor.versions.colState'),
      sortable: false,
      render: (row) => <StatusBadge kind="version" state={row.state} quiet />,
    },
    {
      key: 'date',
      label: t('permits.editor.versions.colDate'),
      sortable: false,
      render: (row) => <Timestamp value={row.date} />,
    },
  ];

  return (
    <Stack gap="md">
      <DataTable<VersionRow>
        columns={columns}
        rows={rows}
        rowKey={(row) => row.key}
        caption={t('permits.editor.versions.caption')}
      />

      <Text size="sm" c="dimmed">
        {t('permits.editor.versions.note')}
      </Text>
    </Stack>
  );
}
