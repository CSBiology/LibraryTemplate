// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#r "paket:
nuget BlackFox.Fake.BuildTask
nuget Fake.Core.Target
nuget Fake.Core.Process
nuget Fake.Core.ReleaseNotes
nuget Fake.IO.FileSystem
nuget Fake.DotNet.Cli
nuget Fake.DotNet.MSBuild
nuget Fake.DotNet.AssemblyInfoFile
nuget Fake.DotNet.Paket
nuget Fake.DotNet.FSFormatting
nuget Fake.DotNet.Fsi
nuget Fake.DotNet.NuGet
nuget Fake.Api.Github
nuget Fake.DotNet.Testing.Expecto //"

#load ".fake/build.fsx/intellisense.fsx"

open BlackFox.Fake
open System.IO
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing
open Fake.IO.Globbing.Operators
open Fake.DotNet.Testing
open Fake.Tools
open Fake.Api
open Fake.Tools.Git

Target.initEnvironment ()

[<AutoOpen>]
module MessagePrompts =

    let prompt (msg:string) =
        System.Console.Write(msg)
        System.Console.ReadLine().Trim()
        |> function | "" -> None | s -> Some s
        |> Option.map (fun s -> s.Replace ("\"","\\\""))

    let rec promptYesNo msg =
        match prompt (sprintf "%s [Yn]: " msg) with
        | Some "Y" | Some "y" -> true
        | Some "N" | Some "n" -> false
        | _ -> System.Console.WriteLine("Sorry, invalid answer"); promptYesNo msg

    let releaseMsg = """This will stage all uncommitted changes, push them to the origin and bump the release version to the latest number in the RELEASE_NOTES.md file. 
        Do you want to continue?"""

    let releaseDocsMsg = """This will push the docs to gh-pages. Remember building the docs prior to this. Do you want to continue?"""

[<AutoOpen>]
module ProjectInfo =
    
    let project         = "LibraryTemplate"
    let summary         = "An open source bioinformatics toolbox written in F#. <https://csbiology.github.io/BioFSharp/>"
    let solutionFile    = "LibraryTemplate.sln"
    let configuration   = "Release"
    let gitOwner = "CSBiology"
    let gitHome = sprintf "%s/%s" "https://github.com" gitOwner
    let gitName = "LibraryTemplate"
    let website = "/LibraryTemplate"
    let pkgDir = "pkg"

    //Build configurations
    let buildConfiguration = DotNet.Custom <| Environment.environVarOrDefault "configuration" configuration
    
    let buildServerSuffix = 
        match BuildServer.buildServer with
        |BuildServer.AppVeyor   -> sprintf "appveyor.%s" BuildServer.appVeyorBuildVersion
        |BuildServer.Travis     -> sprintf "travis.%s" BuildServer.travisBuildNumber
        | _                     -> ""
    
    // Read additional information from the release notes document
    let release = ReleaseNotes.load "RELEASE_NOTES.md"

    let allTestAssemblies = 
        !! ("tests/*.Tests/bin" </> configuration </> "**" </> "netcoreapp3.1/*Tests.dll")

    let allProjectPaths =
        !! "src/**/*.fsproj"
        |>  Seq.map 
            (fun f -> (Path.getDirectory f))

//---------------------------------------------------------------------------------------------------------------------------------
//======================================================= Build Tasks =============================================================
//---------------------------------------------------------------------------------------------------------------------------------

// --------------------------------------------------------------------------------------------------------------------------------
// Clean build results

let clean = 
    BuildTask.create "clean" [] {
        Shell.cleanDirs [
            "bin"; "temp"; "pkg" 
            yield! allProjectPaths |> Seq.map (fun x -> x </> "bin")
            ]
    }

let cleanDocs = 
    BuildTask.create "cleanDocs" [] {
        Shell.cleanDirs ["docs"]
    }

// --------------------------------------------------------------------------------------------------------------------------------
// Generate assembly info files with the right version & up-to-date information. Clean first.

