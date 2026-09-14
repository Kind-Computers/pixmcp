"""Synthetic timing fixture generator tests; no PIX install or capture needed."""
import copy
import sqlite3
import tempfile
import unittest
from pathlib import Path

import make_timing_fixture as generator

SCHEMA = {
    "tables": {
        "Strings": {"sql": "CREATE TABLE Strings (Id integer primary key asc, Value text )"},
        "CpuSample": {"sql": "CREATE TABLE CpuSample (Core integer, Timestamp integer, ProcThreadId integer, primary key (Timestamp, Core)) without rowid"},
        "Stacks": {"sql": "CREATE TABLE Stacks (Id integer primary key asc, NumFrames integer, Addresses blob)"},
    },
    "indexes": {
        "StringsByValue": {"table": "Strings", "sql": "CREATE INDEX StringsByValue ON Strings(Value)"},
        "sqlite_autoindex_CpuSample_1": {"table": "CpuSample", "sql": None},
    },
    "virtualTables": {
        "PixCpuExecutionTimes": [{"name": "Duration", "type": "INTEGER", "hidden": 0},
                                 {"name": "CpuExecutionRowId", "type": "integer", "hidden": 1}],
    },
}
SEED = {"tables": {
    "Strings": {"columns": ["Id", "Value"], "rows": [[1, "Frame"], [2, "Worker"]]},
    "Stacks": {"columns": ["Id", "NumFrames", "Addresses"], "rows": [[1, 1, {"hex": "00ff"}]]},
    "PixCpuExecutionTimes": {"columns": ["Duration"], "rows": [[200]]},
}}


class MakeTimingFixtureTests(unittest.TestCase):
    def test_build_keeps_real_ddl_and_indexes_with_plain_stand_ins_and_seed_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            path = generator.build(SCHEMA, SEED, Path(directory) / "fixture.sqlite")
            self.assertEqual([], generator.verify(SCHEMA, SEED, path))
            connection = sqlite3.connect(path)
            try:
                self.assertIn("without rowid", connection.execute("SELECT sql FROM sqlite_master WHERE name = 'CpuSample'").fetchone()[0])
                self.assertIsNotNone(connection.execute("SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'StringsByValue'").fetchone())
                self.assertEqual(["Duration"], [row[1] for row in connection.execute("PRAGMA table_xinfo(PixCpuExecutionTimes)")])
                self.assertEqual(b"\x00\xff", connection.execute("SELECT Addresses FROM Stacks").fetchone()[0])
            finally:
                connection.close()

    def test_logical_hash_is_stable_across_builds_and_tracks_seed_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            first = generator.logical_hash(generator.build(SCHEMA, SEED, Path(directory) / "a.sqlite"))
            self.assertEqual(first, generator.logical_hash(generator.build(SCHEMA, SEED, Path(directory) / "b.sqlite")))
            changed = copy.deepcopy(SEED)
            changed["tables"]["Strings"]["rows"][0][1] = "Frame 2"
            self.assertNotEqual(first, generator.logical_hash(generator.build(SCHEMA, changed, Path(directory) / "a.sqlite")))

    def test_seed_errors_name_the_table(self):
        with tempfile.TemporaryDirectory() as directory:
            unknown = copy.deepcopy(SEED)
            unknown["tables"]["Missing"] = {"columns": ["Id"], "rows": [[1]]}
            with self.assertRaisesRegex(ValueError, "Missing"):
                generator.build(SCHEMA, unknown, Path(directory) / "fixture.sqlite")
            short = copy.deepcopy(SEED)
            short["tables"]["Strings"]["rows"].append([3])
            with self.assertRaisesRegex(ValueError, "Strings"):
                generator.build(SCHEMA, short, Path(directory) / "fixture.sqlite")

    def test_committed_fixture_matches_its_seed_schema_and_hash(self):
        if not generator.DEFAULT_OUT.is_file():
            self.skipTest("committed fixture not generated yet")
        self.assertEqual(0, generator.main(["--check"]))


if __name__ == "__main__":
    unittest.main()
