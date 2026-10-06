/**
 * Пункти командної палітри без даних (UI-30): екрани й дії оболонки.
 *
 * ⚠ Екрани приходять готовим переліком із `AppLayout` — тим самим
 * `visibleNavGroups`, з якого малюється меню (групи UI-12, фільтр
 * `canAccessRoute`). Палітра нічого не вирішує про права сама: пункт, якого
 * немає в меню, не з'являється й тут, і навпаки.
 *
 * ⛔ Жодних лічильників і жодних даних, яких не повернув сервер: дані
 * (документи, шаблони, довідники) — лише відповідь `GET /api/v1/search`, а
 * сервер уже звузив її областю видимості користувача (прихований аркуш, його
 * стан і лічильники палітра не бачить і не вигадує).
 */

/** Один екран меню — рівно ті поля маршруту, які палітра читає. */
export interface PaletteScreen {
  readonly path: string;
  readonly handle: { readonly labelKey: string; readonly icon?: string | undefined };
}

/** Група меню (`navGroups`) з уже відфільтрованими за правами пунктами. */
export interface PaletteScreenGroup {
  readonly id: string;
  readonly labelKey: string;
  readonly items: readonly PaletteScreen[];
}

/** Рядок палітри будь-якого виду; `id` — сталий між перемальовуваннями. */
export interface CommandItem {
  readonly id: string;
  readonly section: 'screens' | 'actions';
  readonly title: string;
  /** Підпис другим планом (група меню); бере участь у пошуку. */
  readonly hint: string;
  readonly icon?: string | undefined;
  readonly run: () => void;
}

/** Скільки рядків одного розділу показувати, коли є запит (макет: 6). */
export const PerSectionWhileTyping = 6;

/** Слова запиту в нижньому регістрі; порожній запит — порожній перелік. */
export function queryTokens(query: string): string[] {
  return query.trim().toLocaleLowerCase().split(/\s+/).filter((token) => token.length > 0);
}

/**
 * Рядки, де є КОЖНЕ слово запиту (у назві або підписі), не більше
 * `PerSectionWhileTyping` на розділ. Порожній запит — усі рядки як є.
 */
export function matchCommands(items: readonly CommandItem[], query: string): CommandItem[] {
  const tokens = queryTokens(query);
  if (tokens.length === 0) return [...items];

  const perSection = new Map<string, number>();
  const matched: CommandItem[] = [];

  for (const item of items) {
    const haystack = `${item.title} ${item.hint}`.toLocaleLowerCase();
    if (!tokens.every((token) => haystack.includes(token))) continue;

    const taken = perSection.get(item.section) ?? 0;
    if (taken >= PerSectionWhileTyping) continue;
    perSection.set(item.section, taken + 1);
    matched.push(item);
  }

  return matched;
}
