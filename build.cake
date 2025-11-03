// add-ins
#addin nuget:?package=Cake.Git&version=3.0.0
// tools - nuget
#addin nuget:?package=Cake.Incubator&version=8.0.0

// tools - dotnet
#tool dotnet:?package=GitVersion.Tool&version=6.4.0
#tool dotnet:?package=dotnet-reportgenerator-globaltool&version=5.1.26

///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////
var target = Argument("target", "Default");
var targets = Argument("targets", "");
var configuration = Argument("configuration", "Release");
var buildCounter = Argument("buildcounter", "0");
var nugetPublishServerUrl = "carweb";
var nugetPublishServerApiKey = "arbitrary";
var pushPreRelease = Argument("pushPreRelease", false);

///////////////////////////////////////////////////////////////////////////////
// SETTINGS
///////////////////////////////////////////////////////////////////////////////

var distDirectory = Directory("./.dist");
var packageDirectory = Directory("./.pack");
var testOutputDir = Context.MakeAbsolute(Directory("./.test-results"));
var testCoverageOutPutDir = testOutputDir + "/.coverage";
var testCoverageReportOutPutDir = $"{testOutputDir}/.coverage-report";
var mergedCoverageResults = "";
var isMainBranch = false;
var currentBranch = "";
var currentVersion = "1.0.0.0";
var currentVersionNuGet = "1.0.0.0";
var preReleaseTag = "";

using System.Text.RegularExpressions;

///////////////////////////////////////////////////////////////////////////////
// TASKS
///////////////////////////////////////////////////////////////////////////////

Task("Default")
	.IsDependentOn("Build")
	.IsDependentOn("Test");

Task("Build")
	.IsDependentOn("BranchInfo")
	.IsDependentOn("Version")
	.IsDependentOn("Clean")
	.IsDependentOn("Build-Solution");

Task("Test")
	.IsDependentOn("Test-All")
	.IsDependentOn("Generate-CoverageReport")
	.IsDependentOn("Update-Status");

var deploy = Task("Deploy")
	.IsDependentOn("Build")
	.IsDependentOn("Test")
	.IsDependentOn("Pack")
	.IsDependentOn("Push-Nuget")
	;

#region Build
///////////////////////////////////////////////////////////////////////////////
// BUILD TASKS
///////////////////////////////////////////////////////////////////////////////

Task("BranchInfo")
	.Does(()=>
	{
		WriteProgressMessage("Getting branch info...");
		var gitBranch = GitBranchCurrent("./");

		Information($"CanonicalName: {gitBranch.CanonicalName}");
		Information($"FriendlyName: {gitBranch.FriendlyName}");
		Information($"IsRemote: {gitBranch.IsRemote}");

		currentBranch = gitBranch.FriendlyName;
		isMainBranch = StringComparer.OrdinalIgnoreCase.Equals("main",currentBranch);
		Information($"isMainBranch: {isMainBranch}");
	});

Task("Version")
	.Does(()=>
	{
		WriteProgressMessage("Calculating semantic version...");
		GitVersion(new GitVersionSettings(){
			OutputType = GitVersionOutput.BuildServer
		});

		var gitVersion = GitVersion(new GitVersionSettings(){
			OutputType = GitVersionOutput.Json,
			Verbosity = GitVersionVerbosity.Verbose
		});
		currentVersion = gitVersion.SemVer;
		currentVersionNuGet = currentVersion;
		preReleaseTag = gitVersion.PreReleaseTag;

		Information($"Current version: {currentVersion}");
		Information($"Current version NuGet: {currentVersionNuGet}");
		Information($"##teamcity[buildNumber '{currentVersion}']");
	});

Task("Clean")
	.Does(()=>
	{
		WriteProgressMessage("Cleaning directories...");
		CleanDirectory(distDirectory);
		CleanDirectory(packageDirectory);
		CleanDirectory(testOutputDir);
	});

Task("Build-Solution")
	.Does(()=>
	{
		var solutions = GetFiles("*.sln");
		foreach ( var solution in solutions) {
			WriteProgressMessage($"Building {solution.GetFilenameWithoutExtension()} v{currentVersion}");
			DotNetBuild(solution.FullPath, new DotNetBuildSettings()
			{
				Configuration = configuration,
				NoIncremental = true,
				MSBuildSettings = new DotNetMSBuildSettings()
					.WithProperty("Version", currentVersion)
			});
		}
	});

#endregion // build

#region Test
///////////////////////////////////////////////////////////////////////////////
// TEST TASKS
///////////////////////////////////////////////////////////////////////////////

Task("Test-Clean")
	.Does(()=>{
		CleanDirectory(testOutputDir);
		Information($"Cleaned: {testOutputDir}");
	});

