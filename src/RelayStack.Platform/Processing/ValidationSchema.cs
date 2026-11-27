using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RelayStack.Platform.Processing;

public static class ColumnTypes
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Email = "email";

    public static readonly IReadOnlyList<string> All = [String, Integer, Decimal, Boolean, Date, DateTime, Email];
}

public sealed class ColumnDefinition
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = ColumnTypes.String;
    public bool Required { get; set; }
    public int? MaxLength { get; set; }
    public List<string>? AllowedValues { get; set; }
}

/// <summary>
/// Explicit, declarative schema for CSV records. No regular expressions and no expressions are supported in
/// Phase 0: that keeps validation linear-time and rules out customer-supplied executable logic.
/// </summary>
public sealed class ValidationSchema
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<ColumnDefinition> Columns { get; set; } = [];

    /// <summary>"ignore" (default) drops columns not in the schema; "reject" fails the file.</summary>
    public string UnknownColumns { get; set; } = "ignore";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ValidationSchema FromJson(string json) =>
        JsonSerializer.Deserialize<ValidationSchema>(json, JsonOptions) ?? throw new InvalidOperationException("Empty schema.");

    /// <summary>Returns validation problems with the schema itself (shown to API callers at inbox creation).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Columns.Count is 0 or > 200)
        {
            errors.Add("columns must contain between 1 and 200 entries.");
        }

        if (UnknownColumns is not ("ignore" or "reject"))
        {
            errors.Add("unknownColumns must be 'ignore' or 'reject'.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Columns)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100 || c.Name != c.Name.Trim())
            {
                errors.Add("column names must be 1-100 characters without surrounding whitespace.");
            }
            else if (!seen.Add(c.Name))
            {
                errors.Add($"column '{c.Name}' is defined more than once.");
            }

            if (!ColumnTypes.All.Contains(c.Type))
            {
                errors.Add($"column '{c.Name}' has unknown type '{c.Type}'. Allowed: {string.Join(", ", ColumnTypes.All)}.");
            }

            if (c.MaxLength is < 1 or > 65536)
            {
                errors.Add($"column '{c.Name}' maxLength must be between 1 and 65536.");
            }

            if (c.AllowedValues is { Count: 0 or > 100 })
            {
                errors.Add($"column '{c.Name}' allowedValues must contain between 1 and 100 entries.");
            }
        }

        return errors;
    }
}

public sealed record FieldError(string? Column, string Code, string Message);

public sealed class SchemaMismatchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Validates rows against a schema and converts valid rows to typed JSON objects.</summary>
public sealed class RecordValidator
{
    private readonly ValidationSchema _schema;
    private int[] _columnIndexes = [];
    private int _headerFieldCount;

    public RecordValidator(ValidationSchema schema) => _schema = schema;

    /// <summary>Maps header names to schema columns. Throws <see cref="SchemaMismatchException"/> for file-level problems.</summary>
    public void BindHeader(IReadOnlyList<string> header)
    {
        var names = header.Select(h => h.Trim()).ToList();
        var duplicates = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new SchemaMismatchException("duplicate_columns", $"Header contains duplicate columns: {string.Join(", ", duplicates)}.");
        }

        var missing = _schema.Columns.Where(c => c.Required && !names.Contains(c.Name)).Select(c => c.Name).ToList();
        if (missing.Count > 0)
        {
            throw new SchemaMismatchException("missing_columns", $"Header is missing required columns: {string.Join(", ", missing)}.");
        }

        if (_schema.UnknownColumns == "reject")
        {
            var unknown = names.Where(n => _schema.Columns.All(c => c.Name != n)).ToList();
            if (unknown.Count > 0)
            {
                throw new SchemaMismatchException("unknown_columns", $"Header contains columns not in the schema: {string.Join(", ", unknown)}.");
            }
        }

