﻿using Nuke.CoberturaConverter;
using Nuke.Common.Git;
using Nuke.Common.Tools.DotCover;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Nuke.GitHub;
using Nuke.WebDocu;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;
using static Nuke.CoberturaConverter.CoberturaConverterTasks;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.Tools.DocFX.DocFXTasks;
using Nuke.Common.Tools.DocFX;
using static Nuke.Common.Tools.DotCover.DotCoverTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tooling.ProcessTasks;
using static Nuke.GitHub.ChangeLogExtensions;
using static Nuke.GitHub.GitHubTasks;
using static Nuke.WebDocu.WebDocuTasks;
using Nuke.Common.ProjectModel;
using System.Collections.Generic;
using static Nuke.Common.IO.XmlTasks;
using Nuke.Common.Tools.AzureKeyVault.Attributes;
using Nuke.Common.IO;

class Build : NukeBuild
{
    // Console application entry. Also defines the default target.
    public static int Main() => Execute<Build>(x => x.Compile);

    [KeyVaultSettings(
        BaseUrlParameterName = nameof(KeyVaultBaseUrl),
        ClientIdParameterName = nameof(KeyVaultClientId),
        ClientSecretParameterName = nameof(KeyVaultClientSecret))]
    readonly KeyVaultSettings KeyVaultSettings;

    [Parameter] string KeyVaultBaseUrl;
    [Parameter] string KeyVaultClientId;
    [Parameter] string KeyVaultClientSecret;
    [GitVersion(Framework = "netcoreapp3.1")] readonly GitVersion GitVersion;
    [GitRepository] readonly GitRepository GitRepository;


    [KeyVaultSecret] string PublicMyGetSource;
    [KeyVaultSecret] string PublicMyGetApiKey;
    [KeyVaultSecret] string NuGetApiKey;
    [KeyVaultSecret] string DocuBaseUrl;
    [KeyVaultSecret] string GitHubAuthenticationToken;
    [KeyVaultSecret("DanglDataShared-DocuApiKey")] string DocuApiKey;

    [Parameter] readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    [Solution("Dangl.Data.Shared.sln")] readonly Solution Solution;
    AbsolutePath SolutionDirectory => Solution.Directory;
    AbsolutePath OutputDirectory => SolutionDirectory / "output";
    AbsolutePath SourceDirectory => SolutionDirectory / "src";

    string DocFxFile => SolutionDirectory / "docfx.json";
    string ChangeLogFile => RootDirectory / "CHANGELOG.md";

