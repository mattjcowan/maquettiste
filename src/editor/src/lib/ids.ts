import { monotonicFactory } from "ulid";

const next = monotonicFactory();

/** A new uppercase Crockford ULID for an element or sub-element (PD17). */
export function newId(): string {
  return next();
}

export const ULID_PATTERN = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;
export const JOB_GROUP_PATTERN = /^job:[0-7][0-9A-HJKMNP-TV-Z]{25}$/;
