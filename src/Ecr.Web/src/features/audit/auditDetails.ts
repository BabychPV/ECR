import { jobKindLabel } from '@/features/workflow/jobLabel';

/**
 * ✎ debt-1109 x10: «Details» журналу безпеки показував сирий JSON (`{"roleId":7,"added":["Doc.Read"]}`). Загальний
 * читабельний показ без знання кожної події: пари «ключ: значення» через « · », вкладене — через крапку в ключі,
 * масиви — через кому. Не JSON-об'єкт (рядок, число, зіпсований текст) — як є: сире чесніше за вгаданий переклад.
 * Точне значення лишається в `title` комірки.
 */
export function auditDetailsText(raw: string | null | undefined): string {
  return auditDetails(raw).text;
}

/** Подробиці події: читабельний текст і технічні ідентифікатори, винесені з нього (`auditDetails`). */
export interface AuditDetailsView {
  readonly text: string;
  readonly technical: readonly string[];
}

/** `jobId` задачі: `Тип-GUID32` або `Тип~ціль~GUID32` (`QuartzJobScheduler`). */
const JobIdPattern = /^([A-Za-z]\w*)(?:-[0-9a-fA-F]{32}|~[^~]*~[0-9a-fA-F]{32})$/;

/** Непрозорий ідентифікатор: GUID (з дефісами чи без). */
const OpaqueIdPattern = /^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$/;

/**
 * ✎ P-1124-4: «Details» без сирих ідентифікаторів у головному тексті. Живий екран показував
 * «jobId: IConsistencyCheckJob-9e3d… · reason: oji» — людина читає назву задачі, а не ім'я класу з GUID.
 *
 * ⚠ Верхній рівень: `jobId` задачі → «job: <вид задачі мовою інтерфейсу>» (`jobKindLabel`); значення-GUID (`…Id`,
 * `correlationId`) прибираються з тексту. Обидва ідентифікатори не губляться — вони в `technical` (другий,
 * приглушений рядок комірки) і в `title` (повний JSON). Решта — як `auditDetailsText` раніше.
 */
export function auditDetails(raw: string | null | undefined): AuditDetailsView {
  if (raw === null || raw === undefined || raw.trim() === '') return { text: '—', technical: [] };

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { text: raw, technical: [] };
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return { text: raw, technical: [] };

  const technical: string[] = [];
  const readable: Record<string, unknown> = {};

  for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
    if (typeof value === 'string') {
      const job = JobIdPattern.exec(value);
      if (job !== null && /id$/i.test(key)) {
        readable[key.replace(/Id$/i, '')] = jobKindLabel(job[1]!);
        technical.push(value);
        continue;
      }

      if (OpaqueIdPattern.test(value)) {
        technical.push(value);
        continue;
      }
    }

    readable[key] = value;
  }

  const parts: string[] = [];
  flatten(readable, '', parts);
  return { text: parts.length === 0 ? '—' : parts.join(' · '), technical };
}

function flatten(value: Record<string, unknown>, prefix: string, parts: string[]): void {
  for (const [key, item] of Object.entries(value)) {
    const name = prefix === '' ? key : `${prefix}.${key}`;
    if (item !== null && typeof item === 'object' && !Array.isArray(item)) {
      flatten(item as Record<string, unknown>, name, parts);
    } else {
      parts.push(`${name}: ${scalar(item)}`);
    }
  }
}

/**
 * ✎ debt-1110: масив ОБ'ЄКТІВ (подія `RegistryValueChanged`: `changes: [{field, oldValue, newValue}, …]`) давав сирий
 * JSON кожного елемента. Тепер будь-яка глибина — тим самим читабельним видом: об'єкт у масиві — `(ключ: значення, …)`,
 * такі елементи — через «; », прості значення — як і раніше через кому.
 */
function scalar(item: unknown): string {
  if (item === null || item === undefined) return '—';
  if (Array.isArray(item)) {
    if (item.length === 0) return '—';
    const hasObjects = item.some((x) => x !== null && typeof x === 'object');
    return item.map((x) => scalar(x)).join(hasObjects ? '; ' : ', ');
  }
  if (typeof item === 'object') {
    const entries = Object.entries(item as Record<string, unknown>);
    return entries.length === 0 ? '—' : `(${entries.map(([key, value]) => `${key}: ${scalar(value)}`).join(', ')})`;
  }
  return String(item);
}
