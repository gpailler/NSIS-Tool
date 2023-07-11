using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.NuGet;
using Octokit;
using Serilog;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using FileMode = System.IO.FileMode;

[GitHubActions(
    "Publish",
    GitHubActionsImage.WindowsLatest,
    OnPushTags = new[] {"v*"},
    InvokedTargets = new[] { nameof(Publish)},
    ImportSecrets = new[] { nameof(NuGetApiKey) },
    EnableGitHubToken = true)]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.Pack);

    static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    const string NsisUrlTemplate = "https://cfhcable.dl.sourceforge.net/project/nsis/NSIS%203/{0}/nsis-{0}.zip";
    const string NsisNuSpecFile = "NSIS-Tool.nuspec";
    const string NuGetServerUrl = "https://api.nuget.org/v3/index.json";

    static Build()
    {
        HttpTasks.DefaultTimeout = DownloadTimeout;
    }

    [Parameter] string NsisVersion { get; set; } // NSIS version in the form x.yy (e.g. 3.08)

    [Parameter] string NuGetPackageVersion { get; set; } // NuGet version in the form x.y.z (e.g. 3.0.8)

    [Parameter] [Secret] string NuGetApiKey { get; set; }

    AbsolutePath LibDirectory => RootDirectory / "lib";

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    [GitRepository] readonly GitRepository Repository;

    Target Clean => _ => _
        .Executes(() =>
        {
            LibDirectory.CreateOrCleanDirectory();
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Pack => _ => _
        .DependsOn(Clean)
        .DependsOn(DownloadNsis)
        .Requires(() => NuGetPackageVersion)
        .Produces(ArtifactsDirectory / "*.nupkg")
        .Executes(() =>
        {
            NuGetPack(config => config
                .SetTargetPath(RootDirectory / NsisNuSpecFile)
                .SetVersion(NuGetPackageVersion)
                .SetOutputDirectory(ArtifactsDirectory)
            );
        });

    Target Publish => _ => _
        .DependsOn(PublishNugetPackage)
        .DependsOn(PublishGitHubRelease);

    Target PublishNugetPackage => _ => _
        .DependsOn(Pack)
        .Requires(() => NuGetApiKey)
        .Unlisted()
        .Executes(() =>
        {
            var files = ArtifactsDirectory.GlobFiles($"*.{NuGetPackageVersion}.nupkg");
            Assert.Count(files, 1, $"Package not found in '{ArtifactsDirectory}'");
            var package = files.Single();

            NuGetPush(config => config
                .SetSource(NuGetServerUrl)
                .SetApiKey(NuGetApiKey)
                .SetTargetPath(package)
                .SetProcessArgumentConfigurator(arg => arg.Add("-SkipDuplicate"))
            );
        });

    Target PublishGitHubRelease => _ => _
        .DependsOn(Pack)
        .After(PublishNugetPackage)
        .OnlyWhenStatic(() => GitHubActions.Instance != null && GitHubActions.Instance.Token != null)
        .Unlisted()
        .Executes(async () =>
        {
            var files = ArtifactsDirectory.GlobFiles($"*.{NuGetPackageVersion}.nupkg");
            Assert.Count(files, 1, $"Package not found in '{ArtifactsDirectory}'");
            var package = files.Single();

            GitHubTasks.GitHubClient.Credentials = new Credentials(GitHubActions.Instance.Token);
            var release = await GitHubTasks.GitHubClient.Repository.Release.Create(
                Repository.GetGitHubOwner(),
                Repository.GetGitHubName(),
                new NewRelease($"v{NuGetPackageVersion}")
                {
                    Name = $"v{NuGetPackageVersion}",
                    Draft = true,
                    Body = $"Update to NSIS {NsisVersion}"
                });

            await using var packageStream = File.OpenRead(package);
            await GitHubTasks.GitHubClient.Repository.Release.UploadAsset(release, new ReleaseAssetUpload
            {
                FileName = package.Name,
                ContentType = "application/octet-stream",
                RawData = packageStream
            });
        });

    Target DownloadNsis => _ => _
        .DependsOn(Clean)
        .Requires(() => NsisVersion)
        .Unlisted()
        .Executes(() =>
        {
            string nsisUrl = string.Format(NsisUrlTemplate, NsisVersion);
            AbsolutePath tempNsisArchive = Path.GetTempFileName();

            Log.Information($"Downloading '{nsisUrl}' to '{tempNsisArchive}");
            HttpTasks.HttpDownloadFile(nsisUrl, tempNsisArchive, FileMode.Create);

            Log.Information($"Extracting NSIS to {LibDirectory}");
            tempNsisArchive.UnZipTo(LibDirectory);

            Log.Information($"Renaming NSIS folder");
            RenameDirectory(LibDirectory / "nsis-" + NsisVersion, LibDirectory / "nsis");

            tempNsisArchive.DeleteFile();
        });
}
