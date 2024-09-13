using CSharpFunctionalExtensions;
using DotnetPackaging;
using DotnetPackaging.AppImage.Core;
using GlobExpressions;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.GitHub;
using Octokit;
using Serilog;
using Zafiro.FileSystem.Core;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using AppImage = DotnetPackaging.AppImage.AppImage;
using Architecture = System.Runtime.InteropServices.Architecture;
using Project = Nuke.Common.ProjectModel.Project;
using static Nuke.GitHub.GitHubTasks;

namespace Zafiro.Nuke;

public class Actions
{
    private static readonly Dictionary<Architecture, (string Runtime, string RuntimeLinux)> LinuxArchitecture = new()
    {
        [Architecture.X64] = ("linux-x64", "x86_64"),
        [Architecture.Arm64] = ("linux-arm64", "arm64")
    };

    private static readonly Dictionary<Architecture, (string Runtime, string Suffix)> WindowsArchitecture = new()
    {
        [Architecture.X64] = ("win-x64", "x64"),
    };

    public Actions(Solution solution, GitRepository repository, AbsolutePath rootDirectory, GitVersion gitVersion, string configuration = "Release")
    {
        Solution = solution;
        Repository = repository;
        RootDirectory = rootDirectory;
        GitVersion = gitVersion;
        Configuration = configuration;
    }
    
    private System.IO.Abstractions.FileSystem FileSystem { get; } = new();
    public Solution Solution { get; }
    public GitRepository Repository { get; }
    public AbsolutePath RootDirectory { get; }
    public GitVersion GitVersion { get; }
    public string Configuration { get; }
    public AbsolutePath OutputDirectory => RootDirectory / "output";
    public AbsolutePath PublishDirectory => OutputDirectory / "publish";
    public AbsolutePath PackagesDirectory => OutputDirectory / "packages";

