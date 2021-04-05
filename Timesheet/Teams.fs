module Teams

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks

open Microsoft.Graph
open Microsoft.Identity.Client

type Config = { AppId: string }

/// Authentication provider which shows device code in the console
/// and waits until the user uses it to authenticate.
type private DeviceCodeAuth(appId : string, scopes : string list) =
    let msalClient =
        PublicClientApplicationBuilder
            .Create(appId)
            .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount, true)
            .Build()

    let mutable userAccount = None

    let getAccessToken () =
        let executeAsync (builder : AbstractAcquireTokenParameterBuilder<_>) =
            builder.ExecuteAsync()
            |> Async.AwaitTask
        match userAccount with
        | None -> async {
            let showDeviceCode (deviceCode : DeviceCodeResult) =
                // Show device code and instructions where to enter it.
                printfn "%s" deviceCode.Message
                Task.CompletedTask
            let! result =
                // Waits until user enters device code in MS web page.
                msalClient.AcquireTokenWithDeviceCode(scopes, fun deviceCode -> showDeviceCode deviceCode)
                |> executeAsync
            do userAccount <- Some result.Account
            return result.AccessToken }
        | Some account -> async {
            let! result =
                // Return cached token or automatically refresh it.
                msalClient.AcquireTokenSilent(scopes, account)
                |> executeAsync
            return result.AccessToken }

    interface IAuthenticationProvider with
        override _.AuthenticateRequestAsync(request : HttpRequestMessage) =
            async {
                let! token = getAccessToken ()
                request.Headers.Authorization <- AuthenticationHeaderValue("bearer", token)
            } |> Async.StartAsTask :> Task

