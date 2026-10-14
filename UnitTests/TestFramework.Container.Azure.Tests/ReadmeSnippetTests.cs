using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TestFramework.Container.Azure.Readme.GoldenSample;
using Xunit;

namespace TestFramework.Container.Azure.Tests;

/// <summary>
/// The package documents' C# is code that compiles: every block is a region of the README project, and says which.
/// </summary>
/// <remarks>
/// The samples used to be prose. One named a helper with the wrong number of type parameters, the golden sample
/// called two APIs no package in the family has, and nothing noticed, because nothing compiled them. A copy of
/// each sample in a test, kept in step by a comment asking whoever edits one to edit the other, is the same
/// promise with one more place to forget it. Here the README is compared with the code the build compiles, so a
/// sample that stops compiling breaks the build, and a sample that drifts from the code breaks this test.
/// </remarks>
public class ReadmeSnippetTests
{
    private static readonly Regex SnippetMarker = new(@"^<!-- snippet: (?<name>[a-z0-9-]+) -->$", RegexOptions.Compiled);
    private static readonly Regex RegionStart = new(@"^\s*#region snippet:(?<name>[a-z0-9-]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex RegionEnd = new(@"^\s*#endregion\s*$", RegexOptions.Compiled);

    [Fact]
    public void EveryCSharpBlockInTheReadmeNamesTheCompiledSnippetItShows()
    {
        IReadOnlyList<ReadmeBlock> blocks = ReadmeBlocks();

        string[] unmarked = [.. blocks.Where(block => block.Name is null).Select(block => block.Line)];
        Assert.True(unmarked.Length == 0, $"C# blocks with no '<!-- snippet: name -->' line above them: {string.Join(", ", unmarked)}. Put the code in the README project and name it.");
    }

    [Fact]
    public void EveryCSharpBlockInTheReadmeIsTheCodeItsSnippetCompiles()
    {
        IReadOnlyDictionary<string, string> regions = Regions();
        List<string> drifted = [];

        foreach (ReadmeBlock block in ReadmeBlocks().Where(block => block.Name is not null))
        {
            if (!regions.TryGetValue(block.Name!, out string? region))
                drifted.Add($"'{block.Name}' ({block.Line}) has no '#region snippet:{block.Name}' in the README project");
            else if (Normalized(region) != Normalized(block.Code))
                drifted.Add($"'{block.Name}' ({block.Line}) differs from its region");
        }

        Assert.True(drifted.Count == 0, string.Join(Environment.NewLine, drifted));
    }

    [Fact]
    public void EverySnippetInTheReadmeProjectIsShownInTheReadme()
    {
        HashSet<string> shown = [.. ReadmeBlocks().Select(block => block.Name).OfType<string>()];
        string[] orphaned = [.. Regions().Keys.Where(name => !shown.Contains(name)).Order(StringComparer.Ordinal)];

        Assert.True(orphaned.Length == 0, $"Snippets no document shows: {string.Join(", ", orphaned)}");
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task TheGoldenSampleRunsAsWritten()
        => await new ContainerAzureSample().Timeline_runs_against_an_emulated_storage_account();

    /// <summary>The package's documents whose C# is held to this.</summary>
    private static readonly string[] Documents = ["README.md", Path.Combine("Documentation", "Architecture.md")];

    private static IReadOnlyList<ReadmeBlock> ReadmeBlocks()
        => [.. Documents.SelectMany(document => BlocksOf(document))];

    private static IEnumerable<ReadmeBlock> BlocksOf(string document)
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "TestFramework.Container.Azure", document));
        List<ReadmeBlock> blocks = [];

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "```csharp")
                continue;

            string? name = i > 0 && SnippetMarker.Match(lines[i - 1].Trim()) is { Success: true } marker ? marker.Groups["name"].Value : null;
            int end = Array.FindIndex(lines, i + 1, line => line.Trim() == "```");
            blocks.Add(new ReadmeBlock(name, $"{document}:{i + 1}", string.Join('\n', lines[(i + 1)..end])));
            i = end;
        }

        return blocks;
    }

    /// <summary>
    /// Each snippet's code, its regions joined in file order - so a sample can show usings and types that a
    /// namespace line separates in the file.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Regions()
    {
        Dictionary<string, List<string>> regions = new(StringComparer.Ordinal);
        string project = Path.Combine(RepositoryRoot(), "UnitTests", "TestFramework.Container.Azure.Readme");

        foreach (string file in Directory.GetFiles(project, "*.cs").Order(StringComparer.Ordinal))
        {
            List<string>? current = null;
            foreach (string line in File.ReadAllLines(file))
            {
                if (RegionStart.Match(line) is { Success: true } start)
                {
                    string name = start.Groups["name"].Value;
                    current = regions.TryGetValue(name, out List<string>? existing) ? existing : regions[name] = [];
                }
                else if (RegionEnd.IsMatch(line))
                {
                    current = null;
                }
                else
                {
                    current?.Add(line);
                }
            }
        }

        return regions.ToDictionary(pair => pair.Key, pair => string.Join('\n', pair.Value), StringComparer.Ordinal);
    }

    /// <summary>Code without indentation or blank lines: what a reader copies, not how either file lays it out.</summary>
    private static string Normalized(string code)
        => string.Join('\n', code.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TestFramework.Container.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException($"No TestFramework.Container.slnx above '{AppContext.BaseDirectory}'.");
    }

    private sealed record ReadmeBlock(string? Name, string Line, string Code);
}
