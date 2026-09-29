# IE flat-table regression fixture

`ie-flat-anonymized.txt` preserves the 282-node hierarchy of the supplied diagnostic capture. All patient names, card/reception/internal IDs, memo, times, age, clinic titles and URLs have been replaced with synthetic values or removed. Header and action labels remain to reproduce column detection.

The header table has 14 cells. The sibling `tableList` has 32 direct cells representing two reservations, with no row nodes. Its last header spans the arrival/assignment, hold and guide columns. The first reservation is unassigned; the second contains a numeric memo different from its synthetic patient ID.

The fixture is for parsing only. Tests never invoke live iCall controls.