    Target Clean => _ => _
        .Executes(() =>
        {
            GlobDirectories(SourceDirectory, "**/bin", "**/obj").ForEach(DeleteDirectory);
            GlobDirectories(SolutionDirectory / "test", "**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(OutputDirectory);
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore();
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(x => x
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetInformationalVersion(GitVersion.InformationalVersion));
        });

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var changeLog = GetCompleteChangeLog(ChangeLogFile)
                .EscapeStringPropertyForMsBuild();
            DotNetPack(x => x
                .SetConfiguration(Configuration)
                .SetPackageReleaseNotes(changeLog)
                .SetDescription("Dangl.Data.Shared - www.dangl-it.com")
                .SetTitle("Dangl.Data.Shared - www.dangl-it.com")
                .EnableNoBuild()
                .SetOutputDirectory(OutputDirectory)
                .SetVersion(GitVersion.NuGetVersion));
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var testProjects = GlobFiles(SolutionDirectory / "test", "**/*.csproj");
            var testRun = 1;

            try
            {
                DotNetTest(x => x
                    .SetNoBuild(true)
                    .SetTestAdapterPath(".")
                    .CombineWith(cc => testProjects
                        .SelectMany(testProject => GetTestFrameworksForProjectFile(testProject)
                            .Select(targetFramework => cc
                                .SetFramework(targetFramework)
                                .SetProcessWorkingDirectory(Path.GetDirectoryName(testProject))
                                .SetLogger($"xunit;LogFilePath={OutputDirectory / $"{testRun++}_testresults-{targetFramework}.xml"}")))),
                                degreeOfParallelism: Environment.ProcessorCount);
            }
            finally
            {
                PrependFrameworkToTestresults();
            }
        });

    IEnumerable<string> GetTestFrameworksForProjectFile(string projectFile)
    {
        var targetFrameworks = XmlPeek(projectFile, "//Project/PropertyGroup//TargetFrameworks")
            .Concat(XmlPeek(projectFile, "//Project/PropertyGroup//TargetFramework"))
            .Distinct()
            .SelectMany(f => f.Split(';'))
            .Distinct();
        return targetFrameworks;
    }

    Target Coverage => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var testProjects = GlobFiles(SolutionDirectory / "test", "**/*.csproj").ToList();
            var dotnetPath = ToolPathResolver.GetPathExecutable("dotnet");

            var snapshotIndex = 0;
            try
            {
                DotCoverCover(c => c
                    .SetTargetExecutable(dotnetPath)
                    .SetFilters("+:Dangl.Data.Shared;+:Dangl.Data.Shared.AspNetCore")
                    .SetAttributeFilters("System.CodeDom.Compiler.GeneratedCodeAttribute")
                    .CombineWith(cc => testProjects.SelectMany(testProject => {
                        var projectDirectory = Path.GetDirectoryName(testProject);
                        var targetFrameworks = GetTestFrameworksForProjectFile(testProject);
                        return targetFrameworks.Select(targetFramework =>
                        {
                            snapshotIndex++;
                            return cc
                                .SetTargetWorkingDirectory(projectDirectory)
                                .SetOutputFile(OutputDirectory / $"coverage{snapshotIndex:00}.snapshot")
                                .SetTargetArguments($"test --no-build -f {targetFramework} --test-adapter-path:. \"--logger:xunit;LogFilePath={OutputDirectory}/{snapshotIndex}_testresults-{targetFramework}.xml\"");
                        });
                    })), degreeOfParallelism: System.Environment.ProcessorCount,
                    completeOnFailure: true);
            }
            finally
            {
                PrependFrameworkToTestresults();
            }

            var snapshots = GlobFiles(OutputDirectory, "*.snapshot")
                .Aggregate((c, n) => c + ";" + n);

            DotCoverMerge(c => c
                .SetSource(snapshots)
                .SetOutputFile(OutputDirectory / "coverage.snapshot"));

            DotCoverReport(c => c
                .SetSource(OutputDirectory / "coverage.snapshot")
                .SetOutputFile(OutputDirectory / "coverage.xml")
                .SetReportType(DotCoverReportType.DetailedXml));

            // This is the report that's pretty and visualized in Jenkins
            ReportGenerator(c => c
                .SetFramework("netcoreapp3.0")
                .SetReports(OutputDirectory / "coverage.xml")
                .SetTargetDirectory(OutputDirectory / "CoverageReport"));

            // This is the report in Cobertura format that integrates so nice in Jenkins
            // dashboard and allows to extract more metrics and set build health based
            // on coverage readings
            DotCoverToCobertura(s => s
                    .SetInputFile(OutputDirectory / "coverage.xml")
                    .SetOutputFile(OutputDirectory / "cobertura_coverage.xml"))
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        });

    Target Push => _ => _
        .DependsOn(Pack)
        .Requires(() => PublicMyGetSource)
        .Requires(() => PublicMyGetApiKey)
        .Requires(() => NuGetApiKey)
        .Requires(() => Configuration.EqualsOrdinalIgnoreCase("Release"))
        .Executes(() =>
        {
            GlobFiles(OutputDirectory, "*.nupkg").NotEmpty()
                .Where(x => !x.EndsWith("symbols.nupkg"))
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(PublicMyGetSource)
                        .SetApiKey(PublicMyGetApiKey));

                    if (GitVersion.BranchName.Equals("master") || GitVersion.BranchName.Equals("origin/master"))
                    {
                        // Stable releases are published to NuGet
                        DotNetNuGetPush(s => s
                            .SetTargetPath(x)
                            .SetSource("https://api.nuget.org/v3/index.json")
                            .SetApiKey(NuGetApiKey));
                    }
                });
        });

    Target BuildDocFxMetadata => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DocFXMetadata(x => x.AddProjects(DocFxFile));
        });

    Target BuildDocumentation => _ => _
        .DependsOn(Clean)
        .DependsOn(BuildDocFxMetadata)
        .Executes(() =>
        {
            // Using README.md as index.md
            if (File.Exists(SolutionDirectory / "index.md"))
            {
                File.Delete(SolutionDirectory / "index.md");
            }

            File.Copy(SolutionDirectory / "README.md", SolutionDirectory / "index.md");

            DocFXBuild(x => x.SetConfigFile(DocFxFile));

            File.Delete(SolutionDirectory / "index.md");
            Directory.Delete(SolutionDirectory / "shared", true);
            Directory.Delete(SolutionDirectory / "shared-aspnetcore", true);
            Directory.Delete(SolutionDirectory / "obj", true);
        });

    Target UploadDocumentation => _ => _
        .DependsOn(Push) // To have a relation between pushed package version and published docs version
        .DependsOn(BuildDocumentation)
        .Requires(() => DocuApiKey)
        .Requires(() => DocuBaseUrl)
        .Executes(() =>
        {
            var changeLog = GetCompleteChangeLog(ChangeLogFile);

            WebDocu(s => s
                .SetDocuBaseUrl(DocuBaseUrl)
                .SetDocuApiKey(DocuApiKey)
                .SetMarkdownChangelog(changeLog)
                .SetSourceDirectory(OutputDirectory)
                .SetVersion(GitVersion.NuGetVersion)
            );
        });

    Target PublishGitHubRelease => _ => _
        .DependsOn(Pack)
        .Requires(() => GitHubAuthenticationToken)
        .OnlyWhenDynamic(() => GitVersion.BranchName.Equals("master") || GitVersion.BranchName.Equals("origin/master"))
        .Executes(async () =>
        {
            var releaseTag = $"v{GitVersion.MajorMinorPatch}";

            var changeLogSectionEntries = ExtractChangelogSectionNotes(ChangeLogFile);
            var latestChangeLog = changeLogSectionEntries
                .Aggregate((c, n) => c + Environment.NewLine + n);
            var completeChangeLog = $"## {releaseTag}" + Environment.NewLine + latestChangeLog;

            var repositoryInfo = GetGitHubRepositoryInfo(GitRepository);
            var nuGetPackages = GlobFiles(OutputDirectory, "*.nupkg").NotEmpty().ToArray();

            await PublishRelease(x => x
                    .SetArtifactPaths(nuGetPackages)
                    .SetCommitSha(GitVersion.Sha)
                    .SetReleaseNotes(completeChangeLog)
                    .SetRepositoryName(repositoryInfo.repositoryName)
                    .SetRepositoryOwner(repositoryInfo.gitHubOwner)
                    .SetTag(releaseTag)
                    .SetToken(GitHubAuthenticationToken));
        });

    void PrependFrameworkToTestresults()
    {
        var testResults = GlobFiles(OutputDirectory, "*testresults*.xml").ToList();
        foreach (var testResultFile in testResults)
        {
            var frameworkName = GetFrameworkNameFromFilename(testResultFile);
            var xDoc = XDocument.Load(testResultFile);

            foreach (var testType in ((IEnumerable)xDoc.XPathEvaluate("//test/@type")).OfType<XAttribute>())
            {
                testType.Value = frameworkName + "+" + testType.Value;
            }

            foreach (var testName in ((IEnumerable)xDoc.XPathEvaluate("//test/@name")).OfType<XAttribute>())
            {
                testName.Value = frameworkName + "+" + testName.Value;
            }

            xDoc.Save(testResultFile);
        }

        // Merge all the results to a single file
        // The "run-time" attributes of the single assemblies is ensured to be unique for each single assembly by this test,
        // since in Jenkins, the format is internally converted to JUnit. Aterwards, results with the same timestamps are
        // ignored. See here for how the code is translated to JUnit format by the Jenkins plugin:
        // https://github.com/jenkinsci/xunit-plugin/blob/d970c50a0501f59b303cffbfb9230ba977ce2d5a/src/main/resources/org/jenkinsci/plugins/xunit/types/xunitdotnet-2.0-to-junit.xsl#L75-L79
        var firstXdoc = XDocument.Load(testResults[0]);
        var runtime = DateTime.Now;
        var firstAssemblyNodes = firstXdoc.Root.Elements().Where(e => e.Name.LocalName == "assembly");
        foreach (var assemblyNode in firstAssemblyNodes)
        {
            assemblyNode.SetAttributeValue("run-time", $"{runtime:HH:mm:ss}");
            runtime = runtime.AddSeconds(1);
        }
        for (var i = 1; i < testResults.Count; i++)
        {
            var xDoc = XDocument.Load(testResults[i]);
            var assemblyNodes = xDoc.Root.Elements().Where(e => e.Name.LocalName == "assembly");
            foreach (var assemblyNode in assemblyNodes)
            {
                assemblyNode.SetAttributeValue("run-time", $"{runtime:HH:mm:ss}");
                runtime = runtime.AddSeconds(1);
            }
            firstXdoc.Root.Add(assemblyNodes);
        }

        firstXdoc.Save(OutputDirectory / "testresults.xml");
        testResults.ForEach(DeleteFile);
    }

    string GetFrameworkNameFromFilename(string filename)
    {
        var name = Path.GetFileName(filename);
        name = name.Substring(0, name.Length - ".xml".Length);
        var startIndex = name.LastIndexOf('-');
        name = name.Substring(startIndex + 1);
        return name;
    }
}
