import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';

/**
 * ✎ DOCUX (доповнення координатора 26.11, схвалено людиною): «звідки число» в Info інспектора — для обчисленої комірки
 * «Calculated by: <методологія> · step <крок>», для вхідної — «Read by: <методології>» (з «required», коли методологія
 * вимагає значення).
 *
 * Джерело — зріз (контракт doc-ux-server, D-577): `ColumnDto.calculatedBy[]` / `ColumnDto.readBy[]`
 * `{methodologyCode, methodologyName, step, required}`. Сітка кладе їх у комірку інспектора (`GridFormulaBar`), окремого
 * запиту немає.
 */
export type ColumnMethodology = components['schemas']['ColumnMethodologyDto'];

export interface ProvenanceSource {
  readonly calculatedBy?: readonly ColumnMethodology[] | null;
  readonly readBy?: readonly ColumnMethodology[] | null;
}

export type CellProvenance =
  | { readonly kind: 'calculated'; readonly by: readonly { readonly methodology: string; readonly step: string | null }[] }
  | { readonly kind: 'input'; readonly readBy: readonly { readonly methodology: string; readonly required: boolean }[] };

const nameOf = (m: ColumnMethodology): string => (m.methodologyName.trim() !== '' ? m.methodologyName : m.methodologyCode);

/** Колонка зрізу → модель; нічого сказати — `null` (рядків немає, а не «—»). */
export function provenanceFrom(source: ProvenanceSource | null | undefined): CellProvenance | null {
  if (source === null || source === undefined) return null;
  const calculated = source.calculatedBy ?? [];
  if (calculated.length > 0) {
    return {
      kind: 'calculated',
      by: calculated.map((m) => ({ methodology: nameOf(m), step: m.step === null || m.step === '' ? null : m.step })),
    };
  }
  const readBy = (source.readBy ?? []).map((m) => ({ methodology: nameOf(m), required: m.required }));

  return readBy.length === 0 ? null : { kind: 'input', readBy };
}

/** Рядки Info (підпис → значення) — ключі каталогу літералами. */
export function provenanceLines(provenance: CellProvenance | null): { readonly label: string; readonly value: string }[] {
  if (provenance === null) return [];
  if (provenance.kind === 'calculated') {
    return [
      {
        label: t('inspector.info.calculatedBy'),
        value: provenance.by
          .map((entry) =>
            entry.step === null ? entry.methodology : t('inspector.info.calculatedByStep', { methodology: entry.methodology, step: entry.step }),
          )
          .join('; '),
      },
    ];
  }

  return [
    {
      label: t('inspector.info.readBy'),
      value: provenance.readBy
        .map((entry) => (entry.required ? t('inspector.info.readByRequired', { methodology: entry.methodology }) : entry.methodology))
        .join(', '),
    },
  ];
}
