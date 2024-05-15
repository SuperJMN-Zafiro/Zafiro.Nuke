using CSharpFunctionalExtensions;
using DotnetPackaging;
using DotnetPackaging.AppImage.Core;
using GlobExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Serilog;
using Zafiro.FileSystem;
using Zafiro.FileSystem.Lightweight;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using AppImage = DotnetPackaging.AppImage.AppImage;
using Architecture = System.Runtime.InteropServices.Architecture;
using Microsoft.Build.Evaluation;
using Project = Nuke.Common.ProjectModel.Project;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.GitHub.GitHubTasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using DotnetPackaging;
using DotnetPackaging.AppImage;
using DotnetPackaging.AppImage.Core;
using NuGet.Common;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Git;
using Nuke.Common.Tools.DotNet;using Nuke.Common.Tools.NSwag;
using Nuke.Common.Utilities.Collections;
using Nuke.GitHub;
using Serilog;
using Zafiro.FileSystem.Lightweight;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.GitHub.GitHubTasks;
using static Nuke.Common.Tooling.ProcessTasks;
using Maybe = CSharpFunctionalExtensions.Maybe;
using static Nuke.Common.Tools.NSwag.NSwagTasks;

namespace Zafiro.Nuke;

public class Actions
{
    static readonly Dictionary<Architecture, (string Runtime, string RuntimeLinux)> ArchitectureData = new()
    {
        [Architecture.X64] = ("linux-x64", "x86_64"),
        [Architecture.Arm64] = ("linux-arm64", "arm64"),
    };

    private System.IO.Abstractions.FileSystem FileSystem { get; } = new();
    public Solution Solution { get; }
    public GitRepository Repository { get; }
    public AbsolutePath RootDirectory { get; }
    public GitVersion GitVersion { get; }
    public string Configuration { get; }
    public AbsolutePath OutputDirectory => RootDirectory / "output";
    public AbsolutePath PublishDirectory => OutputDirectory / "publish";
    public AbsolutePath PackagesDirectory => OutputDirectory / "packages";

    public Actions(Solution solution, GitRepository repository, AbsolutePath rootDirectory, GitVersion gitVersion, string configuration = "Release")
    {
        Solution = solution;
        Repository = repository;
        RootDirectory = rootDirectory;
        GitVersion = gitVersion;
        Configuration = configuration;
    }

    public Result<IEnumerable<string>> CreateAndroidPacks(string base64Keystore, string signingKeyAlias, string signingKeyPass, string signingStorePass)
    {
        return Result.Try(() =>
        {
            var androidProject = Solution.AllProjects.First(project => project.Name.EndsWith("Android"));
            var keystore = OutputDirectory / "temp.keystore";
            keystore.WriteAllBytes(Convert.FromBase64String(base64Keystore));

            DotNetPublish(settings => settings
                .SetProperty("ApplicationVersion", GitVersion.CommitsSinceVersionSource)
                .SetProperty("ApplicationDisplayVersion", GitVersion.MajorMinorPatch)
                .SetProperty("AndroidKeyStore", "true")
                .SetProperty("AndroidSigningKeyStore", keystore)
                .SetProperty("AndroidSigningKeyAlias", signingKeyAlias)
                .SetProperty("AndroidSigningStorePass", signingStorePass)
                .SetProperty("AndroidSigningKeyPass", signingKeyPass)
                .SetConfiguration("Release")
                .SetProject(androidProject)
                .SetOutput(PublishDirectory));

            keystore.DeleteFile();

            return Glob.Files(PublishDirectory, "*.apk");
        });
    }

