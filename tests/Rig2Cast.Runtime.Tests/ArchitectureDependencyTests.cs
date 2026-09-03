using System.Xml.Linq;

namespace Rig2Cast.Runtime.Tests;

public sealed class ArchitectureDependencyTests
{
    [Fact]
    public void DriverProjectsDoNotReferenceHostAdapterOrRuntimeProjects()
    {
        string repository = FindRepositoryRoot();
        string[] projects =
        [
            Path.Combine(repository, "src", "Rig2Cast.Drivers.Yaesu", "Rig2Cast.Drivers.Yaesu.csproj"),
            Path.Combine(repository, "src", "Rig2Cast.Drivers.Elecraft", "Rig2Cast.Drivers.Elecraft.csproj"),
            Path.Combine(repository, "src", "Rig2Cast.Drivers.Icom", "Rig2Cast.Drivers.Icom.csproj"),
            Path.Combine(repository, "src", "Rig2Cast.Drivers.Xiegu", "Rig2Cast.Drivers.Xiegu.csproj"),
            Path.Combine(repository, "samples", "Rig2Cast.ExamplePlugin", "Rig2Cast.ExamplePlugin.csproj"),
            Path.Combine(repository, "samples", "Rig2Cast.DeclarativeExamplePlugin",
                "Rig2Cast.DeclarativeExamplePlugin.csproj")
        ];
        string[] forbidden = ["Adapters", "PluginHost", "Runtime", "Server", "samples\\Rig2Cast.Console"];

        foreach (string project in projects)
        {
            string[] references = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray();
            Assert.DoesNotContain(references, reference =>
                forbidden.Any(segment => reference.Contains(segment, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rig2Cast.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Rig2Cast repository root.");
    }
}
