/**
 * Підпис колонки «Rule» панелі зауважень (A2-04).
 *
 * ⚠ Сервер віддає код знахідки Check як `REL-<код зв'язку>`, а клон версії шаблону дописує до коду
 * зв'язку `_v<id нової версії>` (код зв'язку глобально унікальний, `TemplateVersionStore.ClonedRelationCode`).
 * Оператор бачив `REL-CHK_TOT_v5` — службовий префікс і номер версії, жодного з яких немає в шаблоні,
 * який він налаштовував. Показуємо код зв'язку без обох; повний код лишається в підказці.
 *
 * Коди інших правил (`cfg.ValidationRule`, `ECR-…`) — як є: їх суфікс `_vN`, якщо він є, задав автор.
 */
export function ruleLabel(ruleCode: string): string {
  if (!ruleCode.startsWith('REL-')) return ruleCode;

  const relation = ruleCode.slice('REL-'.length).replace(/_v\d+$/, '');
  return relation.length > 0 ? relation : ruleCode;
}
