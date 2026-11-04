// The runtime of Maquettiste's expression language (next-phase-3-design.md section 3.1, lowering). Lowered expressions
// call it as `mq`; it implements SQL's semantics, so a lowered check answers as the PostgreSQL CHECK does on the same row.
// Plain modern JavaScript: no dependency, no locale, no Date arithmetic.
//
// Values: null (missing and null are one value); booleans; strings; integers and decimals as exact scaled BigInts (Dec);
// doubles as numbers; dates (Day), times (Time) and timestamps (Ts, microseconds in UTC); uuids as lower-case strings;
// JSON documents (Json). Numbers cross in as their exact text: a string, or the object { "$mq.number": text } the
// engine passes; a plain number is read through its shortest text. A JSON value crosses in as itself.
//
// Loading runs no regular expression: every literal sits in the function that matches with it, so the sandbox's time bound on
// regular expressions applies to a call, which reports a limit, never to the load.
const mq = (() => {
  "use strict";
  const NUMBER = "$mq.number";
  const DAY_US = 86400000000n;

  class Dec { constructor(u, s) { this.u = u; this.s = s; Object.freeze(this); } }
  class Day { constructor(v) { this.v = v; Object.freeze(this); } }
  class Time { constructor(v) { this.v = v; Object.freeze(this); } }
  class Ts { constructor(v) { this.v = v; Object.freeze(this); } }
  class Json { constructor(v) { this.v = v; Object.freeze(this); } }

  const none = (x) => x === null || x === undefined;
  const powers = [1n];
  const pow10 = (n) => {
    while (powers.length <= n) powers.push(powers[powers.length - 1] * 10n);
    return powers[n];
  };
  const abs = (n) => (n < 0n ? -n : n);
  const floorDiv = (n, d) => (n >= 0n ? n / d : -((-n + d - 1n) / d));

  // n / d rounded half away from zero; d > 0.
  const divRound = (n, d) => {
    const q = n / d;
    const r = abs(n % d);
    return r * 2n >= d ? (n < 0n ? q - 1n : q + 1n) : q;
  };

  // Numbers -----------------------------------------------------------------------------------------------------------

  function parseDec(text) {
    const m = /^([+-]?)([0-9]*)(?:\.([0-9]*))?(?:[eE]([+-]?[0-9]+))?$/.exec(String(text).trim());
    if (m === null) return null;
    const whole = m[2], frac = m[3] === undefined ? "" : m[3];
    if (whole === "" && frac === "") return null;
    let u = BigInt((whole === "" ? "0" : whole) + frac);
    let s = frac.length - (m[4] === undefined ? 0 : Number(m[4]));
    if (s < 0) { u *= pow10(-s); s = 0; }
    return new Dec(m[1] === "-" ? -u : u, s);
  }

  function rescale(d, s) {
    if (s === d.s) return d;
    if (s > d.s) return new Dec(d.u * pow10(s - d.s), s);
    return new Dec(divRound(d.u, pow10(d.s - s)), s);
  }

  function toDec(v) {
    if (none(v)) return null;
    if (v instanceof Dec) return v;
    switch (typeof v) {
      case "string": return parseDec(v);
      case "bigint": return new Dec(v, 0);
      case "number":
        if (!Number.isFinite(v)) return null;
        return Number.isSafeInteger(v) ? new Dec(BigInt(v), 0) : parseDec(String(v));
      case "object": return typeof v[NUMBER] === "string" ? parseDec(v[NUMBER]) : null;
      default: return null;
    }
  }

  function decText(d) {
    const digits = abs(d.u).toString().padStart(d.s + 1, "0");
    const cut = digits.length - d.s;
    return (d.u < 0n ? "-" : "") + digits.slice(0, cut) + (d.s > 0 ? "." + digits.slice(cut) : "");
  }

  function toDouble(v) {
    if (none(v)) return null;
    if (typeof v === "number") return v;
    if (v instanceof Dec) return Number(decText(v));
    if (typeof v === "string") return Number(v.trim());
    if (typeof v === "object" && typeof v[NUMBER] === "string") return Number(v[NUMBER]);
    return null;
  }

  // PostgreSQL's select_div_scale (numeric.c): the weight and first group of four digits of each operand.
  function weight(d) {
    if (d.u === 0n) return [0, 0];
    const a = abs(d.u);
    const w = Math.floor((a.toString().length - d.s - 1) / 4);
    const e = d.s + 4 * w;
    return [w, Number(e >= 0 ? a / pow10(e) : a * pow10(-e))];
  }

  function divScale(a, b) {
    const [w1, f1] = weight(a), [w2, f2] = weight(b);
    const q = w1 - w2 - (f1 <= f2 ? 1 : 0);
    return Math.min(Math.max(16 - 4 * q, a.s, b.s, 0), 1000);
  }

  function add(a, b) {
    if (none(a) || none(b)) return null;
    if (typeof a === "number" || typeof b === "number") return toDouble(a) + toDouble(b);
    const s = Math.max(a.s, b.s);
    return new Dec(rescale(a, s).u + rescale(b, s).u, s);
  }

  function neg(a) {
    if (none(a)) return null;
    return typeof a === "number" ? -a : new Dec(-a.u, a.s);
  }

  function mul(a, b) {
    if (none(a) || none(b)) return null;
    if (typeof a === "number" || typeof b === "number") return toDouble(a) * toDouble(b);
    return new Dec(a.u * b.u, a.s + b.s);
  }

  // round((a + 0.000000000000000000) / NULLIF(b, 0), 16): two roundings, as PostgreSQL does them.
  function div(a, b) {
    if (none(a) || none(b)) return null;
    if (typeof a === "number" || typeof b === "number") {
      const y = toDouble(b);
      return y === 0 ? null : toDouble(a) / y;
    }
    if (b.u === 0n) return null;
    const x = a.s >= 18 ? a : rescale(a, 18);
    const scale = divScale(x, b);
    const shift = scale + b.s - x.s;
    let n = shift >= 0 ? x.u * pow10(shift) : x.u;
    let d = shift >= 0 ? b.u : b.u * pow10(-shift);
    if (d < 0n) { n = -n; d = -d; }
    return rescale(new Dec(divRound(n, d), scale), 16);
  }

  // Strings -----------------------------------------------------------------------------------------------------------

  function toStr(v) {
    if (none(v)) return null;
    switch (typeof v) {
      case "string": return v;
      case "number": case "boolean": case "bigint": return String(v);
      case "object": return typeof v[NUMBER] === "string" ? v[NUMBER] : null;
      default: return null;
    }
  }

  function rtrim(s) {
    let end = s.length;
    while (end > 0 && s.charCodeAt(end - 1) === 0x20) end--;
    return s.slice(0, end);
  }

  function trim(s) {
    if (none(s)) return null;
    let start = 0;
    while (start < s.length && s.charCodeAt(start) === 0x20) start++;
    return rtrim(s.slice(start));
  }

  // Code point order: a surrogate (a character beyond the basic plane) sorts above every other UTF-16 unit.
  function cmpStr(a, b) {
    if (a === b) return 0;
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i++) {
      const x = a.charCodeAt(i), y = b.charCodeAt(i);
      if (x === y) continue;
      const sx = x >= 0xd800 && x <= 0xdfff, sy = y >= 0xd800 && y <= 0xdfff;
      if (sx !== sy) return sx ? 1 : -1;
      return x < y ? -1 : 1;
    }
    return a.length < b.length ? -1 : a.length > b.length ? 1 : 0;
  }

  function length(s) {
    if (none(s)) return null;
    let n = 0;
    for (let i = 0; i < s.length; i++) {
      const c = s.charCodeAt(i);
      if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length) {
        const next = s.charCodeAt(i + 1);
        if (next >= 0xdc00 && next <= 0xdfff) i++;
      }
      n++;
    }
    return new Dec(BigInt(n), 0);
  }

  const patterns = new Map();
  function matches(s, p) {
    if (none(s) || none(p)) return null;
    let re = patterns.get(p);
    if (re === undefined) {
      re = new RegExp(p, "su");
      patterns.set(p, re);
    }
    return re.test(s);
  }

  function uuid(v) {
    const s = toStr(v);
    if (s === null) return null;
    const hex = s.replace(/[{}-]/g, "").toLowerCase();
    if (!/^[0-9a-f]{32}$/.test(hex)) return null;
    return hex.slice(0, 8) + "-" + hex.slice(8, 12) + "-" + hex.slice(12, 16) + "-" + hex.slice(16, 20) + "-" + hex.slice(20);
  }

  // Dates and times ---------------------------------------------------------------------------------------------------

  function daysFromCivil(y, m, d) {
    y -= m <= 2 ? 1 : 0;
    const era = Math.floor(y / 400);
    const yoe = y - era * 400;
    const doy = Math.floor((153 * ((m + 9) % 12) + 2) / 5) + d - 1;
    return era * 146097 + yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + doy - 719468;
  }

  function civilFromDays(z) {
    z += 719468;
    const era = Math.floor(z / 146097);
    const doe = z - era * 146097;
    const yoe = Math.floor((doe - Math.floor(doe / 1460) + Math.floor(doe / 36524) - Math.floor(doe / 146096)) / 365);
    const doy = doe - (365 * yoe + Math.floor(yoe / 4) - Math.floor(yoe / 100));
    const mp = Math.floor((5 * doy + 2) / 153);
    const m = mp < 10 ? mp + 3 : mp - 9;
    return [yoe + era * 400 + (m <= 2 ? 1 : 0), m, doy - Math.floor((153 * mp + 2) / 5) + 1];
  }

  const leap = (y) => (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;
  const monthDays = (y, m) => (m === 2 ? (leap(y) ? 29 : 28) : [4, 6, 9, 11].includes(m) ? 30 : 31);

  function days(y, m, d) {
    if (m < 1 || m > 12 || d < 1 || d > monthDays(y, m)) return null;
    return daysFromCivil(y, m, d);
  }

  // C's rint: the nearest integer, a half to the even one.
  function rint(x) {
    const f = Math.floor(x), r = x - f;
    return r > 0.5 || (r === 0.5 && f % 2 !== 0) ? f + 1 : f;
  }

  // Microseconds since midnight. The fraction rounds to six digits as PostgreSQL rounds it (ParseFractionalSecond): read as a
  // double, scaled by a million, then rint, so .1234565 is .123456 and .1234575 is .123458.
  function clock(h, mi, s, frac) {
    if (h > 24 || mi > 59 || s > 60) return null;
    const us = frac === undefined || frac === "" ? 0 : rint(Number("0." + frac) * 1000000);
    return ((h * 60 + mi) * 60 + s) * 1000000 + us;
  }

  function date(v) {
    if (none(v)) return null;
    if (v instanceof Day) return v;
    const m = /^([0-9]{4})-([0-9]{2})-([0-9]{2})$/.exec(String(v).trim());
    if (m === null) return null;
    const d = days(Number(m[1]), Number(m[2]), Number(m[3]));
    return d === null ? null : new Day(d);
  }

  function time(v) {
    if (none(v)) return null;
    if (v instanceof Time) return v;
    const m = /^([0-9]{1,2}):([0-9]{2})(?::([0-9]{2})(?:\.([0-9]*))?)?$/.exec(String(v).trim());
    if (m === null) return null;
    const us = clock(Number(m[1]), Number(m[2]), m[3] === undefined ? 0 : Number(m[3]), m[4]);
    return us === null ? null : new Time(us);
  }

  // A timestamp; with zoned, its offset moves it to UTC (none reads as UTC); without, an offset is ignored, as
  // PostgreSQL ignores one in a timestamp without time zone.
  function stamp(v, zoned) {
    if (none(v)) return null;
    if (v instanceof Ts) return v;
    const m = /^([0-9]{4})-([0-9]{2})-([0-9]{2})(?:[T ]([0-9]{1,2}):([0-9]{2})(?::([0-9]{2})(?:\.([0-9]*))?)?)? ?(Z|z|[+-][0-9]{2}(?::?[0-9]{2})?)?$/.exec(String(v).trim());
    if (m === null) return null;
    const d = days(Number(m[1]), Number(m[2]), Number(m[3]));
    const us = m[4] === undefined ? 0 : clock(Number(m[4]), Number(m[5]), m[6] === undefined ? 0 : Number(m[6]), m[7]);
    if (d === null || us === null) return null;
    let total = BigInt(d) * DAY_US + BigInt(us);
    if (zoned && m[8] !== undefined && m[8] !== "Z" && m[8] !== "z") {
      const z = m[8].replace(":", "");
      const minutes = Number(z.slice(1, 3)) * 60 + (z.length > 3 ? Number(z.slice(3, 5)) : 0);
      total -= BigInt((z[0] === "-" ? -minutes : minutes) * 60000000);
    }
    return new Ts(total);
  }

  const pad = (n, w) => String(n).padStart(w, "0");

  function dayText(d) {
    const [y, m, dd] = civilFromDays(d);
    return pad(y, 4) + "-" + pad(m, 2) + "-" + pad(dd, 2);
  }

  function clockText(us) {
    const s = Math.floor(us / 1000000), f = us % 1000000;
    const text = pad(Math.floor(s / 3600), 2) + ":" + pad(Math.floor(s / 60) % 60, 2) + ":" + pad(s % 60, 2);
    return f === 0 ? text : text + "." + pad(f, 6).replace(/0+$/, "");
  }

  function splitTs(t) {
    const d = floorDiv(t.v, DAY_US);
    return [Number(d), Number(t.v - d * DAY_US)];
  }

  function shiftMonths(day, n) {
    const [y, m, d] = civilFromDays(day);
    const index = y * 12 + (m - 1) + n;
    const ny = Math.floor(index / 12), nm = index - ny * 12 + 1;
    return daysFromCivil(ny, nm, Math.min(d, monthDays(ny, nm)));
  }

  // A day or month count: an int, which may carry a scale (1.0), so it is read at scale 0.
  const count = (n) => Number(rescale(toDec(n), 0).u);

  function addDays(d, n) {
    if (none(d) || none(n)) return null;
    if (d instanceof Day) return new Day(d.v + count(n));
    return new Ts(d.v + BigInt(count(n)) * DAY_US);
  }

  function addMonths(d, n) {
    if (none(d) || none(n)) return null;
    if (d instanceof Day) return new Day(shiftMonths(d.v, count(n)));
    const [day, us] = splitTs(d);
    return new Ts(BigInt(shiftMonths(day, count(n))) * DAY_US + BigInt(us));
  }

  function datePart(part, d) {
    if (none(part) || none(d)) return null;
    const [day, us] = d instanceof Day ? [d.v, 0] : splitTs(d);
    const [y, m, dd] = civilFromDays(day);
    const value = { year: y, month: m, day: dd, isodow: ((day % 7) + 10) % 7 + 1,
      hour: Math.floor(us / 3600000000), minute: Math.floor(us / 60000000) % 60 }[part];
    return value === undefined ? null : new Dec(BigInt(value), 0);
  }

  function dateOf(t) {
    return none(t) ? null : new Day(splitTs(t)[0]);
  }

  // JSON --------------------------------------------------------------------------------------------------------------

  function json(v) {
    if (none(v)) return null;
    return v instanceof Json ? v : new Json(v);
  }

  function jsonType(j) {
    if (none(j)) return null;
    const v = j.v;
    if (v === null) return "null";
    if (Array.isArray(v)) return "array";
    switch (typeof v) {
      case "string": return "string";
      case "number": case "bigint": return "number";
      case "boolean": return "boolean";
      default: return typeof v[NUMBER] === "string" ? "number" : "object";
    }
  }

  function jsonLength(j) {
    return !none(j) && Array.isArray(j.v) ? new Dec(BigInt(j.v.length), 0) : null;
  }

  // Comparison and logic ----------------------------------------------------------------------------------------------

  // PostgreSQL's order of doubles: NaN equals NaN and sorts above every other value.
  function cmpNum(a, b) {
    if (Number.isNaN(a)) return Number.isNaN(b) ? 0 : 1;
    if (Number.isNaN(b)) return -1;
    return a < b ? -1 : a > b ? 1 : 0;
  }

  function cmp(a, b) {
    if (typeof a === "number" || typeof b === "number") return cmpNum(toDouble(a), toDouble(b));
    if (a instanceof Dec) {
      const s = Math.max(a.s, b.s);
      const x = rescale(a, s).u, y = rescale(b, s).u;
      return x < y ? -1 : x > y ? 1 : 0;
    }
    if (typeof a === "string") return cmpStr(a, b);
    if (typeof a === "boolean") return a === b ? 0 : a ? 1 : -1;
    return a.v < b.v ? -1 : a.v > b.v ? 1 : 0;
  }

  const compare = (test) => (a, b) => (none(a) || none(b) ? null : test(cmp(a, b)));

  function and(...xs) {
    let unknown = false;
    for (const x of xs) {
      if (x === false) return false;
      if (none(x)) unknown = true;
    }
    return unknown ? null : true;
  }

  function or(...xs) {
    let unknown = false;
    for (const x of xs) {
      if (x === true) return true;
      if (none(x)) unknown = true;
    }
    return unknown ? null : false;
  }

  const not = (x) => (none(x) ? null : !x);

  function isIn(x, list) {
    if (none(x)) return null;
    for (const item of list) {
      if (cmp(x, item) === 0) return true;
    }
    return false;
  }

  function distinct(a, b) {
    if (none(a) || none(b)) return none(a) !== none(b);
    return cmp(a, b) !== 0;
  }

  function coalesce(...xs) {
    for (const x of xs) {
      if (!none(x)) return x;
    }
    return null;
  }

  // case: condition, value, condition, value…, then the else (null when there is none).
  function choose(...xs) {
    const last = xs.length - 1;
    for (let i = 0; i < last; i += 2) {
      if (xs[i] === true) return none(xs[i + 1]) ? null : xs[i + 1];
    }
    return none(xs[last]) ? null : xs[last];
  }

  // A result leaving the runtime, as JSON: decimals and integers as exact text, dates and timestamps as ISO text.
  function out(v, kind) {
    if (none(v)) return null;
    if (v instanceof Dec) return kind === "double" ? toDouble(v) : decText(v);
    if (typeof v === "number") return Number.isFinite(v) ? v : null;
    if (v instanceof Day) return dayText(v.v);
    if (v instanceof Time) return clockText(v.v);
    if (v instanceof Ts) {
      const [day, us] = splitTs(v);
      return dayText(day) + "T" + clockText(us) + (kind === "datetimeoffset" ? "Z" : "");
    }
    if (v instanceof Json) return typeof v.v === "object" && v.v !== null && typeof v.v[NUMBER] === "string" ? Number(v.v[NUMBER]) : v.v;
    return v;
  }

  function at(row, ...keys) {
    let v = row;
    for (const k of keys) {
      if (v === null || typeof v !== "object") return null;
      v = v[k];
    }
    return v === undefined ? null : v;
  }

  return Object.freeze({
    // Reading values by type.
    at,
    dec: (v, scale) => {
      const d = toDec(v);
      return d === null || scale === undefined ? d : rescale(d, scale);
    },
    dbl: (v) => toDouble(v),
    str: toStr,
    fixed: (v) => {
      const s = toStr(v);
      return s === null ? null : rtrim(s);
    },
    bool: (v) => (typeof v === "boolean" ? v : null),
    date,
    time,
    ts: (v) => stamp(v, false),
    tsz: (v) => stamp(v, true),
    uuid,
    json,
    toDouble,
    now: () => new Ts(BigInt(Date.now()) * 1000n),
    // Operators.
    eq: compare((c) => c === 0),
    ne: compare((c) => c !== 0),
    lt: compare((c) => c < 0),
    le: compare((c) => c <= 0),
    gt: compare((c) => c > 0),
    ge: compare((c) => c >= 0),
    between: (x, lo, hi) => and(compare((c) => c >= 0)(x, lo), compare((c) => c <= 0)(x, hi)),
    in: isIn,
    notIn: (x, list) => not(isIn(x, list)),
    isNull: (x) => none(x),
    isNotNull: (x) => !none(x),
    distinct,
    notDistinct: (a, b) => !distinct(a, b),
    and,
    or,
    not,
    implies: (a, b) => or(not(a), b),
    concat: (...xs) => (xs.some(none) ? null : xs.join("")),
    add,
    sub: (a, b) => add(a, neg(b)),
    mul,
    div,
    neg,
    coalesce,
    case: choose,
    // Functions.
    trim,
    length,
    matches,
    nonnullCount: (...xs) => new Dec(BigInt(xs.filter((x) => !none(x)).length), 0),
    addDays,
    addMonths,
    datePart,
    dateOf,
    round: (x, n) => {
      const d = toDec(x);
      return d === null ? null : rescale(d, n);
    },
    jsonType,
    jsonLength,
    // Results.
    passes: (x) => x !== false,
    holds: (x) => x === true,
    out,
  });
})();
