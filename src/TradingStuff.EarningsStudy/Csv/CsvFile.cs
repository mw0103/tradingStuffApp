using System.Globalization;
using System.Reflection;
using System.Text;

namespace TradingStuff.EarningsStudy.Csv;

/// <summary>
/// Reads and writes the study's tables as RFC 4180 CSV, mapping a positional record's constructor
/// parameters to snake_case columns. One mapper for every table, so no verb grows its own notion
/// of how a decimal or a date is spelled: invariant culture, ISO dates, <c>true</c>/<c>false</c>,
/// and an empty cell for null.
/// </summary>
public static class CsvFile
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Write<T>(string path, IEnumerable<T> rows)
    {
        var schema = Schema<T>.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        writer.WriteLine(string.Join(",", schema.Columns));
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", schema.Getters.Select(get => Quote(Format(get(row!))))));
        }
    }

    /// <summary>Appends rows, writing the header only when the file does not exist yet.</summary>
    public static void Append<T>(string path, IEnumerable<T> rows)
    {
        var schema = Schema<T>.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
        if (writeHeader) writer.WriteLine(string.Join(",", schema.Columns));
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", schema.Getters.Select(get => Quote(Format(get(row!))))));
        }
    }

    public static List<T> Read<T>(string path)
    {
        var schema = Schema<T>.Instance;
        var records = ParseRecords(File.ReadAllText(path));
        if (records.Count == 0) return [];

        var header = records[0];
        var positions = new int[schema.Columns.Length];
        for (var i = 0; i < schema.Columns.Length; i++)
        {
            positions[i] = Array.FindIndex(header, h => string.Equals(h, schema.Columns[i], StringComparison.Ordinal));
            if (positions[i] < 0)
            {
                throw new InvalidDataException($"{path}: column '{schema.Columns[i]}' required by {typeof(T).Name} is missing. Header: {string.Join(",", header)}");
            }
        }

        var rows = new List<T>(records.Count - 1);
        for (var r = 1; r < records.Count; r++)
        {
            var record = records[r];
            if (record.Length == 1 && record[0].Length == 0) continue;
            var args = new object?[schema.Columns.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var cell = positions[i] < record.Length ? record[positions[i]] : "";
                try
                {
                    args[i] = cell.Length == 0 && schema.NullableStrings[i] ? null : Parse(cell, schema.ParameterTypes[i]);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException)
                {
                    throw new InvalidDataException($"{path} line {r + 1}: column '{schema.Columns[i]}' cannot parse '{cell}' as {schema.ParameterTypes[i].Name}.", ex);
                }
            }
            rows.Add((T)schema.Constructor.Invoke(args));
        }
        return rows;
    }

    /// <summary>Column name for a record parameter: <c>ClosePreEntry</c> becomes <c>close_pre_entry</c>, <c>MedianAbsReturn20</c> becomes <c>median_abs_return20</c>.</summary>
    public static string ColumnName(string parameterName)
    {
        var sb = new StringBuilder(parameterName.Length + 4);
        for (var i = 0; i < parameterName.Length; i++)
        {
            var c = parameterName[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    internal static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        decimal d => d.ToString(Invariant),
        DateOnly d => d.ToString("yyyy-MM-dd", Invariant),
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss", Invariant),
        DateTimeOffset d => d.ToString("o", Invariant),
        IFormattable f => f.ToString(null, Invariant),
        _ => value.ToString() ?? ""
    };

    internal static object? Parse(string cell, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return cell.Length == 0 ? null : Parse(cell, underlying);
        }

        if (type == typeof(string)) return cell;
        if (cell.Length == 0) throw new InvalidDataException("empty cell for a non-nullable column");
        if (type == typeof(bool)) return cell switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FormatException("expected true or false")
        };
        if (type == typeof(decimal)) return decimal.Parse(cell, NumberStyles.Number, Invariant);
        if (type == typeof(int)) return int.Parse(cell, NumberStyles.Integer, Invariant);
        if (type == typeof(long)) return long.Parse(cell, NumberStyles.Integer, Invariant);
        if (type == typeof(DateOnly)) return DateOnly.ParseExact(cell, "yyyy-MM-dd", Invariant);
        if (type == typeof(DateTime)) return DateTime.ParseExact(cell, "yyyy-MM-ddTHH:mm:ss", Invariant, DateTimeStyles.None);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(cell, Invariant, DateTimeStyles.RoundtripKind);
        throw new InvalidDataException($"no CSV mapping for {type.Name}");
    }

    internal static string Quote(string field)
    {
        if (field.Length == 0) return field;
        var needsQuotes = field.IndexOfAny([',', '"', '\r', '\n']) >= 0 || field[0] == ' ' || field[^1] == ' ';
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }

    /// <summary>RFC 4180 records: quoted fields may contain commas, doubled quotes and line breaks.</summary>
    internal static List<string[]> ParseRecords(string content)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        // A trailing newline must not produce a phantom empty record.
        while (i < content.Length)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\r':
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add([.. fields]);
                    fields.Clear();
                    i++;
                    if (c == '\r' && i < content.Length && content[i] == '\n') i++;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        if (inQuotes) throw new InvalidDataException("unterminated quoted field at end of file");
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add([.. fields]);
        }
        return records;
    }

    private sealed class Schema<T>
    {
        public static readonly Schema<T> Instance = new();

        public ConstructorInfo Constructor { get; }
        public string[] Columns { get; }
        public Type[] ParameterTypes { get; }

        /// <summary>
        /// A <c>string?</c> parameter reads an empty cell as null; a <c>string</c> parameter reads it
        /// as the empty string. Reference nullability is an annotation, not a runtime type, so it
        /// is read from the constructor parameter rather than from <see cref="Nullable"/>.
        /// </summary>
        public bool[] NullableStrings { get; }
        public Func<object, object?>[] Getters { get; }

        private Schema()
        {
            // A positional record has exactly one public constructor whose parameters mirror its
            // properties; the protected copy constructor is not public and is not considered.
            Constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();
            var parameters = Constructor.GetParameters();
            Columns = [.. parameters.Select(p => ColumnName(p.Name!))];
            ParameterTypes = [.. parameters.Select(p => p.ParameterType)];
            var nullability = new NullabilityInfoContext();
            NullableStrings = [.. parameters.Select(p =>
                p.ParameterType == typeof(string) && nullability.Create(p).WriteState == NullabilityState.Nullable)];
            Getters = [.. parameters.Select(p =>
            {
                var property = typeof(T).GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"{typeof(T).Name}: constructor parameter '{p.Name}' has no matching property.");
                return new Func<object, object?>(o => property.GetValue(o));
            })];
        }
    }
}
