import type { CampaignProgress, CampaignProject } from '@/features/campaign/api';

/**
 * Подання серверної класифікації кампанії на екрані.
 *
 * ⛔ Тут НЕМАЄ жодного правила «хто затримує» чи «скільки чого»: класифікацію
 * (`progress`) і підсумки (`totals`) рахує СЕРВЕР (`CampaignProgressRule`), і
 * визначення в системі одне. Клієнтська копія правила вже була (`isLagging`,
 * `campaignTotals`) і розійшлася з серверною двічі: вона не знала строку
 * подання взагалі, а підсумки складала по обрізаному стелею переліку.
 */

/**
 * Класи, що ЗАТРИМУЮТЬ кампанію, у порядку показу: спершу прострочені, далі
 * ті, в кого до строку лишилося мало. `InProgress` і `Done` не затримують.
 */
const HoldingUp: readonly CampaignProgress[] = ['Overdue', 'AtRisk'];

/** Чи затримує проєкт кампанію — за класом, який назвав сервер. */
export function isHoldingUp(project: CampaignProject): boolean {
  return HoldingUp.includes(project.progress);
}

/** Ранг класу для впорядкування переліку: менший — вище. */
export function holdingUpRank(progress: CampaignProgress): number {
  const at = HoldingUp.indexOf(progress);

  return at === -1 ? HoldingUp.length : at;
}

/**
 * Перелік «хто затримує»: лише `Overdue` і `AtRisk`, прострочені вгорі.
 *
 * ⚠ Сортування стабільне: усередині класу лишається порядок сервера (за кодом
 * проєкту).
 */
export function holdingUp(projects: readonly CampaignProject[]): CampaignProject[] {
  return projects
    .filter(isHoldingUp)
    .map((project, index) => ({ project, index }))
    .sort((a, b) => holdingUpRank(a.project.progress) - holdingUpRank(b.project.progress) || a.index - b.index)
    .map(({ project }) => project);
}

/**
 * Момент ISO-8601 з ЯВНИМ зсувом. Складники — годинник того поясу, у якому
 * момент записано.
 */
const Moment = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})$/;

const DayMs = 24 * 60 * 60 * 1000;

/**
 * Останній день подання — календарна дата `YYYY-MM-DD` у поясі ПРОЄКТУ.
 *
 * `submissionDeadline` — момент, З ЯКОГО проєкт уже прострочений (виключна
 * межа, опівніч `PeriodEnd + GraceOffsetDays`). Тому останній день — доба
 * ПЕРЕД ним: `2026-06-16T00:00:00+05:00` → `2026-06-15`.
 *
 * ⛔ Дата рахується за годинником ЗСУВУ з рядка, а не поясу браузера. Через
 * `new Date(...)` той самий рядок у Києві чи UTC — це ще 15 червня 19:00/22:00,
 * і «мінус доба» дала б 14 червня: користувачеві на заході від майданчика
 * строк показувався б на день раніше, ніж він є.
 *
 * `null` — межу ще не пораховано (або рядок не розібрався); екран каже
 * «строк не визначено», а не вигадує дату.
 */
export function lastSubmissionDay(deadline: string | null): string | null {
  if (deadline === null) return null;

  const parts = Moment.exec(deadline);
  if (parts === null) return null;

  // Годинник поясу проєкту, записаний «як UTC»: лише щоб відняти добу й
  // прочитати календарні складники, не зсуваючи їх поясом браузера.
  const wall = Date.UTC(
    Number(parts[1]),
    Number(parts[2]) - 1,
    Number(parts[3]),
    Number(parts[4]),
    Number(parts[5]),
    Number(parts[6] ?? '0'),
  );

  const day = new Date(wall - DayMs);
  if (Number.isNaN(day.getTime())) return null;

  const pad = (value: number): string => String(value).padStart(2, '0');

  return `${String(day.getUTCFullYear())}-${pad(day.getUTCMonth() + 1)}-${pad(day.getUTCDate())}`;
}
