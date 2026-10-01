import type { RegistryDefDto, RegistryDefinitionDto } from '@/api/types';
import { childSumRules, compositionOf, type ChildSumRule, type CompositionLink } from './composition';

/** Найглибший рівень, який показує редактор (корінь — рівень 0). */
export const MaxCompositionDepth = 4;

/** Вузол дерева композиції: довідник, його зв'язок до батька й частини. */
export interface CompositionNode {
  readonly definition: RegistryDefinitionDto;
  /** Зв'язок до батька; `null` — корінь редактора. */
  readonly link: CompositionLink | null;
  /** Правила «Сума дочірніх» батька, що рахують САМЕ цей довідник. */
  readonly sums: readonly ChildSumRule[];
  readonly children: readonly CompositionNode[];
}

/**
 * Дерево композиції вниз від кореня: хто є частиною кого (`ФВ-8.16`, «ланцюжок знаходиться сам»,
 * FEATURE-REGISTRY-TABLES §8.4).
 *
 * ⚠ Сервер не віддає вхідних зв'язків довідника, тож кандидати в частини — довідники з переліку,
 * у яких є поле `Lookup` на цей; їхні описи читаються, і лишаються ті, чий зв'язок до цього —
 * саме `Composition`, а не `Reference`. Прямий ендпоінт «частини довідника» зекономив би ці
 * запити — названо у звіті лінії як те, чого бракує серверу.
 *
 * ⛔ Обхід обмежений глибиною і множиною відвіданих: кіл композиції сервер не допускає, але дані
 * повз опис (масовий імпорт) можуть їх мати, і вічний цикл запитів гірший за неповне дерево.
 *
 * @param root Опис кореня.
 * @param registries Перелік усіх довідників.
 * @param load Читає опис довідника за кодом.
 */
export async function buildCompositionTree(
  root: RegistryDefinitionDto,
  registries: readonly RegistryDefDto[],
  load: (code: string) => Promise<RegistryDefinitionDto>,
): Promise<CompositionNode> {
  const visited = new Set<number>([root.id]);

  async function walk(definition: RegistryDefinitionDto, link: CompositionLink | null, sums: readonly ChildSumRule[], depth: number): Promise<CompositionNode> {
    if (depth >= MaxCompositionDepth) return { definition, link, sums, children: [] };

    const candidates = registries.filter(
      (registry) =>
        !visited.has(registry.id)
        && registry.fields.some((field) => field.dataType === 'Lookup' && field.lookupRegistryDefId === definition.id),
    );

    const parentSums = childSumRules(definition);
    const loaded = await Promise.all(candidates.map((candidate) => load(candidate.code)));
    const children: CompositionNode[] = [];

    for (const child of loaded) {
      const childLink = compositionOf(child);
      if (childLink?.parentRegistryDefId !== definition.id || visited.has(child.id)) continue;

      visited.add(child.id);
      children.push(
        await walk(child, childLink, parentSums.filter((rule) => rule.child === child.code), depth + 1),
      );
    }

    children.sort((a, b) => a.definition.code.localeCompare(b.definition.code));
    return { definition, link, sums, children };
  }

  return walk(root, null, [], 0);
}
