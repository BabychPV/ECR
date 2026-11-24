/**
 * Спільні умовчання кожної сітки `<RevoGrid>` (сітка документа й стенди перевірок).
 *
 * ✎ Рішення людини 24.11 («прибери цей атрибут також»): позначку «RevoGrid» під сіткою (посилання
 * rv-grid.com/guide/attribution, «Made with ❤ by Revolist OU Team») прибирає штатна властивість бібліотеки
 * `hideAttribution` (@revolist/revogrid 4.11, ліцензія MIT — зміна й використання без обмежень), а не CSS.
 * ⚠ Коментар у типах бібліотеки просить ховати позначку лише з підпискою Pro — це прохання, не умова ліцензії MIT.
 */
export const RevoGridDefaults = { hideAttribution: true } as const;
