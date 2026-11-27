# billing-projected (temporary)

The billing fixture as it was before 0.29.0: tables laid out by convention, an entity mapping and an overlay. Only test
classes marked `[UsesProjectedFixtures]` (they test projection itself) and `PackRepo.BillingDialects()` read it. **Pull request 3
of retiring projection deletes this folder** (docs/engineering/retire-projection-design.md section 9).
