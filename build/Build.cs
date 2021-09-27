using System.IO;
using System.Linq;
using System.Net;
using Nuke.Common;
using Nuke.Common.CI.AppVeyor;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.NuGet;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Logger;
using static Nuke.Common.ControlFlow;
using static Nuke.Common.Tools.NuGet.NuGetTasks;

[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.Publish);

    const string NsisUrlTemplate = "https://cfhcable.dl.sourceforge.net/project/nsis/NSIS%203/{0}/nsis-{0}.zip";
    const string NsisNuSpecFile = "NSIS-Tool.nuspec";
    const string NuGetServerUrl = "https://api.nuget.org/v3/index.json";

    [Parameter] string NsisVersion { get; set; }

    [Parameter] string NuGetPackageVersion { get; set; }

    [Parameter] string NuGetApiKey { get; set; }

    AbsolutePath LibDirectory => RootDirectory / "lib";

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    Target Clean => _ => _
        .Executes(() =>
        {
            EnsureCleanDirectory(LibDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target DownloadNsis => _ => _
        .DependsOn(Clean)
        .Requires(() => NsisVersion)
        .Executes(() =>
        {
            string nsisUrl = string.Format(NsisUrlTemplate, NsisVersion);
            string tempNsisArchive = Path.GetTempFileName();

            Info($"Downloading '{nsisUrl}' to '{tempNsisArchive}");
            try
            {
                using (var webClient = new WebClient())
                {
                    webClient.DownloadFile(nsisUrl, tempNsisArchive);
                }

                Info($"Extracting NSIS to {LibDirectory}");
                UncompressZip(tempNsisArchive, LibDirectory);

                Info($"Renaming NSIS folder");
                RenameDirectory(LibDirectory / "nsis-" + NsisVersion, LibDirectory / "nsis");
            }
            finally
            {
                DeleteFile(tempNsisArchive);
            }
        });

    Target Pack => _ => _
        .DependsOn(DownloadNsis)
        .Requires(() => NuGetPackageVersion)
        .Executes(() =>
        {
            NuGetPack(config => config
                .SetTargetPath(RootDirectory / NsisNuSpecFile)
                .SetVersion(NuGetPackageVersion)
                .SetOutputDirectory(ArtifactsDirectory)
            );
        });

    Target Publish => _ => _
        .DependsOn(Pack)
        .OnlyWhenStatic(() => AppVeyor.Instance != null && AppVeyor.Instance.RepositoryTag)
        .Requires(() => NuGetApiKey)
        .Executes(() =>
        {
            var files = ArtifactsDirectory.GlobFiles($"*.{NuGetPackageVersion}.nupkg");
            Assert(files.Count == 1, $"Package not found in '{ArtifactsDirectory}'");

            NuGetPush(config => config
                .SetSource(NuGetServerUrl)
                .SetApiKey(NuGetApiKey)
                .SetTargetPath(files.Single())
                .SetProcessArgumentConfigurator(arg => arg.Add("-SkipDuplicate"))
            );
        });

}
