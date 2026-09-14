"""Build the PixStorage-shaped synthetic timing capture used by the PIX-free timing tests.

Every base table and index recorded by pixstorage_schema.py is created with its verbatim DDL. The pixstorage.dll virtual
tables become plain tables holding only their non-hidden columns, so the synthetic tier exercises the tuple-match
fallback while native tests cover hidden columns and pushdown. Rows come from the seed JSON
({"tables": {name: {"columns": [...], "rows": [[...]]}}}, BLOB cells as {"hex": "..."}). The SQLite file header differs
between SQLite versions, so determinism is pinned by a SHA-256 over the sorted dump statements, kept next to the file.

    python scripts/make_timing_fixture.py                  # rebuild; refuses when the logical hash would change
    python scripts/make_timing_fixture.py --update-hash    # rebuild after an intended seed or schema change
    python scripts/make_timing_fixture.py --check          # exit 1 when the committed file or hash is stale
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = ROOT / "tests" / "PixMcp.Tests" / "Fixtures"
DEFAULT_SCHEMA = FIXTURES / "pixstorage-schema-2606.18.json"
DEFAULT_SEED = FIXTURES / "timing-fixture.json"
DEFAULT_OUT = FIXTURES / "timing-synthetic.sqlite"
MAX_BYTES = 300_000


def stand_in_ddl(name, columns):
    visible = [f'{column["name"]} {column["type"]}'.rstrip() for column in columns if not column["hidden"]]
    return f"CREATE TABLE {name}({', '.join(visible)})"


def cell(value):
    if isinstance(value, dict):
        if set(value) != {"hex"}:
            raise ValueError(f"unsupported seed cell: {value}")
        return bytes.fromhex(value["hex"])
    return value


def build(schema, seed, path):
    virtual = schema.get("virtualTables", {})
    unreadable = sorted(name for name, columns in virtual.items() if not isinstance(columns, list))
    if unreadable:
        raise ValueError("schema has no columns for virtual tables: " + ", ".join(unreadable))
    known = set(schema["tables"]) | set(virtual)
    for name, entry in seed["tables"].items():
        if name not in known:
            raise ValueError(f"seed table not in the schema: {name}")
        for row in entry["rows"]:
            if len(row) != len(entry["columns"]):
                raise ValueError(f"{name}: row {row} has {len(row)} cells for {len(entry['columns'])} columns")
    path = Path(path)
    if path.exists():
        path.unlink()
    connection = sqlite3.connect(path, isolation_level=None)
    try:
        # 1 KiB pages keep more than a hundred mostly empty tables and indexes small.
        connection.execute("PRAGMA page_size = 1024")
        connection.execute("BEGIN")
        for name in sorted(schema["tables"]):
            connection.execute(schema["tables"][name]["sql"])
        for name in sorted(schema["indexes"]):
            if schema["indexes"][name]["sql"]:  # sqlite_autoindex_* come from the table constraints
                connection.execute(schema["indexes"][name]["sql"])
        for name in sorted(virtual):
            connection.execute(stand_in_ddl(name, virtual[name]))
        for name in sorted(seed["tables"]):
            columns = seed["tables"][name]["columns"]
            statement = f"INSERT INTO {name}({', '.join(columns)}) VALUES({', '.join('?' * len(columns))})"
            connection.executemany(statement, ([cell(value) for value in row] for row in seed["tables"][name]["rows"]))
        connection.execute("COMMIT")
        connection.execute("VACUUM")
    finally:
        connection.close()
    return path


def logical_hash(path):
    connection = sqlite3.connect(path)
    try:
        statements = sorted(line for line in connection.iterdump() if not line.startswith(("BEGIN", "COMMIT", "PRAGMA")))
    finally:
        connection.close()
    digest = hashlib.sha256()
    for statement in statements:
        digest.update(statement.encode("utf-8") + b"\n")
    return digest.hexdigest()


def verify(schema, seed, path):
    """Structural problems of a built fixture: missing or extra tables, stand-in columns, seed row counts, size."""
    problems = []
    virtual = schema.get("virtualTables", {})
    connection = sqlite3.connect(path)
    try:
        names = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}
        expected = set(schema["tables"]) | set(virtual)
        problems += [f"missing table: {name}" for name in sorted(expected - names)]
        problems += [f"unexpected table: {name}" for name in sorted(names - expected)]
        for name in sorted(set(virtual) & names):
            actual = [row[1] for row in connection.execute(f'PRAGMA table_xinfo("{name}")')]
            wanted = [column["name"] for column in virtual[name] if not column["hidden"]]
            if actual != wanted:
                problems.append(f"stand-in {name} columns {actual} != {wanted}")
        for name in sorted(set(seed["tables"]) & names):
            rows = connection.execute(f'SELECT COUNT(*) FROM "{name}"').fetchone()[0]
            if rows != len(seed["tables"][name]["rows"]):
                problems.append(f"{name}: {rows} rows, seed has {len(seed['tables'][name]['rows'])}")
    finally:
        connection.close()
    size = Path(path).stat().st_size
    if size > MAX_BYTES:
        problems.append(f"fixture is {size} bytes; keep it under {MAX_BYTES}")
    return problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--schema", default=str(DEFAULT_SCHEMA), help="Schema JSON written by pixstorage_schema.py.")
    parser.add_argument("--seed", default=str(DEFAULT_SEED), help="Seed rows JSON.")
    parser.add_argument("--out", default=str(DEFAULT_OUT), help="SQLite fixture to write or check.")
    parser.add_argument("--check", action="store_true", help="Rebuild in a temporary folder and compare; exit 1 on drift.")
    parser.add_argument("--update-hash", action="store_true", help="Accept a changed logical hash.")
    args = parser.parse_args(argv)
    schema = json.loads(Path(args.schema).read_text(encoding="utf-8"))
    seed = json.loads(Path(args.seed).read_text(encoding="utf-8"))
    out = Path(args.out)
    hash_path = out.with_name(out.name + ".sha256")
    recorded = hash_path.read_text(encoding="utf-8").strip() if hash_path.is_file() else None
    with tempfile.TemporaryDirectory() as scratch:
        built = build(schema, seed, Path(scratch) / out.name)
        problems = verify(schema, seed, built)
        actual = logical_hash(built)
        if args.check:
            if recorded != actual:
                problems.append(f"logical hash {actual} != recorded {recorded}; rebuild with --update-hash after an intended change")
            if not out.is_file():
                problems.append(f"missing {out}")
            elif logical_hash(out) != actual:
                problems.append(f"{out.name} is stale; rebuild it")
        elif recorded not in (None, actual) and not args.update_hash:
            problems.append(f"logical hash would change from {recorded} to {actual}; pass --update-hash if intended")
        for problem in problems:
            print("make_timing_fixture:", problem)
        if problems:
            return 1
        if not args.check:
            shutil.copyfile(built, out)
            hash_path.write_text(actual + "\n", encoding="utf-8")
        size = (out if args.check else built).stat().st_size
    print(f"make_timing_fixture: {'ok' if args.check else 'wrote'} {out.name} {size} bytes, logical sha256 {actual}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