        _columnIndexes = _schema.Columns.Select(c => names.IndexOf(c.Name)).ToArray();
        _headerFieldCount = names.Count;
    }

    public JsonObject? Validate(IReadOnlyList<string> fields, List<FieldError> errors)
    {
        errors.Clear();
        if (fields.Count != _headerFieldCount)
        {
            errors.Add(new FieldError(null, "field_count", $"Expected {_headerFieldCount} fields but found {fields.Count}."));
            return null;
        }

        var obj = new JsonObject();
        for (var i = 0; i < _schema.Columns.Count; i++)
        {
            var col = _schema.Columns[i];
            var idx = _columnIndexes[i];
            var raw = idx >= 0 ? fields[idx].Trim() : "";

            if (raw.Length == 0)
            {
                if (col.Required)
                {
                    errors.Add(new FieldError(col.Name, "required", "Value is required."));
                }
                else
                {
                    obj[col.Name] = null;
                }

                continue;
            }

            if (col.MaxLength is { } max && raw.Length > max)
            {
                errors.Add(new FieldError(col.Name, "max_length", $"Value exceeds {max} characters."));
                continue;
            }

            if (col.AllowedValues is { } allowed && !allowed.Contains(raw, StringComparer.Ordinal))
            {
                errors.Add(new FieldError(col.Name, "not_allowed", "Value is not one of the allowed values."));
                continue;
            }

            var node = Convert(col, raw, out var error);
            if (error is not null)
            {
                errors.Add(new FieldError(col.Name, "invalid_" + col.Type, error));
                continue;
            }

            obj[col.Name] = node;
        }

        return errors.Count == 0 ? obj : null;
    }

    private static JsonNode? Convert(ColumnDefinition col, string raw, out string? error)
    {
        error = null;
        var inv = CultureInfo.InvariantCulture;
        switch (col.Type)
        {
            case ColumnTypes.String:
                return JsonValue.Create(raw);
            case ColumnTypes.Integer:
                if (long.TryParse(raw, NumberStyles.AllowLeadingSign, inv, out var l))
                {
                    return JsonValue.Create(l);
                }

                error = "Value is not a whole number.";
                return null;
            case ColumnTypes.Decimal:
                if (decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, inv, out var d))
                {
                    return JsonValue.Create(d);
                }

                error = "Value is not a decimal number (use '.' as the decimal separator, no thousands separators).";
                return null;
            case ColumnTypes.Boolean:
                switch (raw.ToLowerInvariant())
                {
                    case "true" or "1" or "yes" or "y":
                        return JsonValue.Create(true);
                    case "false" or "0" or "no" or "n":
                        return JsonValue.Create(false);
                    default:
                        error = "Value is not a boolean (true/false, yes/no, 1/0).";
                        return null;
                }

            case ColumnTypes.Date:
                if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", inv, DateTimeStyles.None, out var date))
                {
                    return JsonValue.Create(date.ToString("yyyy-MM-dd", inv));
                }

                error = "Value is not a date in yyyy-MM-dd format.";
                return null;
            case ColumnTypes.DateTime:
                if (DateTimeOffset.TryParseExact(raw, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], inv, DateTimeStyles.None, out var dt)
                    && (raw.EndsWith('Z') || raw.Contains('+') || raw.LastIndexOf('-') > 9))
                {
                    return JsonValue.Create(dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", inv));
                }

                error = "Value is not an ISO 8601 timestamp with a UTC offset.";
                return null;
            case ColumnTypes.Email:
                if (IsPlausibleEmail(raw))
                {
                    return JsonValue.Create(raw);
                }

                error = "Value is not an email address.";
                return null;
            default:
                error = "Unsupported type.";
                return null;
        }
    }

    private static bool IsPlausibleEmail(string s)
    {
        if (s.Length > 254 || s.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@') || at == s.Length - 1)
        {
            return false;
        }

        var domain = s[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') && !domain.Contains("..", StringComparison.Ordinal);
    }
}
