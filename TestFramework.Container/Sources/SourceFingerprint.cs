using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TestFramework.Container.Sources;

/// <summary>
/// When the sources an image is built from last changed: the project and every project it references,
/// transitively.
/// </summary>
/// <remarks>
/// <para>
/// Part of what makes two builds the same build. The image cache outlives a run - it is what lets a dozen chapters
/// against one unchanged application share one build - so in a test host that stays up while someone edits the
/// application, a key without the sources handed every later run the image of the first, and said it was fresh.
/// </para>
/// <para>
/// References are read from the project files rather than asked of MSBuild: one query per referenced project would
/// cost seconds on every build decision. A reference added through an imported props file or a condition is not
/// seen; the project's own directory still is, so an edit there is never missed.
/// </para>
/// </remarks>
internal static class SourceFingerprint
{
    /// <summary>Directories whose contents a build writes or tools keep, not sources a person edits.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", ".idea" };

    /// <summary>
    /// The newest write among the sources of a project and its references, as a stable string.
    /// </summary>
    /// <param name="projectPath">The project file.</param>
    public static string Of(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        DateTime newest = DateTime.MinValue;
        foreach (string directory in SourceDirectories(Path.GetFullPath(projectPath)))
        {
            foreach (string file in FilesUnder(directory))
            {
                DateTime written = File.GetLastWriteTimeUtc(file);
                if (written > newest)
                    newest = written;
            }
        }

        return newest.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The directory of the project and of every project it references, each once.
    /// </summary>
    private static IEnumerable<string> SourceDirectories(string projectPath)
    {
        HashSet<string> visited = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Stack<string> pending = new([projectPath]);

        while (pending.Count > 0)
        {
            string project = pending.Pop();
            if (!visited.Add(project) || !File.Exists(project))
                continue;

            foreach (string reference in ReferencesOf(project))
                pending.Push(reference);
        }

        return visited.Where(File.Exists).Select(project => Path.GetDirectoryName(project)!).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReferencesOf(string project)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(project);
        }
        catch (System.Xml.XmlException)
        {
            // A project MSBuild could read and this cannot is still fingerprinted by its own directory.
            return [];
        }

        string directory = Path.GetDirectoryName(project)!;
        return document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(include => Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))));
    }

    private static IEnumerable<string> FilesUnder(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
            yield return file;

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (Ignored.Contains(Path.GetFileName(child)))
                continue;

            foreach (string file in FilesUnder(child))
                yield return file;
        }
    }
}
