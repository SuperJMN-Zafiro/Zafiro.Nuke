using System.IO.Compression;
using Microsoft.Build.Locator;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.GitVersion;
using Octokit;
using System;
using FluentAssertions;
using Octokit.Internal;
using Zafiro.FileSystem.Core;

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
        file.Should().Succeed();
    }

    [Fact]
    public async Task Publish_to_github_pages()
    {
        AppContext.SetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", isEnabled: true);
        MSBuildLocator.RegisterDefaults();
        var currentDirectory = (AbsolutePath)Directory.GetCurrentDirectory();
        var solutionDir = currentDirectory / "Sample";

        Directory.Delete(solutionDir, true);
        ZipFile.ExtractToDirectory("SampleSolution.zip", solutionDir);

        var sampleprojectCsproj = solutionDir / "SampleProject.csproj";
        var projectPath = (ZafiroPath)sampleprojectCsproj.ToString();
        var gitHubClient = new GitHubClient(new ProductHeaderValue("Zafiro.Nuke"))
        {
            Credentials = new Credentials("TOKEN")
        };

        var pages = new GitHub(gitHubClient, "SuperJMN-Zafiro.github.io", "SuperJMN-Zafiro");
        var result = await pages.PublishToPages(projectPath);
        result.Should().Succeed();
    }
}