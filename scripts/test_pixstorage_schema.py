"""PixStorage schema census tests over in-memory SQLite; no PIX install or capture needed."""
import copy
import sqlite3
import unittest

from pixstorage_schema import census, compare, stock


def database():
    connection = sqlite3.connect(":memory:")
    connection.executescript("""
        CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
        INSERT INTO CaptureFacts VALUES (2, 100), (3, 900), (24, 500);
        CREATE TABLE Samples(Id INTEGER PRIMARY KEY, Timestamp INTEGER NOT NULL, Doubled INTEGER GENERATED ALWAYS AS (Timestamp * 2) VIRTUAL);
        CREATE INDEX SamplesByTime ON Samples(Timestamp);
        CREATE TABLE Names(Value TEXT UNIQUE);
        INSERT INTO Samples(Id, Timestamp) VALUES (1, 10), (2, 20);
    """)
    return connection


class PixStorageSchemaTests(unittest.TestCase):
    def test_census_lists_tables_indexes_hidden_columns_and_facts(self):
        modules, functions = stock()
        connection = database()
        connection.create_function("findstackid", 2, lambda thread, timestamp: 0)
        result = census(connection, modules, functions)
        self.assertEqual({"tables": 3, "indexes": 2, "modules": 0, "functions": 1}, result["census"])
        self.assertEqual(["findstackid/2"], result["functions"])
        self.assertEqual([2, 3, 24], result["captureFacts"])
        samples = result["tables"]["Samples"]
        self.assertEqual(2, samples["rows"])
        doubled = next(column for column in samples["columns"] if column["name"] == "Doubled")
        self.assertNotEqual(0, doubled["hidden"])
        self.assertTrue(any(name.startswith("sqlite_autoindex_Names") for name in result["indexes"]))

    def test_compare_reports_schema_drift_but_not_row_counts(self):
        modules, functions = stock()
        expected = census(database(), modules, functions)
        self.assertEqual([], compare(expected, copy.deepcopy(expected)))
        changed = database()
        changed.executescript("""
            INSERT INTO Samples(Id, Timestamp) VALUES (3, 30);
            ALTER TABLE Names ADD COLUMN Extra INTEGER;
            CREATE TABLE Added(Id INTEGER);
            DROP INDEX SamplesByTime;
        """)
        changed.create_function("findstackid", 2, lambda thread, timestamp: 0)
        problems = compare(expected, census(changed, modules, functions))
        self.assertIn("table added: Added", problems)
        self.assertIn("table columns changed: Names", problems)
        self.assertIn("table definition changed: Names", problems)
        self.assertIn("index removed: SamplesByTime", problems)
        self.assertIn("functions added: findstackid/2", problems)
        self.assertFalse(any("Samples" in problem and "columns" in problem for problem in problems))

    def test_compare_reports_virtual_table_column_drift(self):
        expected = {"modules": ["ContextSwitch"], "virtualTables": {"ContextSwitch": [{"name": "Core", "type": "INTEGER", "hidden": 0}]}}
        actual = copy.deepcopy(expected)
        self.assertEqual([], compare(expected, actual))
        actual["virtualTables"]["ContextSwitch"].append({"name": "RowId", "type": "integer", "hidden": 1})
        self.assertEqual(["virtual table columns changed: ContextSwitch"], compare(expected, actual))


if __name__ == "__main__":
    unittest.main()
