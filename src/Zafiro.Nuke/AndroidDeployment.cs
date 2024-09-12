using CSharpFunctionalExtensions;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

namespace Zafiro.Nuke;

public class AndroidDeployment(Actions actions, string base64Keystore, string signingKeyAlias, string signingKeyPass, string signingStorePass) : IDeployment
{
    public Task<Result<IEnumerable<AbsolutePath>>> Create(Project project)
    {
        return Task.FromResult(actions.CreateAndroidPacks(project, base64Keystore, signingKeyAlias, signingKeyPass, signingStorePass));
    }
}