import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import {
  canonicalKey,
  KEY_SEPARATOR,
  normalizeKeyPart,
  type KeyPart,
  type KeyPartType,
} from '@/features/registries/keys/normalizeKey';

/**
 * Клієнтський нормалізатор ключа на спільній фікстурі (FEATURE-REGISTRY-TABLES
 * §4.2, RT-02).
 *
 * ⛔ Читається САМЕ той файл, що й серверним `RegistryKeyNormalizerTests.cs`.
 * Копія випадків тут розійшлася б із серверною при першій правці — і обидва
 * набори лишалися б зеленими.
 */
interface FixtureCase {
  id: string;
  ignoreCase: boolean;
  parts: [KeyPartType, string | number | boolean | null][];
  canonical: string[] | null;
}

interface Fixture {
  cases: FixtureCase[];
  same: string[][];
  distinct: string[][];
  sha256: Record<string, string>;
}

const fixture = JSON.parse(
  readFileSync(
    resolve(__dirname, '../../../../../../../tests/Ecr.TestKit/Fixtures/registry-key-normalization.json'),
    'utf-8',
  ),
) as Fixture;

const byId = new Map(fixture.cases.map((c) => [c.id, c]));

function normalize(testCase: FixtureCase): string | null {
  const parts: KeyPart[] = testCase.parts.map(([type, value]) => ({ type, value }));
  return canonicalKey(parts, testCase.ignoreCase);
}

function normalizeId(id: string): string | null {
  const testCase = byId.get(id);
  if (testCase === undefined) throw new Error(`У фікстурі немає випадку «${id}».`);
  return normalize(testCase);
}

const sha256 = (text: string) => createHash('sha256').update(text, 'utf8').digest('hex');

describe('normalizeKey — спільна фікстура з сервером', () => {
  it('фікстура повна: ≥ 40 випадків, ідентифікатори унікальні', () => {
    expect(fixture.cases.length).toBeGreaterThanOrEqual(40);
    expect(byId.size).toBe(fixture.cases.length);
  });

  it.each(fixture.cases.map((c) => [c.id, c] as const))('«%s»', (_id, testCase) => {
    const actual = normalize(testCase);
    const expected = testCase.canonical === null ? null : testCase.canonical.join(KEY_SEPARATOR);

    expect(actual).toBe(expected);

    // Хеш клієнт не рахує (див. normalizeKey.ts), але його канонічний рядок
    // мусить дати РІВНО той хеш, який сервер покладе в `KeyHash`.
    if (actual !== null) expect(sha256(actual)).toBe(fixture.sha256[testCase.id]);
  });

  it('групи same дають один ключ, а distinct — різні', () => {
    for (const group of fixture.same) {
      const keys = group.map(normalizeId);
      expect(keys).not.toContain(null);
      expect(new Set(keys).size, group.join(' | ')).toBe(1);
    }
    for (const group of fixture.distinct) {
      const keys = group.map(normalizeId);
      expect(keys).not.toContain(null);
      expect(new Set(keys).size, group.join(' | ')).toBe(keys.length);
    }
  });
});

describe('normalizeKey — входи, яких немає у фікстурі', () => {
  it('числа приймаються й числом, і bigint, без втрати знаків', () => {
    expect(normalizeKeyPart('Decimal', -0)).toBe('N:0');
    expect(normalizeKeyPart('Decimal', 1e21)).toBe('N:1000000000000000000000');
    expect(normalizeKeyPart('Int', 9007199254740993n)).toBe('N:9007199254740993');
    expect(normalizeKeyPart('Lookup', '00162')).toBe('L:162');
  });

  it('значення не того виду відхиляється, а не мовчки стає іншим ключем', () => {
    expect(() => normalizeKeyPart('Decimal', '1,5')).toThrow(TypeError);
    expect(() => normalizeKeyPart('Decimal', '1e5000')).toThrow(TypeError);
    expect(() => normalizeKeyPart('Decimal', Number.NaN)).toThrow(TypeError);
    expect(() => normalizeKeyPart('Date', '2026-02-30')).toThrow(TypeError);
    expect(() => normalizeKeyPart('Date', '27.09.2026')).toThrow(TypeError);
    expect(() => normalizeKeyPart('Bool', 'true')).toThrow(TypeError);
    expect(() => normalizeKeyPart('Lookup', 1.5)).toThrow(TypeError);
    expect(() => normalizeKeyPart('String', 42)).toThrow(TypeError);
    expect(() => canonicalKey([])).toThrow(RangeError);
  });

  it('undefined — те саме, що null: частини немає', () => {
    expect(canonicalKey([{ type: 'String', value: 'a' }, { type: 'Lookup', value: undefined }])).toBeNull();
  });
});
