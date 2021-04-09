module Program

open System
open System.IO
open System.Text.RegularExpressions

open FSharp.Data
open Giraffe.ViewEngine
open MBrace.FsPickler

type Config = JsonProvider<"ConfigSample.json">

module DataFile =
    let gitLab dataFolder =
        Path.Combine(dataFolder, "GitLab.bin")
        |> Path.GetFullPath

    let teams dataFolder =
        Path.Combine(dataFolder, "Teams.bin")
        |> Path.GetFullPath

    let read dataFile =
        let ser = FsPickler.CreateBinarySerializer()
        use stream = File.OpenRead dataFile
        ser.Deserialize stream

    let write dataFile graph =
        use stream = File.OpenWrite dataFile
        let ser = FsPickler.CreateBinarySerializer()
        ser.Serialize(stream, graph)

let downloadDataFromGitLab (config : Config.Root) dataFolder =
    let gitLabConfig =
        { GitLab.ApiUrl = config.GitLab.ApiUrl
          GitLab.ApiToken = config.GitLab.ApiToken }

    let gitLabDataFile = DataFile.gitLab dataFolder
    printfn $"Downloading data from GitLab into %s{gitLabDataFile}"
    let mrs = GitLab.fetchData gitLabConfig

    DataFile.write gitLabDataFile mrs
    printfn "Data from GitLab downloaded and saved"

let downloadDataFromTeams (config : Config.Root) dataFolder =
    let teamsConfig = { Teams.AppId = config.Teams.AppId }

    let teamsDataFile = DataFile.teams dataFolder
    printfn $"Downloading data from Teams into %s{teamsDataFile}"
    let conversations = Teams.fetchData teamsConfig

    DataFile.write teamsDataFile conversations
    printfn "Data from Teams downloaded and saved"

let writeSummary (config : Config.Root) dataFolder (fromDate : DateTime) (toDate : DateTime) =
    let gitLabUserName = config.GitLab.UserName
    let mrs : list<GitLab.MergeRequest> = DataFile.gitLab dataFolder |> DataFile.read

    let teamsUserId = config.Teams.UserId
    let conversations : Teams.AllConversations = DataFile.teams dataFolder |> DataFile.read

    let report = Report.htmlReport fromDate toDate gitLabUserName teamsUserId mrs conversations
    let html = RenderView.AsBytes.htmlDocument report
    let path = "report.html" |> Path.GetFullPath
    printfn "Writing summary to %s" path
    File.WriteAllBytes(path, html)

[<EntryPoint>]
let main argv =
    let command = argv.[0]
    let config =
        let path = Path.GetFullPath argv.[1]
        printfn $"Loading config from %s{path}"
        Config.Load path
    let dataFolder = argv.[2]

    match command with
    | "download-data-gitlab" -> downloadDataFromGitLab config dataFolder
    | "download-data-teams" -> downloadDataFromTeams config dataFolder
    | "write-summary" ->
        let fromDate = DateTime.Parse argv.[3]
        let toDate = DateTime.Parse argv.[4]
        writeSummary config dataFolder fromDate toDate
    | _ -> failwithf "Unknown command %s" command

    0