// Microsoft Graph Beta doesn't have `PageIterator` so we implemented that here.
let inline getItems< 'A, ^Pg, ^Req when ^Req : (member GetAsync : CancellationToken -> Task<'Pg>)
                                   and  ^Req : (member Client : IBaseClient)
                                   and  ^Req : null
                                   and  ^Pg :> ICollectionPage<'A>
                                   and  ^Pg : (member InitializeNextPageRequest : IBaseClient * string -> unit)
                                   and  ^Pg : (member NextPageRequest : ^Req) >
    (req : ^Req) : 'A list =

    let client = (^Req : (member Client : IBaseClient) req)
    let result = ResizeArray()

    let rec go req =
        let page = (^Req : (member GetAsync : CancellationToken -> Task<'Pg>) (req, Unchecked.defaultof<_>)).Result
        page
        |> Seq.iter result.Add

        // Try to use a link to the next page (if any) to initialize a request for the next page.
        match page.AdditionalData.TryGetValue "@odata.nextLink" with
        | true, next ->
            (^Pg : (member InitializeNextPageRequest : IBaseClient * string -> unit) (page, client, string next))
        | _ -> ()

        // If request to the next page was initialized then download the next page.
        match (^Pg : (member NextPageRequest : ^Req) page) with
        | null -> ()
        | req -> go req
    go req

    result |> Seq.toList

type User = { Id : string; Name : string }
type Team = { Id : string; Name : string }
// More permissions are needed to list channel members. So we omit that.
type Channel = { Team : Team; Id : string; Name : string }
// Chat members can be listed.
type Chat = { Id : string; Members : User list; Type : string; Topic : string option }
type Reaction = { ReactionType : string; UserId : string; Created: DateTimeOffset }
[<RequireQualifiedAccess>]
type Mention = User of User | Other of string
[<RequireQualifiedAccess>]
type MessageContent = Text of string | Html of string
type Message = { Id : string
                 // None when message was generated by a bot.
                 Author : User option
                 Created : DateTimeOffset
                 Subject: string option
                 Body : MessageContent
                 Reactions: Reaction Set
                 Mentions: Mention Set
               }

let listTeams (client : GraphServiceClient) : Team list =
    client.Me.JoinedTeams.Request()
    |> getItems
    |> List.map (fun t -> { Id = t.Id; Name = t.DisplayName })

let listChannels (client : GraphServiceClient) : Channel list =
    listTeams client
    |> List.collect (fun t ->
        client.Teams.[t.Id].Channels.Request()
        |> getItems
        |> List.map (fun ch -> { Team = t; Id = ch.Id; Name = ch.DisplayName }))

let listChats (client : GraphServiceClient) : Chat list =
    client.Me.Chats.Request()
    |> getItems
    |> List.map (fun ch ->
        let topic = Option.ofObj ch.Topic
        let members : User list =
            try
                client.Me.Chats.[ch.Id].Members.Request()
                |> getItems
                |> List.map (fun m -> { Id = m.Id; Name = m.DisplayName })
            with _ when ch.ChatType.Value = ChatType.Meeting ->
                printfn $"Unable to list members of meeting %s{ch.Id} about %A{topic}"
                []
        { Id = ch.Id; Members = members; Type = string ch.ChatType.Value; Topic = topic })

let private convertChatMessage (m : ChatMessage) : Message option =
    if isNull m.Body.Content || isNull m.From then None
    elif m.MessageType.Value <> ChatMessageType.Message then failwith $"Unexpected message type %A{m.MessageType}"
    else Some { Id = m.Id
                Author =
                    m.From.User
                    |> Option.ofObj
                    |> Option.map (fun u -> { Id = u.Id; Name = u.DisplayName })
                Created = m.CreatedDateTime.Value
                Subject =
                    m.Subject
                    |> Option.ofObj
                    |> Option.filter (fun s -> s.Trim() <> "")
                Body =
                    match m.Body.ContentType.Value with
                    | BodyType.Html -> MessageContent.Html m.Body.Content
                    | BodyType.Text -> MessageContent.Text m.Body.Content
                    | ct -> failwithf "Unexpected content type %A" ct
                Reactions =
                    m.Reactions
                    |> Seq.map (fun r -> { ReactionType = r.ReactionType
                                           UserId = r.User.User.Id
                                           Created = r.CreatedDateTime.Value })
                    |> Set.ofSeq
                Mentions =
                    m.Mentions
                    |> Seq.map (fun m ->
                        match m.Mentioned.User with
                        | null -> Mention.Other m.MentionText
                        | u -> Mention.User { Id = u.Id; Name = u.DisplayName })
                    |> Set.ofSeq
              }

let listChannelMessages (client : GraphServiceClient) (ch : Channel) : Message list =
    client.Teams.[ch.Team.Id].Channels.[ch.Id].Messages.Request()
    |> getItems
    |> List.choose convertChatMessage

let listChatMessages (client : GraphServiceClient) (ch : Chat) : Message list =
    client.Chats.[ch.Id].Messages.Request()
    |> getItems
    |> List.choose convertChatMessage

let listRepliesToChannelMessage (client : GraphServiceClient) (ch : Channel) (m : Message) : Message list =
    client.Teams.[ch.Team.Id].Channels.[ch.Id].Messages.[m.Id].Replies.Request()
    |> getItems
    |> List.choose (fun origReply ->
        if isNull origReply.ReplyToId then None
        elif origReply.ReplyToId <> m.Id then failwithf $"Got reply in channel %A{ch} which is not reply to %A{m}"
        else convertChatMessage origReply)

type MessageWithReplies = { Message : Message
                            Replies : Message list }

type ChannelWithMessages = { Channel : Channel
                             Messages : MessageWithReplies list }

/// Chat doesn't have replies - we use `MessageWithReplies` to unify processing with channels.
type ChatWithMessages = { Chat : Chat
                          Messages : MessageWithReplies list }

type AllConversations = { Channels : ChannelWithMessages list
                          Chats : ChatWithMessages list }

let fetchChannel (client : GraphServiceClient) (ch : Channel) : ChannelWithMessages =
    printfn $"Downloading channel %s{ch.Id} (%s{ch.Name}) in team %s{ch.Team.Id} (%s{ch.Team.Name})"
    let messagesWithReplies =
        listChannelMessages client ch
        |> List.map (fun m -> { Message = m; Replies = listRepliesToChannelMessage client ch m })
    { Channel = ch; Messages = messagesWithReplies }

let fetchChat (client : GraphServiceClient) (ch : Chat) : ChatWithMessages =
    let _ =
        let memberNames = ch.Members |> List.map (fun m -> m.Name)
        let about = ch.Topic |> Option.map (sprintf " about %s") |> Option.defaultValue ""
        printfn $"Downloading %s{ch.Type} chat %s{ch.Id} with %A{memberNames}%s{about}"
    let messagesWithReplies =
        listChatMessages client ch
        // There are no replies in chats.
        |> List.map (fun m -> { Message = m; Replies = [] })
    { Chat = ch; Messages = messagesWithReplies }

let fetchData (config : Config) : AllConversations =
    let scopesForTeams = ["User.Read"; "Chat.Read"; "Team.ReadBasic.All"; "Channel.ReadBasic.All"]
    let client = GraphServiceClient(DeviceCodeAuth(config.AppId, scopesForTeams))

    { Channels =
          listChannels client
          |> List.map (fetchChannel client)
      Chats =
          listChats client
          |> List.map (fetchChat client)
    }