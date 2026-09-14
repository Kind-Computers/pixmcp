"""Record or check the PixStorage schema of a PIX timing capture.

Opens the capture read-only (immutable URI), loads pixstorage.dll for its virtual tables and SQL
functions, and records every table with its DDL and table_xinfo columns (hidden ones included),
every index, the modules and functions the extension adds to stock SQLite, and the CaptureFacts ids.
--check compares against the committed JSON and exits 1 on schema drift; row counts and fact ids are
capture-specific and only informational.

    python scripts/pixstorage_schema.py tests/artifacts/timing-validation/timing.wpix
    python scripts/pixstorage_schema.py tests/artifacts/timing-validation/timing.wpix --check
"""
import argparse
import json
from pathlib import Path
import sqlite3
import sys

DEFAULT_OUT = "tests/PixMcp.Tests/Fixtures/pixstorage-schema-2606.18.json"
DEFAULT_DLL = r"C:\Program Files\Microsoft PIX Preview\2606.18-preview\pixstorage.dll"


def stock():
    """Modules and functions of a plain SQLite connection, so the census lists only what pixstorage.dll adds."""
    plain = sqlite3.connect(":memory:")
    try:
        modules = {row[0] for row in plain.execute("SELECT name FROM pragma_module_list")}
        functions = {f"{name}/{narg}" for name, narg in plain.execute("SELECT name, narg FROM pragma_function_list")}
        return modules, functions
    finally:
        plain.close()


def open_capture(path, dll=None):
    uri = "file:" + Path(path).resolve().as_posix() + "?mode=ro&immutable=1"
    connection = sqlite3.connect(uri, uri=True)
    if dll:
        connection.enable_load_extension(True)
        try:
            connection.load_extension(str(dll), entrypoint="sqlite3_batchexpand_init")
        finally:
            connection.enable_load_extension(False)
    return connection


def census(connection, stock_modules=frozenset(), stock_functions=frozenset(), include_rows=True):
    master = connection.execute("SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name").fetchall()
    tables = {}
    for kind, name, _, sql in master:
        if kind != "table" or name.startswith("sqlite_"):
            continue
        columns = [{"name": column[1], "type": column[2], "notNull": bool(column[3]), "primaryKey": column[5], "hidden": column[6]}
                   for column in connection.execute(f'PRAGMA table_xinfo("{name}")')]
        entry = {"sql": sql, "columns": columns}
        if include_rows:
            try:
                entry["rows"] = connection.execute(f'SELECT COUNT(*) FROM "{name}"').fetchone()[0]
            except sqlite3.Error as error:
                entry["rows"] = f"error: {error}"
        tables[name] = entry
    indexes = {name: {"table": table, "sql": sql} for kind, name, table, sql in master if kind == "index"}
    modules = sorted({row[0] for row in connection.execute("SELECT name FROM pragma_module_list")} - set(stock_modules))
    functions = sorted({f"{name}/{narg}" for name, narg in connection.execute("SELECT name, narg FROM pragma_function_list")} - set(stock_functions))
    facts = [row[0] for row in connection.execute("SELECT Id FROM CaptureFacts ORDER BY Id")] if "CaptureFacts" in tables else []
    # Eponymous virtual tables are not in sqlite_master; table_xinfo still lists their columns, hidden constraint inputs included.
    virtual_tables = {}
    for module in modules:
        try:
            virtual_tables[module] = [{"name": column[1], "type": column[2], "hidden": column[6]}
                                      for column in connection.execute(f'PRAGMA table_xinfo("{module}")')]
        except sqlite3.Error as error:
            virtual_tables[module] = f"error: {error}"
    return {"census": {"tables": len(tables), "indexes": len(indexes), "modules": len(modules), "functions": len(functions)},
            "tables": tables, "indexes": indexes, "modules": modules, "virtualTables": virtual_tables, "functions": functions,
            "captureFacts": facts}


def compare(expected, actual):
    """Schema drift between two census documents; row counts and capture fact ids are ignored."""
    problems = []
    for section in ("modules", "functions"):
        before, after = set(expected.get(section, [])), set(actual.get(section, []))
        problems += [f"{section} removed: {name}" for name in sorted(before - after)]
        problems += [f"{section} added: {name}" for name in sorted(after - before)]
    for section, singular in (("tables", "table"), ("indexes", "index")):
        before, after = expected.get(section, {}), actual.get(section, {})
        problems += [f"{singular} removed: {name}" for name in sorted(set(before) - set(after))]
        problems += [f"{singular} added: {name}" for name in sorted(set(after) - set(before))]
        for name in sorted(set(before) & set(after)):
            if before[name].get("sql") != after[name].get("sql"):
                problems.append(f"{singular} definition changed: {name}")
            if section == "tables" and before[name].get("columns") != after[name].get("columns"):
                problems.append(f"table columns changed: {name}")
    before, after = expected.get("virtualTables", {}), actual.get("virtualTables", {})
    problems += [f"virtual table columns changed: {name}" for name in sorted(set(before) & set(after)) if before[name] != after[name]]
    return problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("capture", help="Recorded timing capture (.wpix).")
    parser.add_argument("--out", default=DEFAULT_OUT, help="Schema JSON to write or check.")
    parser.add_argument("--check", action="store_true", help="Compare with --out instead of writing it; exit 1 on drift.")
    parser.add_argument("--pixstorage", default=DEFAULT_DLL, help="pixstorage.dll providing the virtual tables and functions.")
    args = parser.parse_args(argv)
    if not Path(args.capture).is_file():
        parser.error(f"capture not found: {args.capture}")
    modules, functions = stock()
    connection = open_capture(args.capture, args.pixstorage if Path(args.pixstorage).is_file() else None)
    try:
        actual = census(connection, modules, functions)
    finally:
        connection.close()
    out = Path(args.out)
    if args.check:
        problems = compare(json.loads(out.read_text(encoding="utf-8")), actual)
        for problem in problems:
            print("drift:", problem)
        print(f"pixstorage_schema: {'drift' if problems else 'ok'} {actual['census']}")
        return 1 if problems else 0
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(actual, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    print(f"pixstorage_schema: wrote {out} {actual['census']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
