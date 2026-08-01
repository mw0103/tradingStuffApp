using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TradingStuff.Volatility.ThetaData
{
    /// <summary>
    /// A CSV response parsed by column name rather than by position.
    ///
    /// Deliberately name-driven. ThetaData documents a fixed column order per endpoint,
    /// but ordering is exactly the kind of thing that changes between API versions, and a
    /// positional parser that silently reads the ask out of the bid column produces
    /// plausible numbers with no error. Looking columns up by name means a schema change
    /// throws with the headers it actually received.
    /// </summary>
    public class CsvTable
    {
        private readonly Dictionary<string, int> _columns;
        private readonly List<string[]> _rows;

        public IReadOnlyList<string[]> Rows
        {
            get { return _rows; }
        }

        public int Count
        {
            get { return _rows.Count; }
        }

        public IEnumerable<string> ColumnNames
        {
            get { return _columns.Keys; }
        }

        private CsvTable(Dictionary<string, int> columns, List<string[]> rows)
        {
            _columns = columns;
            _rows = rows;
        }

        public static CsvTable Parse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("The response body was empty.");

            var lines = content
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (lines.Count == 0)
                throw new ArgumentException("The response contained no rows.");

            var headers = lines[0].Split(',');
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                columns[headers[i].Trim()] = i;
            }

            var rows = new List<string[]>(lines.Count - 1);
            for (int i = 1; i < lines.Count; i++)
            {
                rows.Add(lines[i].Split(','));
            }

            return new CsvTable(columns, rows);
        }

        public bool HasColumn(string name)
        {
            return _columns.ContainsKey(name);
        }

        /// <summary>
        /// Resolves a column, accepting several spellings so the parser survives the
        /// naming differences between API versions.
        /// </summary>
        public int RequireColumn(params string[] candidateNames)
        {
            foreach (var name in candidateNames)
            {
                int index;
                if (_columns.TryGetValue(name, out index)) return index;
            }

            throw new InvalidOperationException(string.Format(
                "None of the expected columns [{0}] are present. The response has: [{1}]. " +
                "This usually means the endpoint's schema changed.",
                string.Join(", ", candidateNames),
                string.Join(", ", _columns.Keys)));
        }

        public static double GetDouble(string[] row, int column)
        {
            if (column >= row.Length)
                throw new InvalidOperationException("The row has fewer fields than the header declares.");

            double value;
            if (!double.TryParse(row[column], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new InvalidOperationException(string.Format("Could not parse '{0}' as a number.", row[column]));

            return value;
        }

        public static long GetInt64(string[] row, int column)
        {
            if (column >= row.Length)
                throw new InvalidOperationException("The row has fewer fields than the header declares.");

            long value;
            if (!long.TryParse(row[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new InvalidOperationException(string.Format("Could not parse '{0}' as an integer.", row[column]));

            return value;
        }

        public static string GetString(string[] row, int column)
        {
            return column < row.Length ? row[column].Trim() : string.Empty;
        }
    }
}
