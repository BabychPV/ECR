import { useEffect, type JSX } from 'react';
import { Checkbox, NativeSelect, SegmentedControl, Select, Skeleton, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import type { HeaderFieldDefDto } from '@/features/templates/headerField';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { pickName } from '../typeEditor/dialogs/names';
import { getPermitType } from '../api/typesApi';
import type { PermitTypeListItem } from '../api/types';
import '@/features/permits/table/permitDefTable.css';
import './bindings.css';
import {
  BindingRoles,
  completeMapping,
  type BindingDraft,
  type BindingMode,
  type MappingRole,
  type MappingRow,
  type SelectionRule,
  type SheetTableRef,
} from './bindingModel';

/** Дані майстра: чернетка плюс ключ «для якої таблиці дозволу розкладку вже запропоновано». */
export interface WizardData extends BindingDraft {
  readonly mappingKey: string;
}

type Update = (patch: Partial<WizardData>) => void;

const roleText = (role: MappingRole): string => {
  switch (role) {
    case 'RowKey':
      return t('permits.sheetBinding.role.rowKey');
    case 'Limit':
      return t('permits.sheetBinding.role.limit');
    case 'Info':
      return t('permits.sheetBinding.role.info');
    default:
      return t('permits.sheetBinding.role.none');
  }
};

/** Крок 1: таблиця аркуша — вже вибрана тим, звідки відкрито майстер; показується для повноти (як у макеті). */
export function SheetStep({ sheet, name }: { readonly sheet: SheetTableRef; readonly name: string }): JSX.Element {
  return (
    <div className="pm-bind-fields">
      <div>
        <Text size="sm" fw={500}>
          {t('permits.sheetBinding.wizard.sheetTable')}
        </Text>
        <Text data-testid="binding-sheet-table">{name}</Text>
        <Text size="xs" c="dimmed" ff="monospace">
          {sheet.code}
        </Text>
      </div>
      <Text size="sm" c="dimmed" className="pm-bind-wide">
        {t('permits.sheetBinding.wizard.sheetTableNote')}
      </Text>
    </div>
  );
}

export interface SourceStepProps {
  readonly data: WizardData;
  readonly update: Update;
  readonly types: readonly PermitTypeListItem[];
  /** Правка наявної прив'язки: тип і таблицю не змінити (сервер: `bindingIdentityChanged`). */
  readonly locked: boolean;
}

/** Крок 2: тип і таблиця дозволу, спосіб використання. */
export function SourceStep({ data, update, types, locked }: SourceStepProps): JSX.Element {
  const type = types.find((item) => item.code === data.permitTypeCode);

  return (
    <div className="pm-bind-fields">
      <Select
        id="pm-bind-type"
        label={t('permits.sheetBinding.wizard.type')}
        data={types.map((item) => ({ value: item.code, label: pickName(item.nameL10n) }))}
        value={data.permitTypeCode === '' ? null : data.permitTypeCode}
        disabled={locked}
        onChange={(value) => {
          const next = types.find((item) => item.code === value);
          update({ permitTypeCode: value ?? '', permitTableCode: next?.tables[0]?.code ?? '', mapping: [], mappingKey: '' });
        }}
      />
      <Select
        id="pm-bind-table"
        label={t('permits.sheetBinding.wizard.table')}
        data={(type?.tables ?? []).map((table) => ({
          value: table.code,
          label: t('permits.sheetBinding.wizard.tableOption', { name: pickName(table.nameL10n), count: table.columnCount }),
        }))}
        value={data.permitTableCode === '' ? null : data.permitTableCode}
        disabled={locked || type === undefined}
        onChange={(value) => update({ permitTableCode: value ?? '', mapping: [], mappingKey: '' })}
      />
      <div className="pm-bind-wide">
        <SegmentedControl
          fullWidth
          aria-label={t('permits.sheetBinding.wizard.mode')}
          value={data.mode}
          data={[
            { value: 'Rows', label: t('permits.sheetBinding.mode.rows') },
            { value: 'Formulas', label: t('permits.sheetBinding.mode.formulas') },
          ]}
          onChange={(value) => update({ mode: value as BindingMode })}
        />
        <Text size="sm" c="dimmed" mt="xs">
          {t('permits.sheetBinding.wizard.modeNote')}
        </Text>
      </div>
    </div>
  );
}

export interface PickStepProps {
  readonly data: WizardData;
  readonly update: Update;
  readonly headerFields: readonly HeaderFieldDefDto[];
}

/** Крок 3: як аркуш знаходить дозвіл. */
export function PickStep({ data, update, headerFields }: PickStepProps): JSX.Element {
  const lookups = headerFields.filter((field) => field.dataType === 'Lookup');
  const options: readonly { rule: SelectionRule; label: string; hint: string }[] = [
    {
      rule: 'ByFacilityPeriodEnd',
      label: t('permits.sheetBinding.rule.byFacility'),
      hint: t('permits.sheetBinding.rule.byFacilityHint'),
    },
    {
      rule: 'FromHeaderLookup',
      label: t('permits.sheetBinding.rule.fromHeader'),
      hint: t('permits.sheetBinding.rule.fromHeaderHint'),
    },
  ];

  return (
    <div className="pm-bind-fields">
      {options.map((option) => (
        <label key={option.rule} className="pm-bind-radio pm-bind-wide">
          <input
            type="radio"
            name="pm-bind-rule"
            checked={data.selectionRule === option.rule}
            onChange={() => update({ selectionRule: option.rule })}
          />
          <span>
            <Text fw={500}>{option.label}</Text>
            <Text size="sm" c="dimmed">
              {option.hint}
            </Text>
          </span>
        </label>
      ))}
      {data.selectionRule === 'FromHeaderLookup' ? (
        <Select
          id="pm-bind-header"
          className="pm-bind-wide"
          label={t('permits.sheetBinding.rule.headerField')}
          description={lookups.length === 0 ? t('permits.sheetBinding.rule.noHeaderFields') : undefined}
          data={lookups.map((field) => ({ value: String(field.id), label: localized(field.labelL10n) || field.code }))}
          value={data.headerFieldDefId === null ? null : String(data.headerFieldDefId)}
          onChange={(value) => update({ headerFieldDefId: value === null ? null : Number(value) })}
        />
      ) : null}
    </div>
  );
}

export interface ColumnsStepProps {
  readonly data: WizardData;
  readonly update: Update;
  readonly sheet: SheetTableRef;
}

/** Крок 4: зіставлення стовпців (лише режим `Rows`). Розкладка пропонується один раз на таблицю дозволу. */
export function ColumnsStep({ data, update, sheet }: ColumnsStepProps): JSX.Element {
  const type = useQuery({
    queryKey: ['permit-type', data.permitTypeCode],
    queryFn: () => getPermitType(data.permitTypeCode),
    enabled: data.mode === 'Rows' && data.permitTypeCode !== '',
  });

  const permitTable = type.data?.tables.find((table) => table.code === data.permitTableCode);
  const key = `${data.permitTypeCode}/${data.permitTableCode}`;

  // ⚠ Пропозиція ставиться ефектом, а не під час рендера: `update` змінює стан майстра. Повернення на крок
  // нічого не затирає — ключ уже збігається.
  useEffect(() => {
    if (data.mode === 'Rows' && permitTable !== undefined && data.mappingKey !== key) {
      update({ mapping: completeMapping(permitTable, data.mapping, sheet), mappingKey: key });
    }
  }, [data.mode, data.mappingKey, data.mapping, key, permitTable, sheet, update]);

  if (data.mode === 'Formulas') {
    return <Banner tone="info" text={t('permits.sheetBinding.wizard.formulasNoColumns')} testId="binding-formulas-note" />;
  }

  if (type.isPending) return <Skeleton height={160} radius="sm" />;
  if (type.error !== null) return <ErrorAlert error={type.error} onRetry={() => void type.refetch()} />;
  if (permitTable === undefined) return <Text c="dimmed">{t('permits.sheetBinding.wizard.pickTableFirst')}</Text>;

  const change = (fieldCode: string, patch: Partial<MappingRow>): void =>
    update({ mapping: data.mapping.map((row) => (row.fieldCode === fieldCode ? { ...row, ...patch } : row)) });

  const sheetOptions = [
    { value: '', label: t('permits.sheetBinding.wizard.chooseColumn') },
    ...sheet.columns.map((column) => ({
      value: String(column.id),
      label: column.isReadOnly ? column.name : t('permits.sheetBinding.wizard.editableColumn', { name: column.name }),
    })),
  ];

  return (
    <>
      <Text size="sm" c="dimmed" mb="xs">
        {t('permits.sheetBinding.wizard.columnsNote')}
      </Text>
      <div className="pm-tw" tabIndex={0} role="region" aria-label={t('permits.sheetBinding.wizard.columns')}>
        <table className="pm-t">
          <thead>
            <tr className="pm-cols">
              <th scope="col">{t('permits.sheetBinding.map.use')}</th>
              <th scope="col">{t('permits.sheetBinding.map.permitColumn')}</th>
              <th scope="col">{t('permits.sheetBinding.map.sheetColumn')}</th>
              <th scope="col">{t('permits.sheetBinding.map.role')}</th>
            </tr>
          </thead>
          <tbody>
            {data.mapping.map((row) => {
              const column = permitTable.columns.find((candidate) => candidate.code === row.fieldCode);
              const name = column === undefined ? row.fieldCode : pickName(column.nameL10n) || row.fieldCode;
              const used = row.role !== 'None';

              return (
                <tr key={row.fieldCode}>
                  <td>
                    <Checkbox
                      aria-label={t('permits.sheetBinding.map.useColumn', { name })}
                      checked={used}
                      onChange={(event) => change(row.fieldCode, { role: event.currentTarget.checked ? 'Info' : 'None' })}
                    />
                  </td>
                  <td>{name}</td>
                  <td>
                    <NativeSelect
                      aria-label={t('permits.sheetBinding.map.sheetColumnFor', { name })}
                      data={sheetOptions}
                      disabled={!used}
                      value={row.sheetColumnId === null ? '' : String(row.sheetColumnId)}
                      onChange={(event) => change(row.fieldCode, { sheetColumnId: event.currentTarget.value === '' ? null : Number(event.currentTarget.value) })}
                    />
                  </td>
                  <td>
                    <NativeSelect
                      aria-label={t('permits.sheetBinding.map.roleOf', { name })}
                      data={BindingRoles.map((role) => ({ value: role, label: roleText(role) }))}
                      disabled={!used}
                      value={used ? row.role : 'Info'}
                      onChange={(event) => change(row.fieldCode, { role: event.currentTarget.value as MappingRole })}
                    />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </>
  );
}
