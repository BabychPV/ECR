/**
 * ✎ debt-1109 x10: «Details» журналу безпеки показував сирий JSON (`{"roleId":7,"added":["Doc.Read"]}`). Загальний
 * читабельний показ без знання кожної події: пари «ключ: значення» через « · », вкладене — через крапку в ключі,
 * масиви — через кому. Не JSON-об'єкт (рядок, число, зіпсований текст) — як є: сире чесніше за вгаданий переклад.
 * Точне значення лишається в `title` комірки.
 */
export function auditDetailsText(raw: string | null | undefined): string {
  if (raw === null || raw === undefined || raw.trim() === '') return '—';

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return raw;
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return raw;

  const parts: string[] = [];
  flatten(parsed as Record<string, unknown>, '', parts);
  return parts.length === 0 ? '—' : parts.join(' · ');
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
