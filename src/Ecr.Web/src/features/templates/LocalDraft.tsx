import { useEffect, useId, useRef, useState, type JSX, type ReactNode } from 'react';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';

/**
 * Чернетка діалогу, що живе В САМОМУ діалозі, а не на сторінці.
 *
 * ⛔ Причина — не стиль, а живий дефект (2026-09-23): на
 * `TemplateVersionPage` чернетки всіх діалогів («Add sheet», таблиця,
 * колонка, рядок, формула, правила…) були станом сторінки, і кожне
 * натискання клавіші перерендерювало всю сторінку разом із деревом
 * структури. На версії з 91 таблицею й ~3000 колонок це ~195 тис. волокон
 * на символ — замір на стенді: медіана 5.4 с (dev) і 2.0 с (prod-збірка)
 * від натискання до кадру. Тут стан друку локальний: оновлюється лише цей
 * компонент і форма під ним; сторінка знає тільки «відкрито/закрито»
 * (`initial`) і отримує готову чернетку в обробнику збереження.
 *
 * ⚠ `initial` читається лише під час МОНТУВАННЯ — так само, як `useState`.
 * Щоб підставити нове значення у вже відкритий діалог (наприклад, щойно
 * створене правило), сторінка змінює `key`, а не `initial`.
 *
 * ⚠ Ставити ЗОВНІ `<Suspense>` лінивого редактора, а не всередині: інакше
 * підміна заглушки формою перемонтувала б і скинула б введене.
 *
 * ⚠ UI-36: змінена чернетка — джерело незбережених змін для `UnsavedGuard`
 * (`unsavedSources.ts`). `flush` немає навмисно: форму пише лише її кнопка, тож
 * вихід зі сторінки з відкритою зміненою формою показує діалог, а не тихо
 * губить введене. «Змінена» — будь-який `setDraft` після монтування (форми
 * віддають новий об'єкт на кожну правку); закриття діалогу знімає реєстрацію.
 */
export function LocalDraft<T>({
  initial,
  children,
}: {
  initial: T;
  children: (draft: T, setDraft: (next: T) => void) => ReactNode;
}): JSX.Element {
  const [draft, setDraft] = useState<T>(initial);
  // ⚠ Порівняння з МОНТУВАЛЬНИМ значенням: сторінка може передавати `initial` новим
  // об'єктом на кожен свій рендер (`emptyValidationRuleDraft()`).
  const [start] = useState<T>(initial);
  const dirty = useRef(false);
  dirty.current = draft !== start;
  const id = useId();

  useEffect(() => registerUnsavedSource(`local-draft${id}`, { hasUnsaved: () => dirty.current }), [id]);

  return <>{children(draft, setDraft)}</>;
}
