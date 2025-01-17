#load nuget:https://api.nuget.org/v3/index.json?package=Cake.Recipe&version=2.2.0
#tool "dotnet:https://api.nuget.org/v3/index.json?package=sign&version=0.9.1-beta.23530.1&prerelease"
#tool nuget:https://api.nuget.org/v3/index.json?package=NuGet.CommandLine&version=5.11.0

Setup<CodeSigningCredentials>(context => {
    var shouldDeployBakery = (!BuildParameters.IsLocalBuild || BuildParameters.ForceContinuousIntegration) &&
                        BuildParameters.IsTagged &&
                        BuildParameters.PreferredBuildAgentOperatingSystem == BuildParameters.BuildAgentOperatingSystem &&
                        BuildParameters.PreferredBuildProviderType == BuildParameters.BuildProvider.Type;

    var testSigning = string.Equals(EnvironmentVariable("SIGNING_TEST"), "true", StringComparison.OrdinalIgnoreCase);

    var shouldSign = testSigning || shouldDeployBakery;

    var codeSigningCredentials =  shouldSign
                                    ? CodeSigningCredentials.GetCodeSigningCredentials(context)
                                    : new CodeSigningCredentials();

    if(shouldSign && !codeSigningCredentials.HasCredentials)
    {
        throw new InvalidOperationException("Unable to sign Bakery as credentials are not available.");
    }

    return codeSigningCredentials;
});

Environment.SetVariableNames();

var standardNotificationMessage = "Version {0} of {1} has just been released, this will be available here https://www.nuget.org/packages/{1}, once package indexing is complete.";

BuildParameters.SetParameters(context: Context,
                            buildSystem: BuildSystem,
                            sourceDirectoryPath: "./src",
                            title: "Cake.Bakery",
                            repositoryOwner: "cake-build",
                            repositoryName: "bakery",
                            appVeyorAccountName: "cakebuild",
                            shouldRunDotNetCorePack: true,
                            shouldRunDupFinder: false,
                            shouldRunCodecov: false,
                            shouldRunInspectCode: false, // we have a workaround in place
                            nugetConfig: "./src/NuGet.Config",
                            gitterMessage: "@/all " + standardNotificationMessage,
                            twitterMessage: standardNotificationMessage,
                            preferredBuildProviderType: BuildProviderType.GitHubActions);

BuildParameters.PrintParameters(Context);

ToolSettings.SetToolSettings(context: Context,
                            dupFinderExcludePattern: new string[] {
                                BuildParameters.RootDirectoryPath + "/src/*Tests/*.cs" },
                            testCoverageFilter: "+[*]* -[xunit.*]* -[Cake.Core]* -[Cake.Testing]* -[*.Tests]*",
                            testCoverageExcludeByAttribute: "*.ExcludeFromCodeCoverage*",
                            testCoverageExcludeByFile: "*/*Designer.cs;*/*.g.cs;*/*.g.i.cs");

// workaround https://github.com/cake-contrib/Cake.Recipe/issues/862
ToolSettings.SetToolPreprocessorDirectives(
    reSharperTools: "#tool nuget:?package=JetBrains.ReSharper.CommandLineTools&version=2021.2.0");

var binArtifactPath = BuildParameters.Paths.Directories.PublishedApplications.Combine("Cake.Bakery/net6.0");
var zipArtifactsPath = BuildParameters.Paths.Directories.Build.Combine("Packages/Zip");
var omnisharpBaseDownloadURL = "https://github.com/OmniSharp/omnisharp-roslyn/releases/download/v1.37.17";
var omnisharpMonoRuntimeMacOS = $"{omnisharpBaseDownloadURL}/omnisharp-osx.zip";
var omnisharpMonoRuntimeLinux32= $"{omnisharpBaseDownloadURL}/omnisharp-linux-x86.zip";
var omnisharpMonoRuntimeLinux64= $"{omnisharpBaseDownloadURL}/omnisharp-linux-x64.zip";

Task("Copy-License")
    .IsDependentOn("DotNetCore-Build")
    .WithCriteria(() => BuildParameters.ShouldRunDotNetCorePack)
    .Does(() =>
{
    // Copy license
    CopyFileToDirectory("./LICENSE", binArtifactPath);
});

Task("Copy-CIL-Exe")
    .IsDependentOn("DotNetCore-Build")
    .WithCriteria(() => BuildParameters.ShouldRunDotNetCorePack)
    .Does(() =>
{
    // Copy CIL Exe
    CopyFileToDirectory("./asset/Cake.Bakery.exe", binArtifactPath);
});

