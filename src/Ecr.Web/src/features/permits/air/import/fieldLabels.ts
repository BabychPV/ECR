import { useQuery } from '@tanstack/react-query';
import { getPermitType } from '@/features/permits/api/typesApi';
import type { PermitType } from '@/features/permits/api/types';
import { language, t } from '@/shared/i18n';
import { AirType, SourcesTable, SubstancesTable } from './addColumn';

/*
 * Readable names of the import target fields (07.11: «Matched columns» showed `productionCode`, `naeCode`…). Built-in fields
 * of the ERA table 3.3 profile (`BuiltInImportProfiles.cs`) take the labels of the permit card where it has them; a field
 * added with «Add as new column» (`FILE_<letter>`) takes the name of its column in the AIR permit type; anything else is
 * shown as it is.
 */

/** Built-in field (lower case: the server matches names without case) → its label. Literal keys: the seed guard reads them. */
const BuiltInLabels: Readonly<Record<string, () => string>> = {
  productioncode: () => t('permits.air.import.field.productionCode'),
  sourcename: () => t('permits.air.import.field.sourceName'),
  quantity: () => t('permits.air.sources.quantity'),
  hoursperyear: () => t('permits.air.sources.hours'),
  emissionpointname: () => t('permits.air.import.field.emissionPointName'),
  naecode: () => t('permits.air.import.field.naeCode'),
  sourcetype: () => t('permits.air.import.field.sourceType'),
  height: () => t('permits.air.sources.height'),
  diameter: () => t('permits.air.sources.diameter'),
  velocity: () => t('permits.air.sources.velocity'),
  volume: () => t('permits.air.sources.volume'),
  temperature: () => t('permits.air.sources.temperature'),
  x1: () => t('permits.air.import.field.x1'),
  y1: () => t('permits.air.import.field.y1'),
  x2: () => t('permits.air.import.field.x2'),
  y2: () => t('permits.air.import.field.y2'),
  substancecode: () => t('permits.air.import.field.substanceCode'),
  substancename: () => t('permits.air.substances.name'),
  gramspersecond: () => t('permits.air.substances.gs'),
  mgpernm3: () => t('permits.air.substances.mg'),
  tonsperyear: () => t('permits.air.substances.ty'),
};

/** Column code (upper case) → its name in the current language, from the two import tables of the AIR type. */
export type ColumnNames = ReadonlyMap<string, string>;

export const NoColumnNames: ColumnNames = new Map();

export function columnNamesOf(type: PermitType): ColumnNames {
  const lang = language();
  const out = new Map<string, string>();

  for (const table of type.tables) {
    if (table.code !== SourcesTable && table.code !== SubstancesTable) continue;
    for (const column of table.columns) {
      const names = column.nameL10n;
      const name = names[lang] ?? names['en'] ?? Object.values(names)[0];
      if (name !== undefined && name.trim() !== '') out.set(column.code.toUpperCase(), name.trim());
    }
  }

  return out;
}

/** The label of a target field: built-in label, else the permit-type column name, else the name itself. */
export function fieldLabel(name: string, columns: ColumnNames = NoColumnNames): string {
  const builtIn = BuiltInLabels[name.toLowerCase()];
  if (builtIn !== undefined) return builtIn();

  return columns.get(name.toUpperCase()) ?? name;
}

/**
 * Column names of the AIR type for the labels of added fields. Under `['permits', 'types', …]`: «Add as new column»
 * invalidates that prefix, so a just added column gets its name. A failure is silent — the labels fall back to the codes.
 */
export function useAirColumnNames(enabled: boolean): ColumnNames {
  const query = useQuery<PermitType, Error, ColumnNames>({
    queryKey: ['permits', 'types', AirType, 'import-labels'],
    queryFn: () => getPermitType(AirType),
    select: columnNamesOf,
    enabled,
    retry: false,
  });

  return query.data ?? NoColumnNames;
}
