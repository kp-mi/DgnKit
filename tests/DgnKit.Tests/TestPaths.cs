namespace DgnKit.Tests;

internal static class TestPaths
{
    public static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "DgnKit.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    public static string TestData => Path.Combine(RepoRoot, "testdata");
    public static string Corpus => Path.Combine(TestData, "corpus");
    public static string Oracle => Path.Combine(TestData, "oracle");
    public static string Reports => Directory.CreateDirectory(Path.Combine(TestData, "reports")).FullName;

    /// <summary>Maps an oracle JSON back to its corpus DGN by base name.</summary>
    public static string? DgnForOracle(string oracleJson)
    {
        string name = Path.GetFileName(oracleJson).Replace(".oracle.json", "", StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(Corpus)) return null;
        return Directory.EnumerateFiles(Corpus, "*", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.EndsWith(".dgn", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase));
    }
}