    public Result<IEnumerable<AbsolutePath>> CreateAndroidPacks(Project project, string base64Keystore, string signingKeyAlias, string signingKeyPass, string signingStorePass)
    {
        if (base64Keystore == null)
        {
            throw new ArgumentNullException(nameof(base64Keystore));
        }

        if (signingKeyAlias == null)
        {
            throw new ArgumentNullException(nameof(signingKeyAlias));
        }

        if (signingKeyPass == null)
        {
            throw new ArgumentNullException(nameof(signingKeyPass));
        }

        if (signingStorePass == null)
        {
            throw new ArgumentNullException(nameof(signingStorePass));
        }

        return Result.Try(() =>
        {
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
                .SetProject(project)
                .SetOutput(PublishDirectory));

            keystore.DeleteFile();

            return Glob.Files(PublishDirectory, "*.apk").Select(apkFileName => PublishDirectory / apkFileName);
        });
    }

    public Task<Result<IEnumerable<AbsolutePath>>> CreateWindowsPacks(Project project)
    {
        Architecture[] runtimes = [Architecture.X64];

        var windowsPacks = runtimes.Select(runtime => PublishExe(project, runtime)).Combine();
        return windowsPacks;
    }

    private async Task<Result<AbsolutePath>> PublishExe(Project project, Architecture architecture)
    {
        return Result.Try(() =>
        {
            var finalName = project.Name + $"_{WindowsArchitecture[architecture].Suffix}";
            var finalPath = OutputDirectory / project.Name + $"_{WindowsArchitecture[architecture].Suffix}" + ".exe";
            Log.Information("Creating .exe from {Input} to {Output}", project.Name, finalPath);

            DotNetPublish(settings => settings
                .SetProject(project)
                .SetConfiguration(Configuration)
                .SetSelfContained(true)
                .SetPublishSingleFile(true)
                .SetVersion(GitVersion.MajorMinorPatch)
                .SetRuntime(WindowsArchitecture[architecture].Runtime)
                .SetProperty("IncludeNativeLibrariesForSelfExtract", "true")
                .SetProperty("IncludeAllContentForSelfExtract", "true")
                .SetProperty("DebugType", "embedded")
                .SetOutput(OutputDirectory));

            File.Move(OutputDirectory / project.Name + ".exe", OutputDirectory / finalName);
            
            return finalPath;
        });
    }

    public Result<AbsolutePath> CreateZip(Project project, string runtime)
    {
        return Result.Try(() =>
        {
            var src = PublishDirectory / runtime;
            var zipName = $"{Solution.Name}_{GitVersion.MajorMinorPatch}_{runtime}.zip";
            var dest = PackagesDirectory / zipName;
            Log.Information("Zipping {Input} to {Output}", src, dest);
            
            DotNetPublish(settings => settings
                .SetProject(project)
                .SetConfiguration(Configuration)
                .SetRuntime(runtime)
                .SetOutput(PublishDirectory / runtime));
            
            src.ZipTo(dest);
            return dest;
        });
    }

    /// <summary>
    /// Create AppImages for Linux
    /// </summary>
    /// <param name="project">Project to pack</param>
    /// <param name="options">Options for the packaged application</param>
    /// <returns></returns>
    public Task<Result<IEnumerable<AbsolutePath>>> CreateAppImages(Project project, Options options)
    {
        IEnumerable<Architecture> supportedArchitectures = [Architecture.Arm64, Architecture.X64];

        return supportedArchitectures
            .Select(architecture => CreateAppImage(options, architecture, project).Tap(() => Log.Information("Publishing AppImage for {Architecture}", architecture)))
            .CombineInOrder();
    }

    private Task<Result<AbsolutePath>> CreateAppImage(Options options, Architecture architecture, Project desktopProject)
    {
        var publishDirectory = desktopProject.Directory / "bin" / "publish" / LinuxArchitecture[architecture].Runtime;

        DotNetPublish(settings => settings
            .SetConfiguration(Configuration)
            .SetProject(desktopProject)
            .SetRuntime(LinuxArchitecture[architecture].Runtime)
            .SetSelfContained(true)
            .SetOutput(publishDirectory));

        var packagePath = OutputDirectory / Solution.Name + "-" + GitVersion.MajorMinorPatch + "-" + LinuxArchitecture[architecture].RuntimeLinux + ".AppImage";

        var dirResult = new Zafiro.FileSystem.Local.Directory(FileSystem.DirectoryInfo.New(publishDirectory));

        return dirResult
            .ToDirectory()
            .Bind(directory => AppImage
                .From()
                .Directory(directory)
                .Configure(configuration => configuration.From(options))
                .Build()
                .Bind(appImage => appImage.ToData())
                .Bind(x => x.DumpTo(packagePath))
                .Map(() => packagePath));
    }

    public Result PushNuGetPackages(string nuGetApiKey)
    {
        var packableProjects = Solution.AllProjects.Where(x => x.GetProperty<bool>("IsPackable")).ToList();

        return packableProjects
            .Select(project => Pack(project, nuGetApiKey))
            .Combine();
    }

    public Task<Result> CreateGitHubRelease(string token, params AbsolutePath[] artifacts)
    {
        return Result.Try(() =>
        {
            Assert.NotEmpty(artifacts, "Could not find any assets to upload to the release");
            var releaseTag = $"v{GitVersion.MajorMinorPatch}";
            var repositoryInfo = GetGitHubRepositoryInfo(Repository);
            Log.Information("Commit for the release: {GitVersionSha}", GitVersion.Sha);

            return PublishRelease(x => x
                .SetArtifactPaths(artifacts.Select(path => (string)path).ToArray())
                .SetCommitSha(GitVersion.Sha)
                .SetRepositoryName(repositoryInfo.repositoryName)
                .SetRepositoryOwner(repositoryInfo.gitHubOwner)
                .SetTag(releaseTag)
                .SetToken(token)
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