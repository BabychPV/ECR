import type { PermitColumn, PermitRow } from '../lib/columns';

/**
 * Фікстура: таблиця «Discharge volumes» типу «Water discharge permit» з мокапу
 * (`docs/design/permits/screens-permits.js`, `TYPES[2]`) — 31 стовпець у п'яти групах.
 * Підписи англійські, як у мокапі. Лише для тестів.
 */
const Volume = '10³ m³';
const Months = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
] as const;

const location = 'Location of the point';
const clean = 'Contaminated and clean water';
const monthly = 'Estimated volume by month';

function text(code: string, label: string, group: string, unit?: string): PermitColumn {
  return { code, label, type: 'text', group, unit };
}

export const dischargeColumns: readonly PermitColumn[] = [
  { code: 'WaterBody', label: 'Water body', type: 'link', required: true, key: true, ref: 'WaterBodies' },
  text('Source_code', 'Source code', location),
  text('Sea_river_code', 'Sea/river code', location),
  text('Quality_code', 'Quality code', location),
  text('Distance_mouth_km', 'Distance from mouth', location, 'km'),
  text('Transmitting_org', 'Transmitting organisation', location),
  text('Water_mgmt_site', 'Water management site', location),
  ...[1, 2, 3, 4, 5].map((i) => text(`Tributary_${i}`, `Tributary ${i}`, 'Tributaries')),
  { code: 'Sub_Start', label: 'Row valid from', type: 'date', key: true, group: 'Dates' },
  { code: 'Sub_End', label: 'Row valid to', type: 'date', group: 'Dates' },
  ...(
    [
      ['Norm_clean', 'Normatively clean'],
      ['Norm_treated', 'Normatively treated'],
      ['Insuff_treated', 'Insufficiently treated'],
      ['No_treatment', 'Without treatment'],
    ] as const
  ).map(
    ([code, label]): PermitColumn => ({ code, label, type: 'number', unit: Volume, group: clean }),
  ),
  ...Months.map(
    (month): PermitColumn => ({
      code: month.slice(0, 3),
      label: month,
      type: 'number',
      unit: Volume,
      group: monthly,
    }),
  ),
  { code: 'Annual', label: 'Annual stated', type: 'number', unit: Volume, required: true },
];

function monthValues(values: readonly number[]): Record<string, number> {
  const out: Record<string, number> = {};
  Months.forEach((month, i) => {
    out[month.slice(0, 3)] = values[i] ?? 0;
  });
  return out;
}

const monthSample = [16.333, 14.753, 16.333, 15.807, 16.333, 15.807, 16.333, 16.333, 15.807, 16.333, 15.807, 16.333];

export const dischargeRows: readonly PermitRow[] = [
  {
    id: 'd1',
    WaterBody: 'Caspian Sea (north)',
    Source_code: 'sea-10',
    Sea_river_code: '–',
    Quality_code: 'МР',
    Sub_Start: '2026-01-01',
    Sub_End: '2026-04-05',
    Norm_clean: 192.312,
    Norm_treated: 193.312,
    Insuff_treated: 0,
    No_treatment: 0,
    ...monthValues(monthSample),
    Annual: 192.312,
  },
  {
    id: 'd2',
    WaterBody: 'Caspian Sea (north)',
    Source_code: 'sea-10',
    Quality_code: 'МР',
    Sub_Start: '2026-04-06',
    Sub_End: '2026-12-31',
    Norm_clean: 96.156,
    Norm_treated: 0,
    ...monthValues(monthSample.map((v) => Number((v * 0.5).toFixed(3)))),
    // Обовʼязкове «Annual» навмисно порожнє: комірка має позначитися як «Required».
    Annual: null,
  },
];
