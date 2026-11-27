using RelayStack.Platform.Processing;

namespace RelayStack.UnitTests;

public class RecordValidatorTests
{
    private static ValidationSchema Schema(string unknown = "ignore") => new()
    {
        UnknownColumns = unknown,
        Columns =
        [
            new() { Name = "employee_id", Type = ColumnTypes.String, Required = true, MaxLength = 10 },
            new() { Name = "email", Type = ColumnTypes.Email, Required = true },
            new() { Name = "hours", Type = ColumnTypes.Decimal },
            new() { Name = "age", Type = ColumnTypes.Integer },
            new() { Name = "active", Type = ColumnTypes.Boolean },
            new() { Name = "start_date", Type = ColumnTypes.Date },
            new() { Name = "dept", Type = ColumnTypes.String, AllowedValues = ["ENG", "OPS"] },
        ],
    };

    private static readonly string[] Header = ["employee_id", "email", "hours", "age", "active", "start_date", "dept"];

    [Fact]
    public void Converts_a_valid_row_to_typed_json()
    {
        var v = new RecordValidator(Schema());
        v.BindHeader(Header);
        var errors = new List<FieldError>();
        var obj = v.Validate(["E001", "ana@example.com", "37.5", "41", "yes", "2024-03-01", "ENG"], errors);
        Assert.Empty(errors);
        Assert.Equal("""{"employee_id":"E001","email":"ana@example.com","hours":37.5,"age":41,"active":true,"start_date":"2024-03-01","dept":"ENG"}""",
            obj!.ToJsonString());
    }

    [Fact]
    public void Optional_empty_values_become_null()
    {
        var v = new RecordValidator(Schema());
        v.BindHeader(Header);
        var obj = v.Validate(["E001", "ana@example.com", "", "", "", "", ""], []);
        Assert.Equal("""{"employee_id":"E001","email":"ana@example.com","hours":null,"age":null,"active":null,"start_date":null,"dept":null}""", obj!.ToJsonString());
    }

    [Fact]
    public void Reports_every_invalid_field_with_a_code()
    {
        var v = new RecordValidator(Schema());
        v.BindHeader(Header);
        var errors = new List<FieldError>();
        var obj = v.Validate(["E0000000001", "not-an-email", "1,5", "4.2", "maybe", "03/01/2024", "HR"], errors);
        Assert.Null(obj);
        Assert.Equal(new[] { "max_length", "invalid_email", "invalid_decimal", "invalid_integer", "invalid_boolean", "invalid_date", "not_allowed" },
            errors.Select(e => e.Code).ToList());
    }

    [Fact]
    public void Required_field_missing_is_reported()
    {
        var v = new RecordValidator(Schema());
        v.BindHeader(Header);
        var errors = new List<FieldError>();
        Assert.Null(v.Validate(["", "a@b.co", "", "", "", "", ""], errors));
        Assert.Equal("required", Assert.Single(errors).Code);
    }

    [Fact]
    public void Field_count_mismatch_is_a_row_error()
    {
        var v = new RecordValidator(Schema());
        v.BindHeader(Header);
        var errors = new List<FieldError>();
        Assert.Null(v.Validate(["E1", "a@b.co"], errors));
        Assert.Equal("field_count", Assert.Single(errors).Code);
    }

    [Fact]
    public void Header_columns_can_be_in_any_order_and_extra_columns_are_ignored()
    {
        var v = new RecordValidator(Schema());
        v.BindHeader(["notes", "email", "employee_id"]);
        var obj = v.Validate(["ignored", "a@b.co", "E9"], []);
        Assert.Equal("E9", obj!["employee_id"]!.GetValue<string>());
        Assert.False(obj.ContainsKey("notes"));
    }

    [Fact]
    public void Missing_required_header_is_a_file_error()
    {
        var v = new RecordValidator(Schema());
        var ex = Assert.Throws<SchemaMismatchException>(() => v.BindHeader(["employee_id"]));
        Assert.Equal("missing_columns", ex.Code);
    }

    [Fact]
    public void Unknown_columns_rejected_when_configured()
    {
        var v = new RecordValidator(Schema("reject"));
        var ex = Assert.Throws<SchemaMismatchException>(() => v.BindHeader(["employee_id", "email", "salary"]));
        Assert.Equal("unknown_columns", ex.Code);
    }

    [Fact]
    public void Duplicate_header_columns_are_a_file_error()
    {
        var v = new RecordValidator(Schema());
        Assert.Equal("duplicate_columns", Assert.Throws<SchemaMismatchException>(() => v.BindHeader(["email", "email", "employee_id"])).Code);
    }

    [Theory]
    [InlineData("2024-05-01T10:00:00Z", true)]
    [InlineData("2024-05-01T10:00:00+02:00", true)]
    [InlineData("2024-05-01T10:00:00-05:00", true)]
    [InlineData("2024-05-01T10:00:00", false)]
    [InlineData("May 1 2024", false)]
    public void Datetime_requires_an_offset(string value, bool ok)
    {
        var v = new RecordValidator(new ValidationSchema { Columns = [new() { Name = "t", Type = ColumnTypes.DateTime }] });
        v.BindHeader(["t"]);
        Assert.Equal(ok, v.Validate([value], []) is not null);
    }

    [Fact]
    public void Schema_validation_catches_configuration_mistakes()
    {
        var schema = new ValidationSchema
        {
            UnknownColumns = "maybe",
            Columns = [new() { Name = "a", Type = "money" }, new() { Name = "a", MaxLength = 0 }, new() { Name = " b " }],
        };
        var errors = schema.Validate();
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void Schema_round_trips_through_json()
    {
        var json = Schema().ToJson();
        var back = ValidationSchema.FromJson(json);
        Assert.Equal(7, back.Columns.Count);
        Assert.Equal(new[] { "ENG", "OPS" }, back.Columns[6].AllowedValues!);
        Assert.DoesNotContain("allowed_values\":null", json);
        Assert.Contains("\"unknown_columns\":\"ignore\"", json);
    }
}