Task("Zip-Files")
    .IsDependentOn("Copy-License")
    .IsDependentOn("Copy-CIL-Exe")
    .IsDependeeOf("Package")
    .Does<BuildVersion>((context, buildVersion) =>
{
    CleanDirectory(zipArtifactsPath);
    Zip(binArtifactPath, zipArtifactsPath.CombineWithFilePath($"Cake.Bakery.{buildVersion.SemVersion}.zip"));
});

Task("Upload-AppVeyor-Artifacts-Zip")
    .IsDependentOn("Package")
    .IsDependeeOf("Upload-Artifacts")
    .WithCriteria(() => BuildParameters.IsRunningOnAppVeyor)
    .Does(() =>
{
    foreach(var package in GetFiles(zipArtifactsPath + "/*"))
    {
        AppVeyor.UploadArtifact(package);
    }
});

Task("Publish-GitHub-Release-Zip")
    .IsDependentOn("Package")
    .IsDependentOn("Zip-Files")
    .IsDependeeOf("Publish-GitHub-Release")
    .WithCriteria(() => BuildParameters.ShouldPublishGitHub)
    .Does<BuildVersion>((context, buildVersion) => RequireTool(BuildParameters.IsDotNetCoreBuild ? ToolSettings.GitReleaseManagerGlobalTool : ToolSettings.GitReleaseManagerTool, () => {
        if(BuildParameters.CanUseGitReleaseManager)
        {
            foreach(var package in GetFiles(zipArtifactsPath + "/*"))
            {
                GitReleaseManagerAddAssets(BuildParameters.GitHub.Token, BuildParameters.RepositoryOwner, BuildParameters.RepositoryName, buildVersion.Milestone, package.ToString());
            }
        }
        else
        {
            Warning("Unable to use GitReleaseManager, as necessary credentials are not available");
        }
    })
)
.OnError(exception =>
{
    Error(exception.Message);
    Information("Publish-GitHub-Release Task failed, but continuing with next Task...");
    publishingError = true;
});

// Override default Pack task
(BuildParameters.Tasks.DotNetCorePackTask.Task as CakeTask).Actions.Clear();
BuildParameters.Tasks.DotNetCorePackTask
    .IsDependentOn("Copy-License")
    .IsDependentOn("Copy-CIL-Exe")
    .Does<BuildVersion>((context, buildVersion) =>
{
    var msBuildSettings = new DotNetCoreMSBuildSettings()
                                .WithProperty("Version", buildVersion.SemVersion)
                                .WithProperty("AssemblyVersion", "1.0.0.0")
                                .WithProperty("FileVersion",  buildVersion.Version)
                                .WithProperty("AssemblyInformationalVersion", buildVersion.InformationalVersion);

    var projects = GetFiles(BuildParameters.SourceDirectoryPath + "/**/*.csproj");
    foreach(var project in projects)
    {
        var name = project.GetDirectory().FullPath;
        if(name.EndsWith("Cake.Bakery") || name.EndsWith("Tests") || name.EndsWith("Cake.Scripting"))
        {
            continue;
        }

        DotNetCorePack(project.FullPath, new DotNetCorePackSettings {
            NoBuild = true,
            Configuration = BuildParameters.Configuration,
            OutputDirectory = BuildParameters.Paths.Directories.NuGetPackages,
            MSBuildSettings = msBuildSettings
        });
    }

    var binFullArtifactPath = MakeAbsolute(binArtifactPath).FullPath;
    var binFullArtifactPathLength = binFullArtifactPath.Length+1;

    // Cake.Bakery - .NET 4.6
    NuGetPack("./nuspec/Cake.Bakery.nuspec", new NuGetPackSettings {
        Version = buildVersion.SemVersion,
        //ReleaseNotes = TODO,
        BasePath = binArtifactPath,
        OutputDirectory = BuildParameters.Paths.Directories.NuGetPackages,
        Symbols = false,
        NoPackageAnalysis = true,
        Files = GetFiles(binFullArtifactPath + "/**/*")
                                .Select(file=>file.FullPath.Substring(binFullArtifactPathLength))
                                .Select(file=> !file.Equals("LICENSE") ?
                                    new NuSpecContent { Source = file, Target = "tools/" + file } :
                                    new NuSpecContent { Source = file, Target = file })
                                .ToArray()
    });
});

Task("Init-Integration-Tests")
    .IsDependentOn("Sign-Binaries")
    .Does(() =>
{
    CleanDirectories(new [] {
        "./tests/integration/tools",
        "./tests/integration/bin"
    });

    Unzip(
        GetFiles($"{MakeAbsolute(BuildParameters.Paths.Directories.NuGetPackages)}/Cake.Bakery.*.nupkg").First(),
        new DirectoryPath("./tests/integration/tools/Cake.Bakery")
    );

    DotNetCoreBuild("./tests/integration/Cake.Bakery.Tests.Integration.csproj", new DotNetCoreBuildSettings {
        Configuration = BuildParameters.Configuration,
        NoRestore = false,
        Framework = "net6.0",
        OutputDirectory = "./tests/integration/bin"
    });
});

