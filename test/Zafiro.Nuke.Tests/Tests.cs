using System.IO.Compression;
using Microsoft.Build.Locator;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.GitVersion;

namespace Zafiro.Nuke.Tests;

public class Tests
{
    [Fact]
    public async Task Pack_windows()
    {
        AppContext.SetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", isEnabled: true);
        MSBuildLocator.RegisterDefaults();
        var currentDirectory = (AbsolutePath)Directory.GetCurrentDirectory();
        var solutionDir = currentDirectory / "Sample";
        
        Directory.Delete(solutionDir, true);
        ZipFile.ExtractToDirectory("SampleSolution.zip", solutionDir);
        
        var solution = SolutionModelTasks.ParseSolution(solutionDir / "Sample.sln");
        var projects = solution.Projects;

        Actions actions = new Actions(solution, null, currentDirectory, new GitVersion());
        var file = await actions.CreateWindowsPacks(solution.GetProject("SampleProject"));
    }
}