Task("Test-Unit")
	.IsDependentOn("Test-Clean")
	.Does(()=>{
		DetectTestFramework("Unit");
		RunTestsByType("Unit", testFrameworksInUse.First().Key);
	});

Task("Test-Service")
	.IsDependentOn("Test-Clean")
	.Does(()=>{
		DetectTestFramework("Service");
		RunTestsByType("Service", testFrameworksInUse.First().Key);
	});


Task("Test-All")
	.IsDependentOn("Test-Clean")
	.Does(()=>{
		DetectTestFramework("Unit");
		// DetectTestFramework("Service");

		if(testFrameworksInUse.Keys.Count > 1){
			Information("Multiple test frameworks detected. Parallel test runs not possible.");
			foreach (var testFx in testFrameworksInUse)
			{
				foreach (var testType in testFx.Value.TestsByType)
				{
					RunTestsByType(testType.Key, testFx.Key);
				}
			}
		}
		else{
			Information($"Single test framework detected ({testFrameworksInUse.First().Key}). Running tests in parallel.");
			RunTests(".");
		}
	});

private Dictionary<string, TestInfo> testFrameworksInUse =  new Dictionary<string, TestInfo>();

private class TestInfo
{
	public Dictionary<string, ICollection<FilePath>> TestsByType =  new Dictionary<string, ICollection<FilePath>>();

	public TestInfo AddTests(string testType, FilePath projectFile){
		if(!TestsByType.ContainsKey(testType))
		{
			TestsByType.Add(testType, new List<FilePath>{projectFile});
		}
		else {
			TestsByType[testType].Add(projectFile);
		}
		return this;
	}

	public override string ToString()
	{
		var info = "";
		foreach (var testType in TestsByType)
		{
			info += $"{testType.Key}: {Environment.NewLine}";
			foreach (var projFile in testType.Value)
			{
				info += $"- {projFile.FullPath}{Environment.NewLine}";
			}
		}
		return info;
	}
}

private void DetectTestFramework(string testType)
{
	var folderPattern = GetTestFolderPattern(testType);
	var projects = GetFiles($"./test/*{folderPattern}*/**/*.csproj");
	foreach(var projFile in projects)
	{
		var proj = ParseProject(projFile, configuration).NetCore;
		var packageRefs = proj.PackageReferences;
		if (packageRefs.Any(r => r.Name.ToLower().Contains("nunit")))
			AddToTestsByType(testFrameworksInUse, "NUnit", testType, projFile);
	}
}

private void AddToTestsByType(Dictionary<string, TestInfo> testsByType, string testFramework, string testType, FilePath projectFile)
{
	if(!testsByType.ContainsKey(testFramework))
	{
		var ti = new TestInfo().AddTests(testType, projectFile);
		testsByType.Add(testFramework, ti);
	}
	else {
		testsByType[testFramework].AddTests(testType, projectFile);
	}
}

private void RunTestsByType(string testType = "unit", string testFramework = "nunit"){
	WriteProgressMessage($"Running {testType} tests...");
	var folderPattern = GetTestFolderPattern(testType);
	var projects = GetFiles($"./test/*{folderPattern}*/**/*.csproj");
	foreach(var project in projects)
	{
		Information("Testing project " + project);
		RunTests(project.ToString(), testFramework);
	}
}

private void RunTests(string path, string testFramework = "nunit"){
	var testSettings = new DotNetTestSettings {
		Configuration = configuration,
		NoBuild = true,
		ArgumentCustomization = args => args
			.Append("--no-restore")
			.Append($"--results-directory {testCoverageOutPutDir}")
            .Append("--collect \"XPlat Code Coverage\"")
	};
	if (testFramework.ToLower() == "xunit") {
		Information($"Adding console logger for {testFramework}...");
		testSettings.Loggers.Add ("console;verbosity=normal");
	}
	DotNetTest(path, testSettings);
}

private string GetTestFolderPattern(string testType){
	var folderPattern = testType;
	if(testType.ToLower() == "unit"){
		folderPattern = "[uU]nit";
	}
	if(testType.ToLower() == "service"){
		folderPattern = "[sS]service";
	}
	return folderPattern;
}


Task("Generate-CoverageReport")
	.Does(()=>{
		WriteProgressMessage("Generating coverage reports...");

		ReportGenerator(
			report: $"{testCoverageOutPutDir}/**/*.cobertura.xml",
			targetDir: testCoverageReportOutPutDir,
			settings: new ReportGeneratorSettings{
				ReportTypes = new [] { ReportGeneratorReportType.HtmlInline },
				ToolPath = Context.Tools.Resolve("reportgenerator") ?? Context.Tools.Resolve("reportgenerator.exe"),
				Verbosity = ReportGeneratorVerbosity.Info
			});
	});

