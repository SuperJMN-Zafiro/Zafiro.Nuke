using CSharpFunctionalExtensions;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

namespace Zafiro.Nuke;

public interface IDeployment
{
    Task<Result<IEnumerable<AbsolutePath>>> Create(Project project);
}