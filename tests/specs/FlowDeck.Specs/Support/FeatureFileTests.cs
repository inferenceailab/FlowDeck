namespace FlowDeck.Specs.Support;

/// <summary>
/// Constraints on the feature files themselves.
/// </summary>
/// <remarks>
/// Ordinary xUnit tests, not scenarios: these are about the specification
/// documents rather than about FlowDeck, and writing them as Gherkin would be
/// a specification of the specifications.
/// </remarks>
public class FeatureFileTests
{
    /// <summary>
    /// The <c>Features</c> directory in the source tree.
    /// </summary>
    /// <remarks>
    /// Found by walking up from the build output to the project file, rather
    /// than by copying the feature files alongside the assembly. Copying them
    /// put a second set under <c>bin</c>, and an incremental build then
    /// compiled every scenario twice - the suite reported 42 tests for 23
    /// scenarios and was perfectly green about it.
    ///
    /// <para>
    /// Reading the source files also means these tests check what a reviewer
    /// sees in the repository, not a stale copy of it.
    /// </para>
    /// </remarks>
    private static string FeaturesRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlowDeck.Specs.csproj")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Could not locate FlowDeck.Specs.csproj from the test output path.")
            : Path.Combine(directory.FullName, "Features");
    }

    private static IReadOnlyList<string> FeatureFiles() =>
        [.. Directory.EnumerateFiles(FeaturesRoot(), "*.feature", SearchOption.AllDirectories)];

    [Fact]
    public void There_is_at_least_one_feature_file()
    {
        // Guards every other test here: they all pass trivially against an
        // empty directory, which is exactly the state this project was in
        // before #131.
        Assert.NotEmpty(FeatureFiles());
    }

    [Fact]
    public void Every_scenario_is_tagged_with_the_issue_that_asked_for_it()
    {
        // The link back to the issue is the only traceability these files have.
        // Without it a scenario is unattributable, and nobody can tell whether
        // it came from an accepted story or from someone's assumption.
        var untagged = new List<string>();

        foreach (var path in FeatureFiles())
        {
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("Scenario", StringComparison.Ordinal))
                {
                    continue;
                }

                // Tags sit on the lines immediately above, after any blank line.
                var tagged = false;

                for (var j = i - 1; j >= 0; j--)
                {
                    var above = lines[j].Trim();

                    if (above.Length == 0)
                    {
                        continue;
                    }

                    tagged = above.StartsWith("@issue-", StringComparison.Ordinal);
                    break;
                }

                if (!tagged)
                {
                    untagged.Add($"{Path.GetFileName(path)}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(untagged);
    }

    [Fact]
    public void Every_feature_file_declares_a_milestone()
    {
        var missing = FeatureFiles()
            .Where(path => !File.ReadAllText(path).Contains("@M", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_scenario_is_compiled_exactly_once()
    {
        // Reqnroll's incremental generation is not reliable, and it fails
        // silently in both directions. A stale build once produced two
        // generated classes per feature, so every scenario ran twice: 42 tests
        // for 23 scenarios, reported as a clean pass. Later, one feature's
        // code-behind existed on disk but was left out of the compilation, so
        // four API scenarios simply did not run and the suite reported 118
        // green.
        //
        // Counting compiled scenarios against the source is the only place
        // either discrepancy is visible from inside the run.
        //
        // When this fails locally, delete every generated feature code-behind
        // and rebuild. CI checks out clean, so it always generates from
        // scratch and is not exposed to this.
        // Outlines are counted separately: Reqnroll generates a theory for an
        // outline and a fact for a plain scenario, so comparing only facts
        // would leave every Scenario Outline outside the guard - unwatched by
        // the test written to make sure nothing goes unwatched.
        var methods = typeof(FeatureFileTests).Assembly
            .GetTypes()
            .Where(type => type.Name.EndsWith("Feature", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods())
            .Select(method => method.GetCustomAttributes(inherit: false)
                .Select(attribute => attribute.GetType().Name)
                .ToArray())
            .ToArray();

        var facts = methods.Count(names => names.Any(name => name is "FactAttribute" or "SkippableFactAttribute"));
        var theories = methods.Count(names => names.Any(name => name is "TheoryAttribute" or "SkippableTheoryAttribute"));

        var lines = FeatureFiles().SelectMany(File.ReadAllLines).Select(line => line.TrimStart()).ToArray();

        var scenarios = lines.Count(line => line.StartsWith("Scenario:", StringComparison.Ordinal));
        var outlines = lines.Count(line => line.StartsWith("Scenario Outline:", StringComparison.Ordinal));

        Assert.Equal(scenarios, facts);
        Assert.Equal(outlines, theories);
    }

    [Fact]
    public void No_scenario_title_is_duplicated()
    {
        // Two scenarios with one title read as a copy-paste mistake, and a
        // failure report naming only the title becomes ambiguous.
        var titles = FeatureFiles()
            .SelectMany(File.ReadAllLines)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Scenario:", StringComparison.Ordinal))
            .ToArray();

        var duplicated = titles
            .GroupBy(title => title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicated);
    }
}
