import type { Page } from '@playwright/test';

type AxeRun = { violations: { id: string; nodes: { target: unknown[] }[] }[] };

/**
 * Справжній axe-core у браузері (скрипт з node_modules, без нових пакетів): порушення одного правила на поточній сторінці.
 * `include` — CSS-селектор області перевірки (без нього — увесь документ).
 */
export async function axeViolations(page: Page, rule: string, include?: string): Promise<string[]> {
  /*
   * ⛔ Спершу — кінець усіх скінченних CSS-переходів і анімацій (`document.getAnimations()`). Діалог Mantine
   * з'являється переходом прозорості (~200 мс), а `toBeVisible` проходить уже на першому кадрі: axe посеред переходу
   * рахує контраст із неповною непрозорістю й «бачить» порушення там, де в спокої його немає (пачка b12, run
   * 45534979498: `#mantine-…-description` діалогу «New draft version» — лише на швидкому раннері CI).
   * Нескінченні анімації (спінери) не чекаємо.
   */
  await page.evaluate(async () => {
    const finite = document.getAnimations().filter((animation) => {
      const end = animation.effect?.getComputedTiming().endTime;
      return typeof end === 'number' && Number.isFinite(end);
    });
    await Promise.allSettled(finite.map((animation) => animation.finished));
  });
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(
    async ([id, scope]) => {
      const axe = (window as unknown as { axe: { run: (c: unknown, o: object) => Promise<AxeRun> } }).axe;
      const result = await axe.run(scope === null ? document : { include: [[scope]] }, { runOnly: { type: 'rule', values: [id] } });

      return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
    },
    [rule, include ?? null] as const,
  );
}
