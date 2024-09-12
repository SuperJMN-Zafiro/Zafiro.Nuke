using CSharpFunctionalExtensions;
using DotnetPackaging;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Zafiro.Mixins;

namespace Zafiro.Nuke;

public class DeploymentBuilder
{
    public Actions Actions { get; }
    public Project Project { get; }

    private readonly HashSet<IDeployment> deployments = new();

    public DeploymentBuilder(Actions actions, Project project)
    {
        Actions = actions;
        Project = project;
    }

    public DeploymentBuilder ForLinux(Options options)
    {
        deployments.Add(new LinuxDeployment(Actions, options));
        return this;
    }

    public DeploymentBuilder ForWindows()
    {
        deployments.Add(new WindowsDeployment(Actions));
        return this;
    }
    
    public DeploymentBuilder ForAndroid(string base64Keystore, string signingKeyAlias, string signingKeyPass, string signingStorePass)
    {
        deployments.Add(new AndroidDeployment(Actions, base64Keystore, signingKeyAlias, signingKeyPass, signingStorePass));
        return this;
    }

    public Task<Result<IEnumerable<AbsolutePath>>> Build()
    {
        return deployments
            .Select(x => x.Create(Project)).Combine()
            .Map(x => x.Flatten());
    }
}