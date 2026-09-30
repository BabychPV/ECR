import { useState, type JSX } from 'react';
import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryFields, RegistryRules } from '../RegistryConstructor';
import { emptyField, emptyRule, type FieldDraft, type RuleDraft } from '../definition';

/**
 * Конструктор довідника з клавіатури (WCAG 2.4.3, 2.4.6): після «Додати поле/правило» фокус — у новому
 * рядку, після «Прибрати» — на «Додати», а кнопки «Прибрати» розрізняються доступним ім'ям.
 *
 * ⛔ Мутаційні докази (перевірено руками 2026-09-30): прибрати `focus.added()` з «Додати поле» → червоний
 * «нове поле»; прибрати `focus.removed()` з «Прибрати» → червоний «прибирання»; прибрати `aria-label` з
 * «Прибрати» → червоний «різні імена»; прибрати `focus.added()` з «Додати правило» → червоний «правило».
 */
const Definition = { id: 1, code: 'R', fields: [], rules: [], mappings: [] } as unknown as RegistryDefinitionDto;

function FieldsStand({ initial = [] }: { readonly initial?: readonly FieldDraft[] }): JSX.Element {
  const [drafts, setDrafts] = useState<readonly FieldDraft[]>(initial);

  return (
    <MantineProvider>
      <RegistryFields
        definition={Definition}
        canEdit
        newFields={drafts}
        registryOptions={[]}
        onAddField={() => setDrafts((current) => [...current, emptyField()])}
        onChangeField={(index, next) => setDrafts((current) => current.map((d, i) => (i === index ? next : d)))}
        onRemoveField={(index) => setDrafts((current) => current.filter((_, i) => i !== index))}
      />
    </MantineProvider>
  );
}

function RulesStand(): JSX.Element {
  const [rules, setRules] = useState<readonly RuleDraft[]>([]);

  return (
    <MantineProvider>
      <RegistryRules
        rules={rules}
        canEdit
        onChange={(index, next) => setRules((current) => current.map((r, i) => (i === index ? next : r)))}
        onAdd={() => setRules((current) => [...current, emptyRule('Expression')])}
      />
    </MantineProvider>
  );
}

describe('Конструктор довідника — фокус і імена', () => {
  it('нове поле: фокус у полі «Код» доданого рядка', () => {
    render(<FieldsStand />);
    fireEvent.click(screen.getByRole('button', { name: /registries\.addField/ }));

    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: /registries\.code/ }));
  });

  it('прибирання: фокус на «Додати поле», а не на <body>', () => {
    render(<FieldsStand initial={[{ ...emptyField(), code: 'Owner' }]} />);
    const remove = screen.getByRole('button', { name: /registries\.removeField.*Owner/ });
    remove.focus();
    fireEvent.click(remove);

    expect(document.activeElement).toBe(screen.getByRole('button', { name: /registries\.addField/ }));
  });

  it('різні імена: кнопка «Прибрати» несе код поля', () => {
    render(<FieldsStand initial={[{ ...emptyField(), code: 'Owner' }, { ...emptyField(), code: 'Site' }]} />);

    expect(screen.getByRole('button', { name: /registries\.removeField.*: Owner$/ })).toBeDefined();
    expect(screen.getByRole('button', { name: /registries\.removeField.*: Site$/ })).toBeDefined();
  });

  it('правило: фокус у полі «Код правила» доданого рядка', () => {
    render(<RulesStand />);
    fireEvent.click(screen.getByRole('button', { name: /registries\.addRule/ }));

    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: /registries\.ruleCode/ }));
  });
});
