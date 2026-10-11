import type { QueryClient } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import type { JobStatus } from '@/api/types';
import { invalidateSlices } from '@/features/grid/sliceCache';
import { outcomeOf, pollInterval, PollMs, type PlainJobOutcome } from '@/features/workflow/jobFollow';

/**
 * Скільки стежити за фоновим імпортом, перш ніж здатися.
 *
 * ⚠ Імпорт понад `LargeImportThreshold` (2000 комірок) іде задачею хвилини, а не
 * години; межа потрібна, щоб вкладка не питала сервер вічно про задачу, яку,
 * скажімо, видалили.
 */
const FollowMaxMs = 30 * 60_000;

/** Скільки опитувань поспіль можуть упасти минущою відмовою, перш ніж стеження здасться (X2-04). */
const FollowMaxFailures = 5;

/**
 * Чи відмова опитування минуща: обрив мережі, `408`/`429`, `5xx`. `401`/`403`/`404` та інші `4xx` —
 * остаточні (права, задачі немає).
 */
function isTransientFailure(error: unknown): boolean {
  if (!(error instanceof EcrApiError)) return true;

  const status = error.problem.status;

  return status >= 500 || status === 408 || status === 429;
}

/** Параметри для тестів: інтервал і межа часу. */
export interface FollowImportOptions {
  readonly pollMs?: number;
  readonly maxMs?: number;
  readonly wait?: (ms: number) => Promise<void>;
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Стежить за фоновим імпортом (`202` + `jobId`) і після кінцевого стану
 * перечитує зрізи документа (`G1-07`).
 *
 * ⛔ Доти гілка `202` інвалідувала лише `['jobs']`: коли задача завершувалась,
 * сітка відкритого документа показувала дані ДО імпорту (`staleTime` зрізів —
 * 5 хв, `refetchOnWindowFocus: false`), а правки імпортованих рядків ішли зі
 * старою версією й діставали `409` на «чужій» зміні, автор якої — сама людина.
 *
 * ⚠ Перечитування — на БУДЬ-ЯКИЙ кінцевий стан, не лише на успіх: що саме
 * встигла записати впала задача, клієнт не знає, а зайвий перезапит дешевший
 * за неправду на екрані. Адресно (`invalidateSlices`), як і синхронна гілка.
 *
 * @returns Підсумок стеження; `unknown` — стан прочитати не вдалося (немає
 * права `GET /jobs/{id}`, мережа) або сплив час: тоді зрізи лише позначаються
 * застарілими через ту саму адресну інвалідацію — гірше не буде.
 */
export async function followImportJob(
  queryClient: QueryClient,
  jobId: string,
  documentId: number,
  periodKey: number,
  options: FollowImportOptions = {},
): Promise<PlainJobOutcome> {
  const pollMs = options.pollMs ?? PollMs;
  const maxMs = options.maxMs ?? FollowMaxMs;
  const wait = options.wait ?? sleep;
  const startedAt = Date.now();

  let outcome: PlainJobOutcome = 'unknown';
  let failures = 0;
  for (;;) {
    try {
      const job = await queryClient.fetchQuery({
        queryKey: ['job', jobId],
        queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId)}`),
        staleTime: 0,
        retry: false,
      });
      failures = 0;

      if (pollInterval(job.state) === false) {
        outcome = outcomeOf(job.state, false);
        break;
      }
    } catch (error) {
      // ⛔ X2-04: один збій опитування (обрив мережі, 5xx, 429) завершував стеження — зрізи
      // позначались застарілими ДО кінця імпорту, а сітка лишалась зі старими даними й
      // `409` на власних правках. Минущу відмову переживаємо кількома спробами поспіль;
      // остаточну (немає права, задачі немає) — ні: далі питати нема сенсу.
      failures += 1;
      if (!isTransientFailure(error) || failures >= FollowMaxFailures) break;
    }

    if (Date.now() - startedAt >= maxMs) break;
    await wait(pollMs);
  }

  await invalidateSlices(queryClient, { documentId, periodKey });
  await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
  await queryClient.invalidateQueries({ queryKey: ['jobs'] });

  return outcome;
}
