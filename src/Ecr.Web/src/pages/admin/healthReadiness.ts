import { apiFetch, EcrApiError } from '@/api/client';
import type { HealthReport } from '@/api/types';

/**
 * Відповідь `/health/ready` разом із тим, чи сервер вважає систему готовою.
 *
 * `ready: false` — сервер відповів `503`, але з повноцінним звітом перевірок:
 * це стан системи, який треба ПОКАЗАТИ, а не відмова запиту.
 */
export interface Readiness {
  report: HealthReport;
  ready: boolean;
}

/**
 * Читає `/health/ready` (аудит U2).
 *
 * ⛔ Дефект: `MapHealthChecks` віддає `503` щоразу, коли зведений стан
 * `Unhealthy`, — і саме ТОДІ тіло несе найважливіше: яка перевірка впала і
 * чому. `apiFetch` кидає на будь-якому `!ok`, тож сторінка стану показувала
 * загальну помилку «сервіс недоступний» рівно в ту мить, коли адміністратор
 * відкрив її, щоб дізнатися, ЩО недоступне.
 *
 * ⚠ Звіт відновлюється з `EcrApiError`, бо `problemBodyOf` (`api/client.ts`)
 * розбирає тіло відмови як `problem+json`: `status` звіту (`"Unhealthy"`)
 * лягає в `problem.status`, а `checks`/`totalDurationMs` — у розширення
 * (`extensions2`). Звіт від справжнього `problem+json` відрізняє сукупність
 * ознак: `errorCode` склав транспорт (`HTTP-503`, тобто в тілі коду немає),
 * `status` — рядок, а не число, і `checks` — масив перевірок правильної форми.
 * `problem+json` від самого ECR (`ECR-SYS-0503`), тіло без звіту, битий JSON
 * чи мережева відмова лишаються помилкою — показ загальної помилки там
 * правильний.
 */
export async function fetchReadiness(): Promise<Readiness> {
  try {
    return { report: await apiFetch<HealthReport>('/health/ready'), ready: true };
  } catch (error) {
    const report = reportFromUnavailable(error);

    if (report === null) throw error;

    return { report, ready: false };
  }
}

function reportFromUnavailable(error: unknown): HealthReport | null {
  if (!(error instanceof EcrApiError)) return null;

  const { problem } = error;

  if (problem.errorCode !== 'HTTP-503') return null;

  const status: unknown = problem.status;
  const checks = problem.extensions2?.['checks'];

  if (typeof status !== 'string' || !Array.isArray(checks)) return null;

  const parsed: HealthReport['checks'] = [];

  for (const candidate of checks) {
    const check = checkOf(candidate);

    if (check === null) return null;

    parsed.push(check);
  }

  const duration = problem.extensions2?.['totalDurationMs'];

  return {
    status,
    checks: parsed,
    totalDurationMs: typeof duration === 'number' ? duration : 0,
  };
}

function checkOf(value: unknown): HealthReport['checks'][number] | null {
  if (typeof value !== 'object' || value === null) return null;

  const raw = value as Record<string, unknown>;

  if (typeof raw['name'] !== 'string' || typeof raw['status'] !== 'string') return null;

  const data = raw['data'];

  return {
    name: raw['name'],
    status: raw['status'],
    description: typeof raw['description'] === 'string' ? raw['description'] : null,
    durationMs: typeof raw['durationMs'] === 'number' ? raw['durationMs'] : 0,
    data: typeof data === 'object' && data !== null && !Array.isArray(data)
      ? (data as Record<string, unknown>)
      : {},
  };
}
