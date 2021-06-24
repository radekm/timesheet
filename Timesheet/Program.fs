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

let downloadMessagesFromTeamsChannel (ctx : Db.TimesheetDbContext) (client : GraphServiceClient) (ch : Db.Channel) =
    // Update time when the channel was downloaded.
    ch.LastDownload <- DateTimeOffset.UtcNow

    let messagesInDb =
        Db.queryChannelMessages ctx ch
        |> Seq.map (fun m -> m.Id, m)
        |> Map.ofSeq

    let ch : Teams.Channel = Db.convertFromJson ch.Json
    printfn "Database has %d messages for channel %s/%s" messagesInDb.Count ch.Team.Name ch.Name
    let messagesInTeams = (Teams.fetchChannel client ch).Messages
    printfn "Teams has %d messages" messagesInTeams.Length

    let mutable created = 0
    let mutable updated = 0
    let mutable same = 0
    messagesInTeams
    |> List.iter (fun m ->
        let json = Db.convertToJson m
        match messagesInDb |> Map.tryFind m.Message.Id with
        | None ->
            created <- created + 1
            ctx.ChannelMessages.Add { Id = m.Message.Id
                                      ChannelId = ch.Id
                                      Created = m.Message.Created
                                      Json = json
                                    }
            |> ignore
        | Some dbMessage ->
            if dbMessage.Json <> json then
                updated <- updated + 1
                dbMessage.Created <- m.Message.Created
                dbMessage.Json <- json
            else same <- same + 1)

    // Save downloaded message to database so we don't lose them
    // if other call fails.
    ctx.SaveChanges() |> ignore
    printfn "%d messages were created, %d messages were updated, %d messages were already up to date"
        created updated same

let downloadMessagesFromTeamsChat (ctx : Db.TimesheetDbContext) (client : GraphServiceClient) (ch : Db.Chat) =
    // Update time when the chat was downloaded.
    ch.LastDownload <- DateTimeOffset.UtcNow

    let messagesInDb =
        Db.queryChatMessages ctx ch
        |> Seq.map (fun m -> m.Id, m)
        |> Map.ofSeq

    let ch : Teams.Chat = Db.convertFromJson ch.Json
    printfn "Database has %d messages for chat %s" messagesInDb.Count ch.Name
    let messagesInTeams = (Teams.fetchChat client ch).Messages
    printfn "Teams has %d messages" messagesInTeams.Length

    let mutable created = 0
    let mutable updated = 0
    let mutable same = 0
    messagesInTeams
    |> List.iter (fun m ->
        let json = Db.convertToJson m
        match messagesInDb |> Map.tryFind m.Id with
        | None ->
            created <- created + 1
            ctx.ChatMessages.Add { Id = m.Id
                                   ChatId = ch.Id
                                   Created = m.Created
                                   Json = json
                                 }
            |> ignore
        | Some dbMessage ->
            if dbMessage.Json <> json then
                updated <- updated + 1
                dbMessage.Created <- m.Created
                dbMessage.Json <- json
            else same <- same + 1)

    // Save downloaded message to database so we don't lose them
    // if other call fails.
    ctx.SaveChanges() |> ignore
    printfn "%d messages were created, %d messages were updated, %d messages were already up to date"
        created updated same

let downloadMessagesFromTeams (config : Config.Root) (atLeastToDate : DateTime) =
    let teamsConfig = { Teams.AppId = config.Teams.AppId }
    use ctx = new Db.TimesheetDbContext()
    let client = Teams.createClient teamsConfig

    let channelsToDownload =
        ctx.Channels
        |> Seq.filter (fun ch -> ch.LastDownload.Date < atLeastToDate)
        |> Seq.toList
    printfn "Found %d channels to download messages" channelsToDownload.Length

    channelsToDownload |> List.iter (downloadMessagesFromTeamsChannel ctx client)

    let chatsToDownload =
        ctx.Chats
        |> Seq.filter (fun ch -> ch.LastDownload.Date < atLeastToDate)
        |> Seq.toList
    printfn "Found %d chats to download messages" chatsToDownload.Length

    chatsToDownload |> List.iter (downloadMessagesFromTeamsChat ctx client)

    printfn "Messages successfully downloaded"

let readConversationsFromDb (ctx : Db.TimesheetDbContext) : Teams.AllConversations =
    { Channels =
        ctx.Channels
        |> Seq.map (fun ch ->
            { Channel = Db.convertFromJson ch.Json
              Messages =
                  Db.queryChannelMessages ctx ch
                  |> Seq.map (fun m -> Db.convertFromJson m.Json)
                  |> Seq.toList
            } : Teams.ChannelWithMessages)
        |> Seq.toList
      Chats =
        ctx.Chats
        |> Seq.map (fun ch ->
            { Chat = Db.convertFromJson ch.Json
              Messages =
                  Db.queryChatMessages ctx ch
                  |> Seq.map (fun m -> Db.convertFromJson m.Json)
                  |> Seq.toList
            } : Teams.ChatWithMessages)
        |> Seq.toList
    }

let writeSummary (config : Config.Root) dataFolder (fromDate : DateTime) (toDate : DateTime) =
    use ctx = new Db.TimesheetDbContext()

    let gitLabUserName = config.GitLab.UserName
    let mrs : list<GitLab.MergeRequest> = DataFile.gitLab dataFolder |> DataFile.read

    let teamsUserId = config.Teams.UserId
    let conversations : Teams.AllConversations = readConversationsFromDb ctx

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
    | "download-channels-and-chats-teams" -> downloadChannelsAndChatsFromTeams config
    | "download-messages-teams" ->
        let atLeastToDate = DateTime.Parse argv.[3]
        downloadMessagesFromTeams config atLeastToDate
    | "write-summary" ->
        let fromDate = DateTime.Parse argv.[3]
        let toDate = DateTime.Parse argv.[4]
        writeSummary config dataFolder fromDate toDate
    | _ -> failwithf "Unknown command %s" command

    0
