namespace RelayStack.Platform.Processing;

public sealed record CsvLimits(int MaxFieldChars, int MaxFieldsPerRecord, int MaxRecordChars, long MaxRecords)
{
    public static CsvLimits FromOptions(ProcessingOptions o) => new(o.MaxFieldChars, o.MaxFieldsPerRecord, o.MaxRecordChars, o.MaxRecordsPerFile);
}

/// <summary>A structural problem that makes the rest of the file unreadable. Processing of the file stops.</summary>
public sealed class CsvFormatException(string code, string message, long line) : Exception(message)
{
    public string Code { get; } = code;
    public long Line { get; } = line;
}

public sealed record CsvRecord(long RecordNumber, long StartLine, IReadOnlyList<string> Fields);

/// <summary>
/// Streaming RFC 4180 reader. Reads one record at a time with bounded memory:
/// quoted fields, escaped quotes (""), embedded newlines, CRLF/LF/CR line endings, completely empty lines skipped.
/// Every dimension is capped by <see cref="CsvLimits"/> so a hostile file cannot exhaust memory.
/// Deliberately strict: a stray quote inside an unquoted field or text after a closing quote is a format error,
/// because guessing intent there silently corrupts data.
/// </summary>
public sealed class CsvStreamReader
{
    private readonly TextReader _reader;
    private readonly CsvLimits _limits;
    private readonly char[] _buffer = new char[16 * 1024];
    private int _pos;
    private int _len;
    private long _line = 1;
    private long _records;
    private bool _atStart = true;

    public CsvStreamReader(TextReader reader, CsvLimits limits)
    {
        _reader = reader;
        _limits = limits;
    }

    public async ValueTask<CsvRecord?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var record = await ReadRawAsync(ct);
            if (record is null)
            {
                return null;
            }

            // Skip lines that are completely empty (no delimiter, no quotes).
            if (record.Fields.Count == 1 && record.Fields[0].Length == 0 && !_lastRecordHadQuotes)
            {
                continue;
            }

            _records++;
            if (_records > _limits.MaxRecords)
            {
                throw new CsvFormatException("too_many_records", $"File exceeds the limit of {_limits.MaxRecords} records.", record.StartLine);
            }

            return record with { RecordNumber = _records };
        }
    }

    private bool _lastRecordHadQuotes;

    private async ValueTask<CsvRecord?> ReadRawAsync(CancellationToken ct)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var startLine = _line;
        var recordChars = 0;
        var inQuotes = false;
        var fieldWasQuoted = false;
        var afterClosingQuote = false;
        var sawAnything = false;
        _lastRecordHadQuotes = false;

        while (true)
        {
            if (_pos >= _len)
            {
                ct.ThrowIfCancellationRequested();
                _len = await _reader.ReadAsync(_buffer.AsMemory(), ct);
                _pos = 0;
                if (_len == 0)
                {
                    if (inQuotes)
                    {
                        throw new CsvFormatException("unterminated_quote", "File ends inside a quoted field.", startLine);
                    }

                    if (!sawAnything)
                    {
                        return null;
                    }

                    fields.Add(field.ToString());
                    return new CsvRecord(0, startLine, fields);
                }

                if (_atStart)
                {
                    _atStart = false;
                    if (_buffer[0] == '﻿')
                    {
                        _pos = 1; // UTF-8 byte order mark
                    }
                }
            }

            var c = _buffer[_pos++];
            sawAnything = true;

            if (++recordChars > _limits.MaxRecordChars)
            {
                throw new CsvFormatException("record_too_long", $"A record exceeds {_limits.MaxRecordChars} characters.", startLine);
            }

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (await PeekAsync(ct) == '"')
                    {
                        _pos++;
                        Append(field, '"', startLine);
                    }
                    else
                    {
                        inQuotes = false;
                        afterClosingQuote = true;
                    }
                }
                else
                {
                    if (c == '\n' || (c == '\r' && await PeekAsync(ct) != '\n'))
                    {
                        _line++;
                    }

                    Append(field, c, startLine);
                }

                continue;
            }

            switch (c)
            {
                case ',':
                    AddField(fields, field, startLine);
                    fieldWasQuoted = false;
                    afterClosingQuote = false;
                    break;
                case '\r':
                case '\n':
                    if (c == '\r' && await PeekAsync(ct) == '\n')
                    {
                        _pos++;
                    }

                    _line++;
                    fields.Add(field.ToString());
                    return new CsvRecord(0, startLine, fields);
                case '"':
                    if (field.Length > 0 || afterClosingQuote || fieldWasQuoted)
                    {
                        throw new CsvFormatException("unexpected_quote", $"Unexpected quote character on line {_line}.", _line);
                    }

                    inQuotes = true;
                    fieldWasQuoted = true;
                    _lastRecordHadQuotes = true;
                    break;
                default:
                    if (afterClosingQuote)
                    {
                        throw new CsvFormatException("text_after_quote", $"Unexpected text after a closing quote on line {_line}.", _line);
                    }

                    Append(field, c, startLine);
                    break;
            }
        }
    }

    private void AddField(List<string> fields, System.Text.StringBuilder field, long startLine)
    {
        fields.Add(field.ToString());
        field.Clear();
        if (fields.Count >= _limits.MaxFieldsPerRecord)
        {
            throw new CsvFormatException("too_many_fields", $"A record exceeds {_limits.MaxFieldsPerRecord} fields.", startLine);
        }
    }

    private void Append(System.Text.StringBuilder field, char c, long startLine)
    {
        if (field.Length >= _limits.MaxFieldChars)
        {
            throw new CsvFormatException("field_too_long", $"A field exceeds {_limits.MaxFieldChars} characters.", startLine);
        }

        field.Append(c);
    }

    private async ValueTask<int> PeekAsync(CancellationToken ct)
    {
        if (_pos >= _len)
        {
            _len = await _reader.ReadAsync(_buffer.AsMemory(), ct);
            _pos = 0;
            if (_len == 0)
            {
                return -1;
            }
        }

        return _buffer[_pos];
    }
}
