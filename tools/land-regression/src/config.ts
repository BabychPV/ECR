import { readFileSync } from 'node:fs';

/** Відображення CSV → ECR. Лежить у локальному JSON поза репозиторієм. */
export interface Mapping {
  projectId: number;
  templateVersionId?: number;
  sheetDefId: number;
  /** Код таблиці Land (`TableCode`) у документі. */
  tableCode: string;
  periodKey: number;
  /** Колонка CSV з ключем рядка; без неї ключ — «r1», «r2», … */
  rowKeyColumn?: string;
  /** Колонка CSV → код ColumnDef вхідної комірки. */
  inputs: Record<string, string>;
  /** Колонка CSV → код викиду (`OutputCode`) у calculation-results. */
  expected: Record<string, string>;
  /** Відносний допуск; за замовчуванням 1e-6. */
  tolerance?: number;
  /** Додатково повідомляти викиди, яких немає в еталоні. */
  reportUnexpected?: boolean;
}

export interface Settings {
  baseUrl: string;
  userName: string;
  password: string;
  csvPath: string;
  mappingPath: string;
  outPath: string;
}

export function loadMapping(path: string): Mapping {
  const m = JSON.parse(readFileSync(path, 'utf8')) as Partial<Mapping>;
  const missing = (['projectId', 'sheetDefId', 'tableCode', 'periodKey', 'inputs', 'expected'] as const)
    .filter((k) => m[k] === undefined);
  if (missing.length) throw new Error(`Mapping: не вистачає полів: ${missing.join(', ')}`);
  if (Object.keys(m.expected!).length === 0) throw new Error('Mapping: expected порожній');
  return m as Mapping;
}

/** Аргументи: `--csv`, `--mapping`, `--out`, `--base-url`; секрети лише з env. */
export function loadSettings(argv: string[], env: NodeJS.ProcessEnv): Settings {
  const arg = (name: string): string | undefined => {
    const i = argv.indexOf(`--${name}`);
    return i >= 0 ? argv[i + 1] : undefined;
  };
  const need = (v: string | undefined, what: string): string => {
    if (!v) throw new Error(`Не задано ${what}`);
    return v;
  };
  return {
    baseUrl: need(arg('base-url') ?? env.ECR_BASE_URL, '--base-url або ECR_BASE_URL').replace(/\/+$/, ''),
    userName: need(env.ECR_USER, 'ECR_USER'),
    password: need(env.ECR_PASSWORD, 'ECR_PASSWORD'),
    csvPath: need(arg('csv') ?? env.LAND_CSV, '--csv або LAND_CSV'),
    mappingPath: need(arg('mapping') ?? env.LAND_MAPPING, '--mapping або LAND_MAPPING'),
    outPath: arg('out') ?? env.LAND_OUT ?? 'land-regression-result.json',
  };
}
