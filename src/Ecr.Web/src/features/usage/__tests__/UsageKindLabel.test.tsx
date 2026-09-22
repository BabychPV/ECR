import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { UsageKindLabel } from '@/features/usage/UsageKindLabel';
import { testTheme } from '@/test/render';

/**
 * Спільна назва виду залежного (`usageKind.*`) для довідників і одиниць.
 *
 * ⚠ Каталог не завантажений: `t()` повертає ключ у `⟦…⟧`, тож перевіряється
 * саме ключ, який просить гілка.
 *
 * ⚠ Перелік — копія `UsageKinds.All` на сервері. Що він не відстає від сервера,
 * стереже `UsageKindCatalogTests` (архітектурні тести); тут — що кожна гілка
 * просить СВІЙ ключ, а не сусідній.
 */
const Kinds = [
  'templateColumn',
  'registryField',
  'methodologySubstance',
  'sourceEntity',
  'methodologyConstant',
  'methodologyFormula',
  'methodologyOutput',
  'fieldMap',
  'unitConversion',
  'derivedUnit',
  'dimensionBase',
  'data',
  // ФВ-8.14: «де використано» константи методики й колонки шаблону.
  'templateFormula',
  'calculationBinding',
  'methodologyRule',
  'methodologyRequiredInput',
] as const;

function label(kind: string): HTMLElement {
  const { container } = render(
    <MantineProvider theme={testTheme}>
      <span data-testid="kind">
        <UsageKindLabel kind={kind} />
      </span>
    </MantineProvider>,
  );
  const span = container.querySelector('[data-testid="kind"]');
  if (span === null) throw new Error('немає обгортки');
  return span as HTMLElement;
}

describe('UsageKindLabel', () => {
  it.each(Kinds)('%s — ключ каталогу usageKind.<вид>', (kind) => {
    const span = label(kind);
    expect(span.textContent).toBe(`⟦usageKind.${kind}⟧`);
    expect(span.querySelector('code')).toBeNull();
  });

  it('невідомий вид — сире значення в <code>, не порожньо й не вигадана назва', () => {
    const span = label('futureThing');
    expect(span.querySelector('code')?.textContent).toBe('futureThing');
    expect(span.textContent).not.toContain('usageKind');
  });
});
