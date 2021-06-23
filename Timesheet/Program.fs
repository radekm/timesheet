module Program

open System
open System.IO

open FSharp.Data
open Giraffe.ViewEngine
open MBrace.FsPickler
open Microsoft.Graph

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

let downloadChannelsFromTeams (ctx : Db.TimesheetDbContext) (client : GraphServiceClient) =
    let channelsInDb = ctx.Channels |> Seq.map (fun ch -> ch.Id, ch) |> Map.ofSeq
    printfn $"Database contains %d{channelsInDb.Count} channels"
    let channelsInTeams = Teams.listChannels client
    printfn $"Teams has %d{channelsInTeams.Length} channels"

    let mutable created = 0
    let mutable updated = 0
    let mutable same = 0
    channelsInTeams
    |> List.iter (fun ch ->
        let json = Db.convertToJson ch
        match channelsInDb |> Map.tryFind ch.Id with
        | None ->
            created <- created + 1
            ctx.Channels.Add { Id = ch.Id
                               Name = ch.Name
                               TeamName = ch.Team.Name
                               Json = json
                               LastDownload = DateTimeOffset.MinValue
                               Messages = ResizeArray()
                             }
            |> ignore
        | Some dbChannel ->
            if dbChannel.Json <> json then
                updated <- updated + 1
                dbChannel.Name <- ch.Name
                dbChannel.TeamName <- ch.Team.Name
                dbChannel.Json <- json
            else same <- same + 1)
    printfn "%d channels will be created, %d channels will be updated, %d channels are already up to date"
        created updated same

let downloadChatsFromTeams (ctx : Db.TimesheetDbContext) (client : GraphServiceClient) =
    let chatsInDb = ctx.Chats |> Seq.map (fun ch -> ch.Id, ch) |> Map.ofSeq
    printfn $"Database contains %d{chatsInDb.Count} chats"
    let chatsInTeams = Teams.listChats client
    printfn $"Teams has %d{chatsInTeams.Length} chats"

    let mutable created = 0
    let mutable updated = 0
    let mutable same = 0
    chatsInTeams
    |> List.iter (fun ch ->
        let json = Db.convertToJson ch
        match chatsInDb |> Map.tryFind ch.Id with
        | None ->
            created <- created + 1
            ctx.Chats.Add { Id = ch.Id
                            Name = ch.Name
                            Json = json
                            LastDownload = DateTimeOffset.MinValue
                            Messages = ResizeArray()
                          }
            |> ignore
        | Some dbChat ->
            if dbChat.Json <> json then
                updated <- updated + 1
                dbChat.Name <- ch.Name
                dbChat.Json <- json
            else same <- same + 1)
    printfn "%d chats will be created, %d chats will be updated, %d chats are already up to date"
        created updated same

let downloadChannelsAndChatsFromTeams (config : Config.Root) =
    let teamsConfig = { Teams.AppId = config.Teams.AppId }
    use ctx = new Db.TimesheetDbContext()
    let client = Teams.createClient teamsConfig

    downloadChannelsFromTeams ctx client
    downloadChatsFromTeams ctx client

    ctx.SaveChanges() |> ignore
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
    | "download-channels-and-chats-teams" -> downloadChannelsAndChatsFromTeams config
    | "write-summary" ->
        let fromDate = DateTime.Parse argv.[3]
        let toDate = DateTime.Parse argv.[4]
        writeSummary config dataFolder fromDate toDate
    | _ -> failwithf "Unknown command %s" command

    0