    public Result<IEnumerable<AbsolutePath>> CreateWindowsPacks()
    {
        return Result.Try(() =>
        {
            var desktopProject = Solution.AllProjects.First(project => project.Name.EndsWith("Desktop"));
            var runtimes = new[] { "win-x64", };

            DotNetPublish(settings => settings
                .SetConfiguration(Configuration)
                .SetProject(desktopProject)
                .CombineWith(runtimes, (c, runtime) =>
                    c.SetRuntime(runtime)
                        .SetOutput(PublishDirectory / runtime)));

            return runtimes.Select(rt =>
            {
                var src = PublishDirectory / rt;
                var zipName = $"{Solution.Name}_{GitVersion.MajorMinorPatch}_{rt}.zip";
                var dest = PackagesDirectory / zipName;
                Log.Information("Zipping {Input} to {Output}", src, dest);
                src.ZipTo(dest);
                return dest;
            });
        });
    }

    public Task<Result<IEnumerable<AbsolutePath>>> CreateLinuxAppImages(Options options)
    {
        IEnumerable<Architecture> supportedArchitectures = [Architecture.Arm64, Architecture.X64];
        var desktopProject = Solution.AllProjects.First(project => project.Name.EndsWith("Desktop"));

        return supportedArchitectures
            .Select(architecture => CreateAppImage(options, architecture, desktopProject)
                .Tap(() => Log.Information("Publishing AppImage for {Architecture}", architecture)))
            .CombineInOrder();
    }

    private Task<Result<AbsolutePath>> CreateAppImage(Options options, Architecture architecture, Project desktopProject)
    {
        var publishDirectory = desktopProject.Directory / "bin" / "publish" / ArchitectureData[architecture].Runtime;

        DotNetPublish(settings => settings
            .SetConfiguration(Configuration)
            .SetProject(desktopProject)
            .SetRuntime(ArchitectureData[architecture].Runtime)
            .SetSelfContained(true)
            .SetOutput(publishDirectory));

        var packagePath = OutputDirectory / Solution.Name + "-" + GitVersion.MajorMinorPatch + "-" + ArchitectureData[architecture].RuntimeLinux + ".AppImage";

        return AppImage
            .From()
            .Directory(new DotnetDir(FileSystem.DirectoryInfo.New(publishDirectory)))
            .Configure(configuration => configuration.From(options))
            .Build()
            .Bind(appImage => appImage.ToData())
            .Bind(x => x.DumpTo(packagePath))
            .Map(() => packagePath);
    }

    public Result PushNuGetPackages(string nuGetApiKey)
    {
        var packableProjects = Solution.AllProjects.Where(x => x.GetProperty<bool>("IsPackable")).ToList();

        return packableProjects
            .Select(project => Pack(project, nuGetApiKey))
            .Combine();
    }

    public Task<Result> CreateGitHubRelease(string authenticationToken, params AbsolutePath[] artifacts)
    {
        return Result.Try(() =>
        {
            Assert.NotEmpty(artifacts, "Could not find any assets to upload to the release");
            var releaseTag = $"v{GitVersion.MajorMinorPatch}";
            var repositoryInfo = GetGitHubRepositoryInfo(Repository);
            Log.Information("Commit for the release: {GitVersionSha}", GitVersion.Sha);
            
            return PublishRelease(x => x
                .SetArtifactPaths(artifacts.Select(path => (string) path).ToArray())
                .SetCommitSha(GitVersion.Sha)
                .SetRepositoryName(repositoryInfo.repositoryName)
                .SetRepositoryOwner(repositoryInfo.gitHubOwner)
                .SetTag(releaseTag)
                .SetToken(authenticationToken)
            );
        });
    }

    private Result Pack(Project project, string nuGetApiKey)
    {
        return Result.Try(() =>
        {
            DotNetPack(settings => settings
                .SetConfiguration(Configuration)
                .SetVersion(GitVersion.NuGetVersion)
                .SetOutputDirectory(OutputDirectory)
                .SetProject(project));

            var packageId = project.GetProperty("PackageId") ?? project.GetProperty("AssemblyName") ?? project.Name;
            var package = OutputDirectory / packageId + "." + GitVersion.NuGetVersion + ".nupkg";

            DotNetNuGetPush(settings => settings
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(nuGetApiKey)
                .SetTargetPath(package));
        });
    }
}