Task("Download-Mono-Assets")
    .WithCriteria(() => !IsRunningOnWindows())
    .Does(() =>
{
    CleanDirectory("./tests/integration/mono");

    string downloadUrl = null;
    switch(Context.Environment.Platform.Family)
    {
        case PlatformFamily.OSX:
            downloadUrl = omnisharpMonoRuntimeMacOS;
            break;
        case PlatformFamily.Linux:
            downloadUrl = Context.Environment.Platform.Is64Bit ?
                omnisharpMonoRuntimeLinux64 :
                omnisharpMonoRuntimeLinux32;
            break;
        default:
            break;
    }

    if (string.IsNullOrEmpty(downloadUrl))
    {
        return;
    }

    var zipFile = DownloadFile(downloadUrl);
    Unzip(zipFile, "./tests/integration/mono");

    StartProcess("chmod", "u+x ./tests/integration/mono/run");
    StartProcess("chmod", "u+x ./tests/integration/mono/bin/mono");
});

Task("Run-Bakery-Integration-Tests")
    .IsDependentOn("Init-Integration-Tests")
    .IsDependentOn("Download-Mono-Assets")
    .IsDependeeOf("CI")
    .IsDependeeOf("Default")
    .Does(() =>
{
    // If not running on Windows, run with OmniSharp Mono and Mono.
    if (!IsRunningOnWindows())
    {
        var exitCode = StartProcess(
            MakeAbsolute(new FilePath("./tests/integration/bin/Cake.Bakery.Tests.Integration")),
            new ProcessSettings {
                Arguments = MakeAbsolute(new FilePath("./tests/integration/tools/Cake.Bakery/tools/Cake.Bakery.exe")).FullPath,
                WorkingDirectory = new DirectoryPath("./tests/integration"),
                EnvironmentVariables = new Dictionary<string, string> {
                    { "CakeBakeryTestsIntegrationMono", Context.Tools.Resolve("mono").FullPath }
                }
        });

        if (exitCode != 0)
        {
            throw new Exception("Mono integration tests failed.");
        }

        exitCode = StartProcess(
            MakeAbsolute(new FilePath("./tests/integration/bin/Cake.Bakery.Tests.Integration")),
            new ProcessSettings {
                Arguments = MakeAbsolute(new FilePath("./tests/integration/tools/Cake.Bakery/tools/Cake.Bakery.exe")).FullPath,
                WorkingDirectory = new DirectoryPath("./tests/integration"),
                EnvironmentVariables = new Dictionary<string, string> {
                    { "CakeBakeryTestsIntegrationMono", MakeAbsolute(new FilePath("./tests/integration/mono/bin/mono")).FullPath }
                }
        });

        if (exitCode != 0)
        {
            throw new Exception("OmniSharp Mono integration tests failed.");
        }
    }
    else
    {
        var exitCode = StartProcess("./tests/integration/bin/Cake.Bakery.Tests.Integration.exe", new ProcessSettings {
            Arguments = MakeAbsolute(new FilePath("./tests/integration/tools/Cake.Bakery/tools/Cake.Bakery.exe")).FullPath,
            WorkingDirectory = new DirectoryPath("./tests/integration")
        });

        if (exitCode != 0)
        {
            throw new Exception(".NET Framework integration tests failed.");
        }
    }
});


