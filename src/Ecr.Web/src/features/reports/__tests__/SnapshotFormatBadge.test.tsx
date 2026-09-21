import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SnapshotFormatBadge, snapshotHashFormat } from '@/features/reports/SnapshotFormatBadge';
import { testTheme } from '@/test/render';

/**
 * Поле `hashFormat` у `schema.d.ts` — необов'язковий `string`, не union: нове
 * значення сервера компілятор не зловить. Тут — що клієнт робить із тим, чого
 * не знає.
 */
describe('snapshotHashFormat', () => {
  it('три значення сервера проходять як є', () => {
    expect(snapshotHashFormat('current')).toBe('current');
    expect(snapshotHashFormat('legacy')).toBe('legacy');
    expect(snapshotHashFormat('unknown')).toBe('unknown');
  });

  it('відсутнє поле й незнайомий рядок — unknown, а не «новий формат»', () => {
    expect(snapshotHashFormat(undefined)).toBe('unknown');
    expect(snapshotHashFormat('v3')).toBe('unknown');
    // Регістр не вгадується: сервер пише константи малими літерами.
    expect(snapshotHashFormat('Legacy')).toBe('unknown');
  });
});

describe('SnapshotFormatBadge', () => {
  function badge(format: string | undefined): HTMLElement | null {
    const { container } = render(
      <MantineProvider theme={testTheme}>
        <SnapshotFormatBadge format={format} />
      </MantineProvider>,
    );

    return container.querySelector<HTMLElement>('[data-hash-format]');
  }

  it('незнайомий рядок сервера малюється як «формат невідомий»', () => {
    expect(badge('v3')?.getAttribute('data-hash-format')).toBe('unknown');
  });

  it('позначка не стискається нижче власного тексту', () => {
    expect(badge('legacy')?.style.minWidth).toBe('fit-content');
  });
});
