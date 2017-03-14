using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static Version Version452 = new Version("4.5.2");

    static int Main(string[] args)
    {
        var testProjects = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.*proj")
                                    .Where(f => !f.EndsWith(".xproj"))
                                    .ToList();

        if (testProjects.Count == 0)
        {
            Console.Error.WriteLine("Could not find any project file in the current directory.");
            return -1;
        }

        if (testProjects.Count > 1)
        {
            Console.Error.WriteLine($"Multiple project files were found; only a single project file is supported. Found: {string.Join(", ", testProjects.Select(x => Path.GetFileName(x)))}");
            return -1;
        }

        var testProject = testProjects[0];
        var testProjectFolder = Path.GetDirectoryName(testProject);
        var testProjectFile = Path.GetFileName(testProject);
        var objFolder = Path.Combine(testProjectFolder, "obj");

        var projectPropsFile = Path.Combine(objFolder, testProjectFile + ".dotnet-xunit.props");
        File.WriteAllText(projectPropsFile, @"
<Project>
  <PropertyGroup>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <CopyNuGetImplementations>true</CopyNuGetImplementations>
    <DebugType Condition=""'$(TargetFrameworkIdentifier)' != '.NETCoreApp'"">Full</DebugType>
    <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
    <GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>
    <GenerateDependencyFile>true</GenerateDependencyFile>
    <OutputType Condition=""'$(TargetFrameworkIdentifier)' == '.NETCoreApp'"">Exe</OutputType>
  </PropertyGroup>
</Project>");

        var projectTargetsFile = Path.Combine(objFolder, testProjectFile + ".dotnet-xunit.targets");
        File.WriteAllText(projectTargetsFile, @"
<Project>
   <Target Name=""_Xunit_GetTargetFrameworks"">
     <ItemGroup Condition="" '$(TargetFrameworks)' == '' "">
       <_XunitTargetFrameworksLines Include=""$(TargetFramework)"" />
     </ItemGroup>
     <ItemGroup Condition="" '$(TargetFrameworks)' != '' "">
       <_XunitTargetFrameworksLines Include=""$(TargetFrameworks)"" />
     </ItemGroup>
     <WriteLinesToFile File=""$(_XunitInfoFile)"" Lines=""@(_XunitTargetFrameworksLines)"" Overwrite=""true"" />
   </Target>
   <Target Name=""_Xunit_GetTargetValues"">
     <ItemGroup>
       <_XunitInfoLines Include=""OutputPath: $(OutputPath)""/>
       <_XunitInfoLines Include=""AssemblyName: $(AssemblyName)""/>
       <_XunitInfoLines Include=""TargetFileName: $(TargetFileName)""/>
       <_XunitInfoLines Include=""TargetFrameworkIdentifier: $(TargetFrameworkIdentifier)""/>
       <_XunitInfoLines Include=""TargetFrameworkVersion: $(TargetFrameworkVersion)""/>
     </ItemGroup>
     <WriteLinesToFile File=""$(_XunitInfoFile)"" Lines=""@(_XunitInfoLines)"" Overwrite=""true"" />
   </Target>
</Project>");

        var tmpFile = Path.GetTempFileName();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"msbuild \"{testProject}\" /t:_Xunit_GetTargetFrameworks /nologo \"/p:_XunitInfoFile={tmpFile}\""
        };

        WriteLine($"Detecting target frameworks in {testProjectFile}...");

        try
        {
            var process = Process.Start(psi);
            var returnValue = 0;

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                WriteLineError("Detection failed!");
                return -1;
            }

            var targetFrameworks = File.ReadAllLines(tmpFile);
            foreach (var targetFramework in targetFrameworks)
                returnValue = Math.Max(RunTargetFramework(testProject, targetFramework, build: true), returnValue);

            return returnValue;
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    static int RunTargetFramework(string testProject, string targetFramework, bool build)
    {
        var targets = "";
        if (build)
        {
            WriteLine($"Building for framework {targetFramework}...");
            targets = "Build;_Xunit_GetTargetValues";
        }
        else
        {
            WriteLine($"Gathering project information for {targetFramework}...");
            targets = "_Xunit_GetTargetValues";
        }

        var tmpFile = Path.GetTempFileName();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"msbuild \"{testProject}\" /t:{targets} /nologo \"/p:_XunitInfoFile={tmpFile}\" \"/p:TargetFramework={targetFramework}\""
            };

            var process = Process.Start(psi);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                WriteLineError("Build failed!");
                return 1;
            }

            var lines = File.ReadAllLines(tmpFile);
            var outputPath = "";
            var assemblyName = "";
            var targetFileName = "";
            var targetFrameworkIdentifier = "";
            var targetFrameworkVersion = "";

            foreach (var line in lines)
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line.Substring(0, idx)?.Trim().ToLowerInvariant();
                var value = line.Substring(idx + 1)?.Trim();
                if (name == "outputpath")
                    outputPath = value;
                else if (name == "assemblyname")
                    assemblyName = value;
                else if (name == "targetfilename")
                    targetFileName = value;
                else if (name == "targetframeworkidentifier")
                    targetFrameworkIdentifier = value;
                else if (name == "targetframeworkversion")
                    targetFrameworkVersion = value;
            }

            var version = string.IsNullOrWhiteSpace(targetFrameworkVersion) ? new Version("0.0.0.0") : new Version(targetFrameworkVersion.TrimStart('v'));

            if (targetFrameworkIdentifier == ".NETCoreApp")
                return RunDotNetCoreProject(outputPath, assemblyName, targetFileName);
            if (targetFrameworkIdentifier == ".NETFramework" && version >= Version452)
                return RunDesktopProject(outputPath, targetFileName);

            WriteLineWarning($"Unsupported target framework '{targetFrameworkIdentifier} {version}' (only .NETCoreApp 1.0+ and .NETFramework 4.5.2+ are supported)");
            return 0;
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    static int RunDesktopProject(string outputPath, string targetFileName)
    {
        var thisAssemblyPath = typeof(Program).GetTypeInfo().Assembly.Location;
#if DEBUG
        var consoleFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisAssemblyPath), "..", "..", "..", "..", "xunit.console", "bin", "Debug", "net452", "win7-x86"));
