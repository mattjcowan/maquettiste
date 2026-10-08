// Helpers for the seed-data pack. They run in the Maquettiste sandbox: no CLR, no files, no clock.

// One unit per database.
maquettiste.selector("databases", (model) => model.databases);
