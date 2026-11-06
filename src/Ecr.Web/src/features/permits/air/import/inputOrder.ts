/**
 * Order of the parts of an input in the wizard grids (`pi-grid`): the hint goes UNDER the box, so boxes of fields with and
 * without a hint stand on one line (07.11: «many misaligned buttons and fields»).
 */
export const InputOrder: ('label' | 'input' | 'description' | 'error')[] = ['label', 'input', 'description', 'error'];
