import type { JSX } from 'react';
import { Select, Skeleton, Text, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { lookupLabel } from '@/features/registries/rc812/data';
import type { AirScopeCodes, AirScopeEntry, AirScopeField, AirScopeFields } from '../airTypes';
import type { ImportDraft } from './importModel';
import { InputOrder } from './inputOrder';
import { areaChoice, optionValue, regionOutsideTree, ScopeKind } from './scopeModel';

export interface ScopeStepProps {
  readonly permitNumber: string;
  readonly fields: AirScopeFields | undefined;
  readonly codes: AirScopeCodes | undefined;
  readonly failure: unknown;
  readonly onRetry: () => void;
  readonly draft: ImportDraft;
  readonly onChange: (patch: Partial<ImportDraft>) => void;
  readonly problem: 'region' | 'area' | null;
}

interface ScopeInputProps {
  readonly field: AirScopeField;
  readonly options: readonly AirScopeEntry[];
  readonly label: string;
  readonly description: string;
  readonly value: string;
  readonly error: string | null;
  readonly onChange: (value: string) => void;
  readonly autoFocus?: boolean;
}

/**
 * Одне поле області за видом колонки типу AIR: Registry — той самий пікер, що й у колонок-довідників (пошуковий
 * `Select`, підпис «назва (код)»); List — `Select` значень типу; Text — поле вводу.
 */
function ScopeInput({ field, options, label, description, value, error, onChange, autoFocus }: ScopeInputProps): JSX.Element {
  if (field.kind === ScopeKind.Text) {
    return (
      <TextInput
        label={label}
        description={description}
        inputWrapperOrder={InputOrder}
        required={field.required}
        placeholder={t('permits.air.import.text.placeholder')}
        value={value}
        error={error}
        onChange={(event) => onChange(event.currentTarget.value)}
        data-autofocus={autoFocus === true ? true : undefined}
      />
    );
  }

  const data = options.map((option) => ({
    value: optionValue(field, option),
    label: field.kind === ScopeKind.Registry ? lookupLabel({ code: option.code, display: option.name }) : option.name,
  }));

  return (
    <Select
      label={label}
      description={description}
      inputWrapperOrder={InputOrder}
      required={field.required}
      data={data}
      value={value === '' ? null : value}
      searchable
      clearable={!field.required}
      allowDeselect={!field.required}
      placeholder={field.required ? undefined : t('permits.air.import.area.none')}
      nothingFoundMessage={t('permits.air.import.scope.nothingFound')}
      disabled={!field.registryFound}
      error={error}
      onChange={(next) => onChange(next ?? '')}
      data-autofocus={autoFocus === true ? true : undefined}
    />
  );
}

function MissingRegistry({ field }: { readonly field: AirScopeField }): JSX.Element | null {
  if (field.kind === ScopeKind.Text || field.registryFound) return null;

  return (
    <div className="pi-wide">
      <Banner
        tone="warning"
        title={t('permits.air.import.scope.empty')}
        text={t('permits.air.import.scope.emptyHint', { registry: field.registryCode ?? '', column: field.columnCode })}
      />
    </div>
  );
}

/**
 * Крок 1 «Регіон і ділянка» (як «Import Parameters» в Excel: номер дозволу, регіон, ділянка). Поля — колонки REGION і
 * AREA типу AIR (`GET /air-limits/scope-fields`); Area показується лише для регіону з ділянками й лише ділянки цього регіону.
 */
export function ScopeStep({ permitNumber, fields, codes, failure, onRetry, draft, onChange, problem }: ScopeStepProps): JSX.Element {
  if (failure !== null && failure !== undefined) return <ErrorAlert error={failure} onRetry={onRetry} />;
  if (fields === undefined || codes === undefined) return <Skeleton height={160} role="status" aria-label={t('common.loading')} />;

  const area = areaChoice(fields, codes, draft.region);
  const reset = { resolutions: {}, confirmedReplace: false } as const;

  return (
    <div className="pi-grid">
      <TextInput label={t('permits.air.import.permitNumber')} value={permitNumber} readOnly />
      <ScopeInput
        field={fields.region}
        options={fields.region.options}
        label={t('permits.air.import.region.label')}
        description={t('permits.air.import.region.hint')}
        value={draft.region}
        error={problem === 'region' ? t('permits.air.import.region.required') : null}
        onChange={(value) => onChange({ region: value, area: '', ...reset })}
        autoFocus
      />
      {area.show && (
        <ScopeInput
          field={fields.area}
          options={area.options}
          label={t('permits.air.import.area.label')}
          description={t('permits.air.import.area.hint')}
          value={draft.area}
          error={problem === 'area' ? t('permits.air.import.area.required') : null}
          onChange={(value) => onChange({ area: value, ...reset })}
        />
      )}
      <MissingRegistry field={fields.region} />
      {area.show && <MissingRegistry field={fields.area} />}
      {regionOutsideTree(fields, draft.region) && (
        <div className="pi-wide">
          <Banner tone="warning" title={t('permits.air.import.scope.notInTreeTitle')} text={t('permits.air.import.scope.notInTreeText')} />
        </div>
      )}
      <Text size="sm" c="dimmed" className="pi-wide">
        {t('permits.air.import.scopeNote')}
      </Text>
    </div>
  );
}
