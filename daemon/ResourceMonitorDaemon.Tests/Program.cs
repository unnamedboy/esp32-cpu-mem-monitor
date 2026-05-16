using ResourceMonitorDaemon;

var tests = new (string Name, Action Run)[]
{
    ("FormatPercentPayload uses invariant one-decimal formatting", () =>
    {
        AssertEqual("0.0", MetricPayloadFormatter.FormatPercentPayload(0));
        AssertEqual("42.3", MetricPayloadFormatter.FormatPercentPayload(42.26));
        AssertEqual("100.0", MetricPayloadFormatter.FormatPercentPayload(100));
    }),
    ("FormatPercentPayload clamps out-of-range values", () =>
    {
        AssertEqual("0.0", MetricPayloadFormatter.FormatPercentPayload(-0.1));
        AssertEqual("100.0", MetricPayloadFormatter.FormatPercentPayload(100.1));
    }),
};

var failed = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void AssertEqual(string expected, string actual)
{
    if (expected != actual)
    {
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
    }
}
