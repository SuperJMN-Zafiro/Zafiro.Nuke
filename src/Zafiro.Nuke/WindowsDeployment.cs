using CSharpFunctionalExtensions;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

namespace Zafiro.Nuke;

public class WindowsDeployment(Actions actions) : IDeployment
{
    public Actions Actions { get; } = actions;

    public Task<Result<IEnumerable<AbsolutePath>>> Create(Project project)
    {
        return Task.FromResult(Actions.CreateWindowsPacks(project));
    }
}