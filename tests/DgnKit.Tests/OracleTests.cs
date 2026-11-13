using Xunit;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>
/// Compares DgnKit against MicroStation-generated oracle dumps (testdata/oracle/*.oracle.json, produced by
/// tools/oracle/mstn-oracle.ps1). Skips silently when the corpus/oracle has not been fetched.
/// </summary>
public class OracleTests
{
    private readonly ITestOutputHelper _out;
    public OracleTests(ITestOutputHelper output) { _out = output; }

    public static IEnumerable<object[]> OracleFiles() =>
        Directory.Exists(TestPaths.Oracle)
            ? Directory.EnumerateFiles(TestPaths.Oracle, "*.oracle.json").OrderBy(x => x).Select(x => new object[] { Path.GetFileName(x) })
            : Array.Empty<object[]>();

    [Fact]
    public void AllOracleFiles_MatchCoreFields()
    {
        var results = new List<OracleComparison>();
        foreach (var o in Directory.Exists(TestPaths.Oracle) ? Directory.EnumerateFiles(TestPaths.Oracle, "*.oracle.json").OrderBy(x => x) : Enumerable.Empty<string>())
        {
            var dgn = TestPaths.DgnForOracle(o);
            if (dgn == null) continue;
            // Only dumps from the typed oracle (MstnOracle) carry reliable coordinates and per-model units.
            using (var sr = new StreamReader(o)) { var head = new char[120]; sr.Read(head, 0, head.Length); if (!new string(head).Contains("typed")) continue; }
            results.Add(OracleComparer.Compare(dgn, o));
        }
        if (results.Count == 0) return; // corpus not present
        var report = OracleComparer.Report(results);
        File.WriteAllText(Path.Combine(TestPaths.Reports, "oracle-report.md"), report);
        _out.WriteLine(report);

        // Core decode must be essentially exact; looser thresholds for fields still under reverse engineering.
        Assert.All(results, r => Assert.True(r.Rate("element.found") >= 0.99, $"{r.File}: element.found {r.Rate("element.found"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("element.type") >= 0.99, $"{r.File}: element.type {r.Rate("element.type"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("element.color") >= 0.98, $"{r.File}: element.color {r.Rate("element.color"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("element.weight") >= 0.98, $"{r.File}: element.weight {r.Rate("element.weight"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("model.uorPerMaster") >= 0.99, $"{r.File}: model.uorPerMaster"));
        Assert.All(results, r => Assert.True(r.Rate("geometry.vertices") >= 0.98, $"{r.File}: geometry.vertices {r.Rate("geometry.vertices"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("text.string") >= 0.98, $"{r.File}: text.string {r.Rate("text.string"):P1}"));
        // Dimensions (type 33): definition points/type/values exact; displayed text (incl. overrides and text blocks).
        Assert.All(results, r => Assert.True(r.Rate("dimension.type") >= 0.99, $"{r.File}: dimension.type {r.Rate("dimension.type"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("dimension.points") >= 0.99, $"{r.File}: dimension.points {r.Rate("dimension.points"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("dimension.value") >= 0.99, $"{r.File}: dimension.value {r.Rate("dimension.value"):P1}"));
        Assert.All(results, r => Assert.True(r.Rate("dimension.text") >= 0.95, $"{r.File}: dimension.text {r.Rate("dimension.text"):P1}"));
    }
}
