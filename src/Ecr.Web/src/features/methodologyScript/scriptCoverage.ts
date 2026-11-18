import type { MethodologyCoverageDto } from '@/features/methodologies/api';
import type { ScriptOutlineDto } from './types';

/**
 * Виходи скрипта без активної прив'язки до колонки — за наявним ендпоінтом
 * покриття версії (`GET …/coverage`): вихід без прив'язок, або якого покриття
 * ще не знає (скрипт не збережено чи без діагностик).
 */
export function outputsWithoutColumn(
  outline: ScriptOutlineDto | null,
  coverage: Pick<MethodologyCoverageDto, 'outputs'> | undefined,
): ReadonlySet<string> {
  if (outline === null || coverage === undefined) return new Set();

  const bound = new Set(
    coverage.outputs.filter((item) => item.bindings.length > 0).map((item) => item.code),
  );
  return new Set(outline.outputs.map((output) => output.code).filter((code) => !bound.has(code)));
}
