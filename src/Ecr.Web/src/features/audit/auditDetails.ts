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

function scalar(item: unknown): string {
  if (item === null || item === undefined) return '—';
  if (Array.isArray(item)) return item.length === 0 ? '—' : item.map((x) => scalar(x)).join(', ');
  if (typeof item === 'object') return JSON.stringify(item);
  return String(item);
}