Task("Sign-Binaries")
    .IsDependentOn("Package")
    .IsDependeeOf("Upload-AppVeyor-Artifacts-Zip")
    .IsDependeeOf("Upload-Artifacts")
    .IsDependeeOf("Publish-PreRelease-Packages")
    .IsDependeeOf("Publish-Release-Packages")
    .IsDependeeOf("Publish-GitHub-Release-Zip")
    .IsDependeeOf("Publish-GitHub-Release")
    .WithCriteria<CodeSigningCredentials>((context, data) => data.HasCredentials, "Skipping signing as credentials are not available.")
    .Does<CodeSigningCredentials>((context, data) =>
{
    // Get the files to sign.
    var files = GetFiles(string.Concat(BuildParameters.Paths.Directories.NuGetPackages, "/", "*.nupkg")) +
                GetFiles(string.Concat(BuildParameters.Paths.Directories.ChocolateyPackages , "/", "*.nupkg")) +
                GetFiles(string.Concat(zipArtifactsPath, "/", "*.zip"));

    var filter = File("./signclient.filter");

     Parallel.ForEach(
        files,
        file => {
        context.Information("Signing {0}...", file.FullPath);

        // Build the argument list.
        var arguments = new ProcessArgumentBuilder()
            .Append("code")
            .Append("azure-key-vault")
            .AppendQuoted(file.FullPath)
            .AppendSwitchQuoted("--file-list", filter)
            .AppendSwitchQuoted("--publisher-name", "Cake")
            .AppendSwitchQuoted("--description", "Cake (C# Make) is a cross platform build automation system.")
            .AppendSwitchQuoted("--description-url", "https://cakebuild.net")
            .AppendSwitchQuotedSecret("--azure-key-vault-tenant-id", data.SignTenantId)
            .AppendSwitchQuotedSecret("--azure-key-vault-client-id", data.SignClientId)
            .AppendSwitchQuotedSecret("--azure-key-vault-client-secret", data.SignClientSecret)
            .AppendSwitchQuotedSecret("--azure-key-vault-certificate", data.SignKeyVaultCertificate)
            .AppendSwitchQuotedSecret("--azure-key-vault-url", data.SignKeyVaultUrl);

        // Sign the binary.
        var result = StartProcess(data.SignPath, new ProcessSettings {  Arguments = arguments });
        if(result != 0)
        {
            // We should not recover from this.
            throw new InvalidOperationException("Signing failed!");
        }

        context.Information("Done signing {0}.", file.FullPath);
    });
});

// additional workaround for https://github.com/cake-contrib/Cake.Recipe/issues/862
// to suppress the --build/--no-build warning that is generated in the default
BuildParameters.Tasks.InspectCodeTask = Task("InspectCode2021")
    .WithCriteria(() => BuildParameters.BuildAgentOperatingSystem == PlatformFamily.Windows, "Skipping due to not running on Windows")
    .Does<BuildData>(data => RequireTool(ToolSettings.ReSharperTools, () => {
        var inspectCodeLogFilePath = BuildParameters.Paths.Directories.InspectCodeTestResults.CombineWithFilePath("inspectcode.xml");

        var settings = new InspectCodeSettings() {
            SolutionWideAnalysis = true,
            OutputFile = inspectCodeLogFilePath,
            ArgumentCustomization = x => x.Append("--no-build")
        };

        if (FileExists(BuildParameters.SourceDirectoryPath.CombineWithFilePath(BuildParameters.ResharperSettingsFileName)))
        {
            settings.Profile = BuildParameters.SourceDirectoryPath.CombineWithFilePath(BuildParameters.ResharperSettingsFileName);
        }

        InspectCode(BuildParameters.SolutionFilePath, settings);

        // Pass path to InspectCode log file to Cake.Issues.Recipe
        IssuesParameters.InputFiles.InspectCodeLogFilePath = inspectCodeLogFilePath;
    })
);
BuildParameters.Tasks.AnalyzeTask.IsDependentOn("InspectCode2021");
IssuesBuildTasks.ReadIssuesTask.IsDependentOn("InspectCode2021");

Build.RunDotNetCore();


public class CodeSigningCredentials
{
    public string SignTenantId {  get; private set; }
    public string SignClientId {  get; private set; }
    public string SignClientSecret {  get; private set; }
    public string SignKeyVaultCertificate {  get; private set; }
    public string SignKeyVaultUrl {  get; private set; }
    public FilePath SignPath {  get; private set; }

    public bool HasCredentials
    {
        get
        {
            return
                !string.IsNullOrEmpty(SignTenantId) &&
                !string.IsNullOrEmpty(SignClientId) &&
                !string.IsNullOrEmpty(SignClientSecret) &&
                !string.IsNullOrEmpty(SignKeyVaultCertificate) &&
                !string.IsNullOrEmpty(SignKeyVaultUrl);
        }
    }

    public static CodeSigningCredentials GetCodeSigningCredentials(ICakeContext context)
    {
        return new CodeSigningCredentials {
            SignTenantId =  context.EnvironmentVariable("SIGN_TENANT_ID"),
            SignClientId = context.EnvironmentVariable("SIGN_CLIENT_ID"),
            SignClientSecret = context.EnvironmentVariable("SIGN_CLIENT_SECRET"),
            SignKeyVaultCertificate = context.EnvironmentVariable("SIGN_KEYVAULT_CERTIFICATE"),
            SignKeyVaultUrl = context.EnvironmentVariable("SIGN_KEYVAULT_URL"),
            SignPath = context.Tools.Resolve("sign.exe")
                        ?? context.Tools.Resolve("sign")
                        ?? throw new InvalidOperationException("sign.exe could not be located.")
        };
    }
}