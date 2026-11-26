import { useQuery } from '@tanstack/react-query';
import { t } from '@/shared/i18n';

/**
 * ✎ DOCUX (доповнення координатора 26.11, схвалено людиною): «звідки число» в Info інспектора — для обчисленої комірки
 * «Calculated by: <методологія> · step <крок>», для вхідної — «Read by: <методології>» (з «required», коли методологія
 * вимагає значення).
 *
 * ⚠ ТИМЧАСОВИЙ типізований адаптер: поле дає сервер (lane doc-ux-server), і доки його контракту немає, `provenanceSource`
 * — `null`: запиту немає, рядків немає (жодного вигаданого шляху — сторож споживачів дій сервера). Коли контракт
 * зведено — сюди підставляється читання згенерованого типу; форма `ProvenanceSource` — те, що узгоджено з сервером.
 */
export interface ProvenanceMethodology {
  readonly methodologyCode: string;
  readonly methodologyName?: string | null;
}

export interface ProvenanceSource {
  /** Обчислена комірка: методологія й крок, що її пишуть; `null` — не обчислена. */
  readonly calculatedBy?: (ProvenanceMethodology & { readonly stepCode?: string | null }) | null;
  /** Вхідна комірка: методології, що її читають; `required` — методологія вимагає значення. */
  readonly readBy?: readonly (ProvenanceMethodology & { readonly required?: boolean | null })[] | null;
}

export type CellProvenance =
  | { readonly kind: 'calculated'; readonly methodology: string; readonly step: string | null }
  | { readonly kind: 'input'; readonly readBy: readonly { readonly methodology: string; readonly required: boolean }[] };

const nameOf = (m: ProvenanceMethodology): string =>
  m.methodologyName !== undefined && m.methodologyName !== null && m.methodologyName.trim() !== '' ? m.methodologyName : m.methodologyCode;

/** Відповідь сервера → модель; нічого сказати — `null` (рядків немає, а не «—»). */
export function provenanceFrom(source: ProvenanceSource | null | undefined): CellProvenance | null {
  if (source === null || source === undefined) return null;
  if (source.calculatedBy !== null && source.calculatedBy !== undefined) {
    const step = source.calculatedBy.stepCode;
    return { kind: 'calculated', methodology: nameOf(source.calculatedBy), step: step === undefined || step === null || step === '' ? null : step };
  }
  const readBy = (source.readBy ?? []).map((m) => ({ methodology: nameOf(m), required: m.required === true }));

  return readBy.length === 0 ? null : { kind: 'input', readBy };
}

/** Рядки Info (підпис → значення) — ключі каталогу літералами. */
export function provenanceLines(provenance: CellProvenance | null): { readonly label: string; readonly value: string }[] {
  if (provenance === null) return [];
  if (provenance.kind === 'calculated') {
    return [
      {
        label: t('inspector.info.calculatedBy'),
        value:
          provenance.step === null
            ? provenance.methodology
            : t('inspector.info.calculatedByStep', { methodology: provenance.methodology, step: provenance.step }),
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

/** Адреса комірки для запиту «звідки число». */
export interface ProvenanceCell {
  readonly documentId: number;
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly rowKey: string;
  readonly columnCode: string;
}

/** Джерело даних; `null` — сервер ще не віддає поле (до зведення контракту doc-ux-server). */
export type ProvenanceFetcher = (cell: ProvenanceCell) => Promise<ProvenanceSource | null>;

let provenanceSource: ProvenanceFetcher | null = null;

/** Для тестів і для зведення з контрактом: підставити джерело. */
export function setProvenanceSource(fetcher: ProvenanceFetcher | null): void {
  provenanceSource = fetcher;
}

/** «Звідки число» для комірки інспектора; без джерела — `null` без запиту. */
export function useCellProvenance(cell: ProvenanceCell | null): CellProvenance | null {
  const fetcher = provenanceSource;
  const query = useQuery({
    queryKey: ['cell-provenance', cell?.documentId, cell?.tableInstanceId, cell?.periodKey, cell?.rowKey, cell?.columnCode],
    queryFn: () => (fetcher === null || cell === null ? Promise.resolve(null) : fetcher(cell)),
    enabled: fetcher !== null && cell !== null,
    retry: false,
  });

  return provenanceFrom(query.data);
}
