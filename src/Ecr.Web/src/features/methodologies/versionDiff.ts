import type { MethodologyDraftVersionDto } from '@/api/types';
import type { MethodologyVersionDiffDto } from '@/features/methodologies/api';

/**
 * Рішення порівняння версій методології (`BE-25`), які не є розміткою.
 *
 * ⚠ Сервер порівнює рівно три набори — формули, константи, тест-кейси
 * (`CompareMethodologyVersionsHandler`). Правил відбору, прив'язок і режимів
 * у відповіді НЕМАЄ, тож і тут для них немає ні групи, ні підпису.
 */
export type MethodologyDiffItem = MethodologyVersionDiffDto['items'][number];

/** Групи в порядку показу — той самий, у якому їх віддає сервер. */
export const DiffKinds = ['Formula', 'Constant', 'TestCase'] as const;

export type DiffKind = (typeof DiffKinds)[number];

/**
 * Базова версія за замовчуванням для порівняння з `targetId`.
 *
 * ⚠ Рішення: найближча ПОПЕРЕДНЯ опублікована, інакше просто попередня.
 * Опублікована — це те, що рахує зараз, тож порівняння чернетки з нею
 * відповідає на питання «що зміниться, якщо це опублікувати».
 *
 * ⚠ «Попередня» — за `id`, а не за `versionNumber`: номер — вільний рядок
 * (`2026.1`, `2026.10`, `v2`), і лексичне порівняння дало б хибний порядок;
 * `id` видається сервером за порядком створення.
 *
 * ⚠ Для найстарішої версії попередньої немає — тоді найближча наступна
 * (спершу опублікована), щоб «Порівняти» не вело в порожній діалог.
 */
export function defaultBaseVersion(
  versions: readonly MethodologyDraftVersionDto[],
  targetId: number,
): MethodologyDraftVersionDto | undefined {
  const others = versions.filter((version) => version.id !== targetId);

  const earlier = others.filter((version) => version.id < targetId).sort((a, b) => b.id - a.id);
  const later = others.filter((version) => version.id > targetId).sort((a, b) => a.id - b.id);

  return (
    earlier.find((version) => version.status === 'Published') ??
    earlier[0] ??
    later.find((version) => version.status === 'Published') ??
    later[0]
  );
}

/** Відмінності, розкладені за наборами; порядок усередині — як у сервера. */
export function groupDiff(
  items: readonly MethodologyDiffItem[],
): Readonly<Record<DiffKind, readonly MethodologyDiffItem[]>> {
  return {
    Formula: items.filter((item) => item.kind === 'Formula'),
    Constant: items.filter((item) => item.kind === 'Constant'),
    TestCase: items.filter((item) => item.kind === 'TestCase'),
  };
}
