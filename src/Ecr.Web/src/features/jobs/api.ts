import { useMutation, type UseMutationResult } from '@tanstack/react-query';
import { apiEnqueue, type AcceptedJob } from '@/api/client';
import { showApiError } from '@/shared/ui/notify';

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника — той самий
 * прийом, що й `features/registries/api.ts` і `features/units/api.ts`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в коді клієнта літерал
 * `/api/v1/…` разом із методом поруч, і винесений префікс зробив би дію
 * «недосяжною з інтерфейсу».
 */

/**
 * Просить сервер зупинити фонову задачу.
 *
 * ⛔ До цієї дії довгу задачу не можна було спинити НІЯК: річний перерахунок
 * іде двадцять хвилин, а єдиним способом його обірвати було витіснення
 * зсередини планувальника (`D2-64`) — «щоб зупинити, запусти ще раз».
 *
 * ⚠ `jobId` кодується `encodeURIComponent`, і це не косметика. Ідентифікатор
 * має вигляд `IRecalculationJob#42`, а `#` в URL ПОЧИНАЄ ФРАГМЕНТ: незакодований
 * він обрізає шлях до `/api/v1/jobs/IRecalculationJob`, сервер віддає 404, і
 * виглядає це як «задачі немає», а не як зламана адреса. Саме на цьому падав
 * крок 17 `smoke.ps1`, і шістнадцять кроків перед ним проходили.
 *
 * ⚠ Відповідь — `202`, не результат: задача бачить токен і закривається станом
 * `Cancelled` на найближчій межі батчу. Стан дочитується тим самим
 * `GET /api/v1/jobs/{jobId}`, яким екран уже показує прогрес.
 */
export function cancelJob(jobId: string): Promise<AcceptedJob> {
  return apiEnqueue(`/api/v1/jobs/${encodeURIComponent(jobId)}/cancel`);
}

/**
 * Скасування задачі як мутація React Query.
 *
 * ⚠ `onSuccess` НЕ інвалідує стан задачі, а лишає це викликачеві: екран уже
 * опитує `GET /jobs/{jobId}` з інтервалом, доки стан `Queued`/`Running`, і
 * друге джерело оновлення дало б два запити на одну подію.
 */
// ⚠ Тип помилки — `unknown`, а не `Error`: `showApiError` приймає саме його,
// і `EcrApiError` доїжджає сюди через межу, де ніхто не обіцяв класу винятку.
export function useCancelJob(
  onCancelled?: () => void,
): UseMutationResult<AcceptedJob, unknown, string> {
  return useMutation({
    mutationFn: cancelJob,
    onSuccess: () => onCancelled?.(),

    // ⛔ Причина показується кодом, а не «щось пішло не так»: `409`
    // (`ECR-JOB-0409`) означає «задача вже завершилась» — тобто кнопку треба
    // сховати, а не повторити спробу.
    onError: showApiError,
  });
}
