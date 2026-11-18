import { apiFetch } from '@/api/client';
import type {
  FormatConstResultDto,
  MethodologySubstancesDto,
  MethodologyScriptDto,
  PreviewScriptRequest,
  SaveScriptRequest,
  ScriptPreviewDto,
  ScriptTextRequest,
  ScriptValidationDto,
} from './types';

/*
 * ⛔ Адреса в кожній функції записана повністю (разом із методом): сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в клієнті саме
 * літерали `/api/v1/…`, а не збираний з помічника префікс.
 */

const JsonHeaders = { 'Content-Type': 'application/json' } as const;

/** Скрипт версії: текст, діагностика, схема. */
export function methodologyScript(
  methodologyId: number,
  versionId: number,
): Promise<MethodologyScriptDto> {
  return apiFetch<MethodologyScriptDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/script`,
  );
}

/**
 * Зберігає текст. Лише чернетка зі `Script`-рушієм; `expectedHash` ловить
 * правку з іншої вкладки (`409 ECR-CALC-0409.scriptChanged`).
 */
export function saveMethodologyScript(
  methodologyId: number,
  versionId: number,
  body: SaveScriptRequest,
): Promise<MethodologyScriptDto> {
  return apiFetch<MethodologyScriptDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/script`,
    { method: 'PUT', headers: JsonHeaders, body: JSON.stringify(body) },
  );
}

/** Перевірка тексту без збереження. */
export function validateMethodologyScript(
  methodologyId: number,
  versionId: number,
  body: ScriptTextRequest,
  signal?: AbortSignal,
): Promise<ScriptValidationDto> {
  return apiFetch<ScriptValidationDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/script/validate`,
    {
      method: 'POST',
      headers: JsonHeaders,
      body: JSON.stringify(body),
      ...(signal === undefined ? {} : { signal }),
    },
  );
}

/** Пробний розрахунок на документі; нічого не записує. */
export function previewMethodologyScript(
  methodologyId: number,
  versionId: number,
  body: PreviewScriptRequest,
): Promise<ScriptPreviewDto> {
  return apiFetch<ScriptPreviewDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/script/preview`,
    { method: 'POST', headers: JsonHeaders, body: JSON.stringify(body) },
  );
}

/** Канонічне форматування літерала `const`-мапи сервером (`text = null` — не розібрано). */
export function formatMethodologyScriptConst(
  methodologyId: number,
  versionId: number,
  body: ScriptTextRequest,
): Promise<FormatConstResultDto> {
  return apiFetch<FormatConstResultDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/script/format-const`,
    { method: 'POST', headers: JsonHeaders, body: JSON.stringify(body) },
  );
}

/** Склад речовин версії: код речовини → Id запису довідника (для `OUT@<id>` у тестах). */
export function methodologyScriptSubstances(
  methodologyId: number,
  versionId: number,
): Promise<MethodologySubstancesDto> {
  return apiFetch<MethodologySubstancesDto>(
    `/api/v1/methodologies/${String(methodologyId)}/versions/${String(versionId)}/substances`,
  );
}
