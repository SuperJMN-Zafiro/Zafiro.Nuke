using CSharpFunctionalExtensions;
using DotnetPackaging;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

namespace Zafiro.Nuke;

public class LinuxDeployment : IDeployment
{
    public Actions Actions { get; }
    public Options Options { get; }

    public LinuxDeployment(Actions actions, Options options)
    {
        Actions = actions;
        Options = options;
    }

    public Task<Result<IEnumerable<AbsolutePath>>> Create(Project project)
    {
        return Actions.CreateAppImages(project, Options);
    }
}