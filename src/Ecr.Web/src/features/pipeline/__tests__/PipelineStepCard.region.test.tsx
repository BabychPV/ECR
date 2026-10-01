import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { PipelineStepCard } from '@/features/pipeline/PipelineStepCard';
import { renderWithMantine } from '@/test/render';

/**
 * Крок конвеєра — іменована область (WCAG 1.3.1, 2.4.1): читач пропонує кроки в переліку областей за назвою.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати `aria-labelledby` із `<section>` → червоний
 * (безіменна `section` не має ролі `region`).
 */
describe('PipelineStepCard: область з іменем', () => {
  it('кожен крок — region з іменем свого заголовка', () => {
    renderWithMantine(
      <>
        <PipelineStepCard step={{ key: 'collect', state: 'ok', points: 3 }} index={3} title="Collect" hint="h" />
        <PipelineStepCard step={{ key: 'map', state: 'zero', points: 0 }} index={4} title="Map" hint="h" />
      </>,
    );

    expect(screen.getByRole('region', { name: '3. Collect' })).toBeDefined();
    expect(screen.getByRole('region', { name: '4. Map' })).toBeDefined();
  });
});