Task("Update-Status")
    .Does(() => {

    var coverageStatus = "";
	var covReportPath = $"{testCoverageReportOutPutDir}/index.htm";
	Information($"Coverage report: {covReportPath}");

	if(FileExists(covReportPath)){
		var covReport = System.IO.File.ReadAllText(covReportPath);
		Information($"Coverage report size: {covReport.Length}");

		var branchCovRegExPattern = @"Branch coverage.*?(?<branchcov>\d+(\.\d)?)%\s";
		decimal branchCoveragePct = GetCoveragePct(covReport, branchCovRegExPattern, "branchcov");
		Information($"Branch coverage: {branchCoveragePct}");

		var lineCovRegExPattern = @"Line coverage.*?(?<linecov>\d+(\.\d)?)%\s";
		decimal lineCoveragePct = GetCoveragePct(covReport, lineCovRegExPattern, "linecov");
		Information($"Line coverage: {lineCoveragePct}");

		if (branchCoveragePct > 0 && lineCoveragePct > 0){
        	coverageStatus = $". Coverage: {branchCoveragePct.ToString("#")}/{lineCoveragePct.ToString("#")}% (b/l)";
		}
		else {
			coverageStatus = ". No test coverage!";
		}
        	Information("Coverage status: " + coverageStatus);
	}

    Information($"##teamcity[buildStatus text='{{build.status.text}}{coverageStatus}']");
});

private decimal GetCoveragePct(string covReport, string pattern, string matchedGroup){
	decimal coveragePct = 0m;
	foreach (Match match in Regex.Matches(covReport, pattern)){
		var covStr = match.Groups[matchedGroup].Value;
		coveragePct = Convert.ToDecimal(covStr, new System.Globalization.CultureInfo("en-US"));
	}
	return coveragePct;
}

#endregion // TEST

#region Deploy
///////////////////////////////////////////////////////////////////////////////
// DEPLOY TASKS
///////////////////////////////////////////////////////////////////////////////

Task("Pack")
	.IsDependentOn("Version")
	.Does(()=>
	{
		var projects = GetFiles($"./src/**/*.csproj");
		foreach(var project in projects)
		{
			// Release package
			WriteProgressMessage($"Packaging release NuGet package...");
			DotNetPack(
				project.ToString(),
				new DotNetPackSettings
				{
                    NoBuild = false,
					Configuration = configuration,
					OutputDirectory = packageDirectory,
					DiagnosticOutput = true,
					ArgumentCustomization = args => args
						.Append($"/p:Version={currentVersionNuGet}")
				}
			);

			WriteProgressMessage($"Packaging symbol NuGet package...");
			DotNetPack(
				project.ToString(),
				new DotNetPackSettings
				{
					NoBuild = false,
					Configuration = "Debug",
					OutputDirectory = packageDirectory,
					IncludeSymbols = true,
					IncludeSource = true,
					ArgumentCustomization = args => args
						.Append($"/p:Version={currentVersionNuGet}")
				}
			);
		}
	});

Task("Push-Nuget")
	.Does(()=>
	{
		var packages = GetFiles($"{packageDirectory}/*.nupkg");
		foreach (var package in packages) {

			if (!isMainBranch && !pushPreRelease) {
				Information($"Not on main branch and 'pushPreRelease' is set to {pushPreRelease} so the following packages will not be pushed:");
				Information($"Package: {package}");
				continue;
			}

			// Push normal package
			WriteProgressMessage($"Pushing {package} to nuget server");
			DotNetNuGetPush(package.ToString(),new DotNetNuGetPushSettings{
				Source = nugetPublishServerUrl,
				ApiKey = nugetPublishServerApiKey
			});
		}
	});

Task("Deploy-PreRelease")
	.Does(() => {
		pushPreRelease = true;
		RunTarget(deploy.Task.Name);
	});

#endregion // Deploy

private void WriteProgressMessage(string message)
{
  if(TeamCity.IsRunningOnTeamCity)
  {
    TeamCity.WriteProgressMessage(message);
  }
  else
  {
	  Information(message);
  }
}

private void WriteStatusMessage(string message)
{
  if(TeamCity.IsRunningOnTeamCity)
  {
    TeamCity.WriteStatus(message);
  }
  else
  {
	  Information(message);
  }
}


///////////////////////////////////////////////////////////////////////////////
// RUNNER
///////////////////////////////////////////////////////////////////////////////
if (string.IsNullOrEmpty(targets)) {
	RunTarget(target);
}
else {
	foreach (var t in targets.Split('+')) {
		RunTarget(t);
	}
}