let assemblyInfo = 
    BuildTask.create "assemblyInfo" [clean.IfNeeded] {
        let getAssemblyInfoAttributes projectName =
            [ AssemblyInfo.Title (projectName)
              AssemblyInfo.Product project
              AssemblyInfo.Description summary
              AssemblyInfo.Version release.AssemblyVersion
              AssemblyInfo.FileVersion release.AssemblyVersion
              AssemblyInfo.Configuration configuration ]

        let getProjectDetails projectPath =
            let projectName = Path.GetFileNameWithoutExtension(projectPath)
            ( projectPath,
              projectName,
              Path.GetDirectoryName(projectPath),
              (getAssemblyInfoAttributes projectName)
            )

        !! "src/**/*.fsproj"
        |> Seq.map getProjectDetails
        |> Seq.iter 
            (fun (projFileName, _, folderName, attributes) ->
                AssemblyInfoFile.createFSharp (folderName </> "AssemblyInfo.fs") attributes
            )
    }


// --------------------------------------------------------------------------------------------------------------------------------
// Build library & test project. build assembly info first


let buildAll = 
    BuildTask.create "buildAll" [clean.IfNeeded; assemblyInfo] {
        solutionFile 
        |> DotNet.build (fun p -> 
            { p with
                Configuration = buildConfiguration }
            )
    }




// --------------------------------------------------------------------------------------------------------------------------------
// Copies binaries from default VS location to expected bin folder
// But keeps a subdirectory structure for each project in the
// src folder to support multiple project outputs
// Build first.

let copyBinaries = 
        BuildTask.create "copyBinaries" [clean.IfNeeded; assemblyInfo.IfNeeded; buildAll] {
        !! "src/**/*.fsproj"
        |>  Seq.map 
            (fun f -> (Path.getDirectory f) </> "bin" </> configuration, "bin" </> (Path.GetFileNameWithoutExtension f))
        |>  Seq.iter 
            (fun (fromDir, toDir) -> 
                printfn "copy from %s to %s" fromDir toDir
                Shell.copyDir toDir fromDir (fun _ -> true)
            )
    }


// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

let runTests assembly =
    [Path.Combine(__SOURCE_DIRECTORY__, assembly)]
    |> Expecto.run (fun p ->
        { p with 
            WorkingDirectory = __SOURCE_DIRECTORY__
        })

let runTestsAll =
    BuildTask.create "runTestsAll" [buildAll] {
        allTestAssemblies
        |> Seq.iter runTests
    }


// --------------------------------------------------------------------------------------
// Build a NuGet package. Build and test packages first
    

let buildPrereleasePackages = 
    BuildTask.create "buildPrereleasePackages" [buildAll; runTestsAll] {
        printfn "Please enter pre-release package suffix"
        let suffix = System.Console.ReadLine()
        Paket.pack(fun p -> 
            { p with
                
                ToolType = ToolType.CreateLocalTool()
                OutputPath = pkgDir
                Version = sprintf "%s-%s" release.NugetVersion suffix
                ReleaseNotes = release.Notes |> String.toLines })
    }

let BuildReleasePackages = 
    BuildTask.create "BuildReleasePackages" [buildAll; runTestsAll] {
        Paket.pack(fun p ->
            { p with
                ToolType = ToolType.CreateLocalTool()
                OutputPath = pkgDir
                Version = release.NugetVersion
                ReleaseNotes = release.Notes |> String.toLines })
    }

//dependencies for cI will be resolved in the CI build task.
let buildCIPackages name config projectPaths = 
    BuildTask.create (sprintf "buildCIPackages%s" name) [
        buildAll.IfNeeded
        runTestsAll.IfNeeded
        copyBinaries.IfNeeded
    ] 
        {
            projectPaths
            |> Seq.iter 
                (fun proj -> 
                    Paket.pack (fun p ->
                        { p with
                            BuildConfig = config
                            TemplateFile = proj </> "paket.template"
                            ToolType = ToolType.CreateLocalTool()
                            OutputPath = pkgDir
                            Version = sprintf "%s-%s" release.NugetVersion buildServerSuffix
                            ReleaseNotes = release.Notes |> String.toLines 
                        }
                    )
                )
        }

let publishNugetPackages = 
    BuildTask.create "publishNugetPackages" [buildAll; runTestsAll; BuildReleasePackages] {
        Paket.push(fun p ->
            { p with
                WorkingDir = pkgDir
                ToolType = ToolType.CreateLocalTool()
                ApiKey = Environment.environVarOrDefault "NuGet-key" "" })
    }


//Token Targets for local builds
let fullBuildChainLocal = 
    BuildTask.createEmpty "fullBuildChainLocal" [
        clean
        assemblyInfo
        buildAll
        copyBinaries
        runTestsAll
        cleanDocs
        //generateDocumentation
        BuildReleasePackages
    ]

BuildTask.runOrDefaultWithArguments fullBuildChainLocal