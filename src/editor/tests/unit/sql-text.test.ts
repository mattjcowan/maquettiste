import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { scanSqlText, sqlTextMentions, sqlTextPosition, trimSqlWhite, unwrapSqlText, type SqlDialect } from "../../src/model/sqlText";

interface Vector {
  name: string;
  dialect: SqlDialect;
  text: string;
  placeholders: { name: string; offset: number; line: number; column: number }[];
  firstWord: string | null;
  verb: string | null;
  names?: string[];
  orderBy: boolean;
  offsetFetchOrTop: boolean;
  finalSemicolon?: number | null;
}

const file = resolve(__dirname, "../../../../tests/fixtures/sql-text/scanner.json");
const vectors = (JSON.parse(readFileSync(file, "utf8")) as { cases: Vector[] }).cases;

describe("SQL text scanner, shared vectors", () => {
  it("has vectors", () => {
    expect(vectors.length).toBeGreaterThan(20);
  });

  it.each(vectors.map((v) => [v.name, v] as const))("%s", (_name, v) => {
    const scan = scanSqlText(v.text, v.dialect);
    expect(scan.placeholders.map((p) => ({ name: p.name, offset: p.offset, ...sqlTextPosition(v.text, p.offset) }))).toEqual(v.placeholders);
    expect(scan.firstWord).toBe(v.firstWord);
    expect(scan.verb).toBe(v.verb);
    if (v.names) expect(scan.names).toEqual(v.names);
    expect(scan.orderBy).toBe(v.orderBy);
    expect(scan.offsetFetchOrTop).toBe(v.offsetFetchOrTop);
    if (v.finalSemicolon !== undefined) expect(scan.finalSemicolon).toBe(v.finalSemicolon);
  });

  it("compares names ignoring case", () => {
    const scan = scanSqlText("SELECT * FROM Invoices", "postgresql");
    expect(sqlTextMentions(scan, "invoices")).toBe(true);
    expect(sqlTextMentions(scan, "customers")).toBe(false);
  });

  it("trims .NET white space plus the byte order mark, and finds a final semicolon outside comments and strings", () => {
    expect(trimSqlWhite("\ufeff\u0085 SELECT 1;\u00a0\n")).toBe("SELECT 1;");
    expect(unwrapSqlText("SELECT 1;", "postgresql")).toBe("SELECT 1");
    expect(unwrapSqlText("SELECT 1 ; -- note\n/* x */ ", "postgresql")).toBe("SELECT 1");
    expect(unwrapSqlText("SELECT ';'", "postgresql")).toBe("SELECT ';'");
    expect(unwrapSqlText("SELECT 1 -- ;", "postgresql")).toBe("SELECT 1 -- ;");
    expect(unwrapSqlText("SELECT 1;;", "postgresql")).toBe("SELECT 1;");
    expect(unwrapSqlText("BEGIN SELECT 1; END", "postgresql")).toBe("BEGIN SELECT 1; END");
  });

  it("upper-cases keywords as ASCII, leaving ß and ı alone", () => {
    expect(scanSqlText("select 1", "postgresql").firstWord).toBe("SELECT");
    expect(scanSqlText("ßelect 1", "postgresql").firstWord).toBe("ßELECT");
    expect(scanSqlText("selectı 1", "postgresql").firstWord).toBe("SELECTı");
  });
});
