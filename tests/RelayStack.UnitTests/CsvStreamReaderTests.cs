using RelayStack.Platform.Processing;

namespace RelayStack.UnitTests;

public class CsvStreamReaderTests
{
    private static readonly CsvLimits Limits = new(MaxFieldChars: 100, MaxFieldsPerRecord: 10, MaxRecordChars: 500, MaxRecords: 1000);

    private static async Task<List<List<string>>> ReadAll(string text, CsvLimits? limits = null)
    {
        var reader = new CsvStreamReader(new StringReader(text), limits ?? Limits);
        var rows = new List<List<string>>();
        while (await reader.ReadAsync(CancellationToken.None) is { } r)
        {
            rows.Add(r.Fields.ToList());
        }

        return rows;
    }

    [Fact]
    public async Task Parses_simple_rows_with_lf_and_crlf()
    {
        var rows = await ReadAll("a,b,c\r\n1,2,3\n4,5,6");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "a", "b", "c" }, rows[0]);
        Assert.Equal(new[] { "4", "5", "6" }, rows[2]);
    }

    [Fact]
    public async Task Handles_quoted_fields_escaped_quotes_and_embedded_newlines()
    {
        var rows = await ReadAll("name,note\n\"Smith, Jane\",\"said \"\"hi\"\"\nthen left\"\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Smith, Jane", rows[1][0]);
        Assert.Equal("said \"hi\"\nthen left", rows[1][1]);
    }

    [Fact]
    public async Task Preserves_empty_fields_and_skips_blank_lines()
    {
        var rows = await ReadAll("a,b,c\n\n1,,3\n,,\n\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "1", "", "3" }, rows[1]);
        Assert.Equal(new[] { "", "", "" }, rows[2]);
    }

    [Fact]
    public async Task Keeps_a_quoted_empty_line_as_a_record()
    {
        var rows = await ReadAll("a\n\"\"\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "" }, rows[1]);
    }

    [Fact]
    public async Task Strips_utf8_byte_order_mark()
    {
        var rows = await ReadAll("﻿id,name\n1,x\n");
        Assert.Equal("id", rows[0][0]);
    }

    [Fact]
    public async Task Record_numbers_and_start_lines_account_for_multiline_fields()
    {
        var reader = new CsvStreamReader(new StringReader("h\n\"a\nb\"\nc\n"), Limits);
        var r1 = await reader.ReadAsync(CancellationToken.None);
        var r2 = await reader.ReadAsync(CancellationToken.None);
        var r3 = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(2, r2!.RecordNumber);
        Assert.Equal(2, r2.StartLine);
        Assert.Equal(3, r3!.RecordNumber);
        Assert.Equal(4, r3.StartLine);
        Assert.Equal(1, r1!.StartLine);
    }

    [Theory]
    [InlineData("a,b\n\"unterminated\n", "unterminated_quote")]
    [InlineData("a,b\nab\"c,d\n", "unexpected_quote")]
    [InlineData("a,b\n\"ab\"c,d\n", "text_after_quote")]
    public async Task Rejects_malformed_quoting(string text, string code)
    {
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => ReadAll(text));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Enforces_field_length_limit()
    {
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => ReadAll("a\n" + new string('x', 101) + "\n"));
        Assert.Equal("field_too_long", ex.Code);
    }

    [Fact]
    public async Task Enforces_field_count_limit()
    {
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => ReadAll(string.Join(',', Enumerable.Range(0, 11)) + "\n"));
        Assert.Equal("too_many_fields", ex.Code);
    }

    [Fact]
    public async Task Exactly_the_maximum_field_count_is_allowed()
    {
        var rows = await ReadAll(string.Join(',', Enumerable.Range(0, 10)) + "\n");
        Assert.Equal(10, rows[0].Count);
    }

    [Fact]
    public async Task Enforces_record_length_limit()
    {
        var line = string.Join(',', Enumerable.Range(0, 9).Select(_ => new string('y', 60)));
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => ReadAll(line + "\n"));
        Assert.Equal("record_too_long", ex.Code);
    }

    [Fact]
    public async Task Enforces_record_count_limit()
    {
        var limits = Limits with { MaxRecords = 3 };
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => ReadAll("h\n1\n2\n3\n", limits));
        Assert.Equal("too_many_records", ex.Code);
    }

    [Fact]
    public async Task Streams_large_input_with_bounded_buffers()
    {
        var sb = new System.Text.StringBuilder("id,value\n");
        for (var i = 0; i < 50_000; i++)
        {
            sb.Append(i).Append(",\"v,").Append(i).Append("\"\n");
        }

        var reader = new CsvStreamReader(new StringReader(sb.ToString()), Limits with { MaxRecords = 100_000 });
        long count = 0;
        CsvRecord? last = null;
        while (await reader.ReadAsync(CancellationToken.None) is { } r)
        {
            count++;
            last = r;
        }

        Assert.Equal(50_001, count);
        Assert.Equal("v,49999", last!.Fields[1]);
    }
}