#else
        var consoleFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisAssemblyPath), "..", "..", "tools", "net452"));
#endif

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(consoleFolder, "xunit.console.exe"),
            Arguments = $@"""{targetFileName}""",
            WorkingDirectory = outputPath
        };

//#if DEBUG
        WriteLineDebug($"EXEC: {psi.FileName} {psi.Arguments}");
//#endif

        var runTests = Process.Start(psi);
        runTests.WaitForExit();

        return runTests.ExitCode;
    }

    static int RunDotNetCoreProject(string outputPath, string assemblyName, string targetFileName)
    {
        var thisAssemblyPath = typeof(Program).GetTypeInfo().Assembly.Location;
#if DEBUG
        var consoleFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisAssemblyPath), "..", "..", "..", "..", "xunit.console", "bin", "Debug", "netcoreapp1.0"));
#else
        var consoleFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisAssemblyPath), "..", "..", "tools", "netcoreapp1.0"));
#endif

        foreach (var sourceFile in Directory.EnumerateFiles(consoleFolder))
        {
            var destinationFile = Path.Combine(outputPath, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, true);
        }

        var dotnetArguments = "";
        if (File.Exists(Path.Combine(outputPath, assemblyName + ".deps.json")))
            dotnetArguments += $@" --depsfile ""{assemblyName}.deps.json""";
        if (File.Exists(Path.Combine(outputPath, assemblyName + ".runtimeconfig.json")))
            dotnetArguments += $@" --runtimeconfig ""{assemblyName}.runtimeconfig.json""";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $@"exec {dotnetArguments} xunit.console.dll ""{targetFileName}""",
            WorkingDirectory = outputPath
        };

//#if DEBUG
        WriteLineDebug($"EXEC: {psi.FileName} {psi.Arguments}");
//#endif

        var runTests = Process.Start(psi);
        runTests.WaitForExit();

        return runTests.ExitCode;
    }

    static void WriteLine(string message)
        => WriteLineWithColor(ConsoleColor.White, message);

    static void WriteLineDebug(string message)
        => WriteLineWithColor(ConsoleColor.DarkGray, message);

    static void WriteLineError(string message)
        => WriteLineWithColor(ConsoleColor.Red, message, Console.Error);

    static void WriteLineWarning(string message)
        => WriteLineWithColor(ConsoleColor.Yellow, message);

    static void WriteLineWithColor(ConsoleColor color, string message, TextWriter writer = null)
    {
        Console.ForegroundColor = color;
        (writer ?? Console.Out).WriteLine(message);
        Console.ResetColor();
    }
}
