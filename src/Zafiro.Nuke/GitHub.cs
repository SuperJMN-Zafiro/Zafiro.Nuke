using System.IO.Abstractions;
using CSharpFunctionalExtensions;
using MoreLinq;
using Nuke.Common.Tools.DotNet;
using Octokit;
using Zafiro.DataModel;
using Zafiro.FileSystem.Core;
using Zafiro.FileSystem.Readonly;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using Directory = Zafiro.FileSystem.Local.Directory;
using File = Zafiro.FileSystem.Readonly.File;
using IDirectory = Zafiro.FileSystem.Readonly.IDirectory;

namespace Zafiro.Nuke;

public class GitHub
{
    public GitHub(GitHubClient client, string repositoryName, string repositoryOwner, string branchName = "master")
    {
        BranchName = branchName;
        RepositoryOwner = repositoryOwner;
        RepositoryName = repositoryName;
        Client = client;
    }

    public string RepositoryOwner { get; }
    public string BranchName { get; }
    public string RepositoryName { get; }
    public GitHubClient Client { get; }

    public Task<Result> PublishToPages(ZafiroPath projectPath)
    {
        var result = PublishWithDotNet(projectPath)
            .Bind(directory => directory.Directories().TryFirst(d => d.Name.Contains("wwwroot")).ToResult("Cannot find wwwroot directory in the output"))
            .Bind(PushToRepo);

        return result;
    }

    private Task<Result> PushToRepo(IDirectory directory)
    {
        IRootedFile noJekyllFile = new RootedFile(ZafiroPath.Empty, new File(".nojekyll", Data.FromString("No Jekyll")));

        var files = directory
            .RootedFiles()
            .Append(noJekyllFile); // This needs to be there for Avalonia applications to work

        return GetTree(files)
            .Bind(PushTree);
    }

    private Task<Result> PushTree(IEnumerable<NewTreeItem> items)
    {
        return Result.Try(() => Commit(items));
    }

    private async Task Commit(IEnumerable<NewTreeItem> items)
    {
        var reference = await Client.Git.Reference.Get(RepositoryOwner, RepositoryName, $"heads/{BranchName}");
        var latestCommit = await Client.Git.Commit.Get(RepositoryOwner, RepositoryName, reference.Object.Sha);

        var newTree = new NewTree();

        items.ForEach(item => newTree.Tree.Add(item));

        var createdTree = await Client.Git.Tree.Create(RepositoryOwner, RepositoryName, newTree);

        var newCommit = new NewCommit("Site update", createdTree.Sha, latestCommit.Sha);
        var commit = await Client.Git.Commit.Create(RepositoryOwner, RepositoryName, newCommit);

        await Client.Git.Reference.Update(RepositoryOwner, RepositoryName, $"heads/{BranchName}", new ReferenceUpdate(commit.Sha));
    }

    private Task<Result<IEnumerable<NewTreeItem>>> GetTree(IEnumerable<IRootedFile> files)
    {
        return files.Select(file => Result.Try(() => NewTreeItem(file))).CombineInOrder();
    }

    private async Task<NewTreeItem> NewTreeItem(IRootedFile file)
    {
        var base64String = Convert.ToBase64String(file.Bytes());

        var blob = await Client.Git.Blob.Create(RepositoryOwner, RepositoryName, new NewBlob
        {
            Content = base64String,
            Encoding = EncodingType.Base64
        });

        return new NewTreeItem
        {
            Path = file.FullPath(),
            Mode = "100644",
            Type = TreeType.Blob,
            Sha = blob.Sha
        };
    }

    private static Task<Result<IDirectory>> PublishWithDotNet(ZafiroPath projectPath)
    {
        var publish = Result.Try(() => Build(projectPath));

        return publish.Bind(async directoryInfo => await ToDirectory(directoryInfo));
    }

    private static async Task<Result<IDirectory>> ToDirectory(IDirectoryInfo directoryInfo)
    {
        var dir = new Directory(directoryInfo);
        var directory = await dir.ToDirectory();
        return directory;
    }

    private static IDirectoryInfo Build(ZafiroPath projectPath)
    {
        var fs = new System.IO.Abstractions.FileSystem();
        var tempFolder = fs.Directory.CreateTempSubdirectory("gh-pages-publish");

        DotNetPublish(settings => settings
            .SetProject(projectPath)
            .SetOutput(tempFolder.FullName));

        return tempFolder;
    }
}