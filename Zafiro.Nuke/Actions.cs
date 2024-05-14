using CSharpFunctionalExtensions;
using DotnetPackaging;
using DotnetPackaging.AppImage.Core;
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
    public AbsolutePath RootDirectory { get; }
    public GitVersion GitVersion { get; }
    public string Configuration { get; }
    public AbsolutePath OutputDirectory => RootDirectory / "output";
    public AbsolutePath PublishDirectory => OutputDirectory / "publish";
    public AbsolutePath PackagesDirectory => OutputDirectory / "packages";

    public Actions(Solution solution, AbsolutePath rootDirectory, GitVersion gitVersion, string configuration = "Release")
    {
        Solution = solution;
        RootDirectory = rootDirectory;
        GitVersion = gitVersion;
        Configuration = configuration;
    }

    public void CreateAndroidPack(string base64Keystore, string signingKeyAlias, string signingKeyPass, string signingStorePass)
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
    }

    public void CreateWindowsPack()
    {
        var desktopProject = Solution.AllProjects.First(project => project.Name.EndsWith("Desktop"));
        var runtimes = new[] { "win-x64", };

        DotNetPublish(settings => settings
            .SetConfiguration(Configuration)
            .SetProject(desktopProject)
            .CombineWith(runtimes, (c, runtime) =>
                c.SetRuntime(runtime)
                    .SetOutput(PublishDirectory / runtime)));

        runtimes.ForEach(rt =>
        {
            var src = PublishDirectory / rt;
            var zipName = $"{Solution.Name}_{GitVersion.MajorMinorPatch}_{rt}.zip";
            var dest = PackagesDirectory / zipName;
            Log.Information("Zipping {Input} to {Output}", src, dest);
            src.ZipTo(dest);
        });
    }

    public Task<Result> CreateLinuxAppImages(Options options)
    {
        IEnumerable<Architecture> supportedArchitectures = [Architecture.Arm64, Architecture.X64];
        var desktopProject = Solution.AllProjects.First(project => project.Name.EndsWith("Desktop"));

        return supportedArchitectures
            .Select(architecture => CreateAppImage(options, architecture, desktopProject)
                .Tap(() => Log.Information("Publishing AppImage for {Architecture}", architecture)))
            .CombineInOrder();
    }

    private Task<Result> CreateAppImage(Options options, Architecture architecture, Project desktopProject)
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
            .Bind(x => x.DumpTo(packagePath));
    }

    public void CreateNuGetPackages()
    {
        var packableProjects = Solution.AllProjects.Where(x => x.GetProperty<bool>("IsPackable")).ToList();

        DotNetPack(settings => settings
            .SetConfiguration(Configuration)
            .SetVersion(GitVersion.NuGetVersion)
            .SetOutputDirectory(OutputDirectory)
            .CombineWith(packableProjects, (packSettings, project) =>
                packSettings.SetProject(project)));
    }

    public void PushNuGetPackages(string nuGetApiKey)
    {
        var nugetPackages = OutputDirectory.GlobFiles("*.nupkg");
        Assert.NotEmpty(nugetPackages);
        
        DotNetNuGetPush(settings => settings
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(nuGetApiKey)
                .CombineWith(nugetPackages, (s, v) => s.SetTargetPath(v)),
            degreeOfParallelism: 5, completeOnFailure: true);
    }
}