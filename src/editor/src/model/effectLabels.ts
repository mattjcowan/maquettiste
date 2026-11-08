// What an action sets on the object, and the states a scenario object is in (object-effects-design.md section 10).
export const EFFECT_LABELS = {
  setsColumn: "Sets",
  setsHint: "The values this action sets on the object, each worked out from the object and the context as they were when the action started.",
  none: "(none)",
  setsNoneHint: "This action sets nothing on the object. Use the pencil to make it write a value to one of the object's attributes when it runs.",
  refused: "The change was not accepted, so the dialog stays open. Fix it here, or cancel; the findings are in the Problems panel.",
  editSets: (action: string) => `Edit what ${action} sets`,
  title: (action: string) => `Sets of ${action}`,
  attribute: "Attribute",
  value: "Value",
  pickAttribute: "Pick an attribute",
  addSet: "Add set",
  removeSet: "Remove set",
  noSubjects: "The process has no subject, so there is nothing to set.",
  noAttributes: "No attribute is held by every subject, so there is nothing to set.",
  valueHint: "An expression: it may read the context, the object under subject, now(), current_actor() and in_state(...).",
  valuePlaceholder: "for example current_actor() or now()",
  /** The type line of a condition (a check's, an Only if): it must be true or false, and no answer counts as no. */
  conditionType: (type: string, nullable: boolean) =>
    type === "boolean" || type === "unknown"
      ? `A yes or no condition${nullable ? "; when a value it reads is missing, the answer is no" : ""}`
      : `Type ${type}: a condition must come out true or false`,
  previewType: (type: string, nullable: boolean) => `Type ${type}${nullable ? ", may be null" : ""}`,
  statesColumn: "States",
  statesHint: "The states this object is in, in the lifecycles that can govern it. One configuration per lifecycle.",
  valuesColumn: "Values",
  valuesHint: "The attribute values this object starts with, as name=value pairs. Names must be attributes of its entity.",
  unknownAttributes: (object: string, names: string[]) =>
    `${object}: not an attribute of its entity or not a name=value pair: ${names.join(", ")}. Nothing was saved.`,
  addExpected: "Add expected value",
  addExpectedHint: "Expect the object to hold a value after this step",
  expectedAttribute: "Attribute",
  expectedValue: "Value",
  noExpectable: "The subject object has no attribute left to expect, or the scenario has no subject object yet.",
  expectedValues: "Expected values",
  expectedValuesHint: "The values of the object this step changes. Filled by Update from replay.",
} as const;
