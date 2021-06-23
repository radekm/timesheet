module Report

open System
open System.Text.RegularExpressions

open FSharp.Data

open Giraffe.ViewEngine.Attributes
open Giraffe.ViewEngine.HtmlElements

let belongsToDay (day : DateTime) (dt : DateTimeOffset) =
    // Work day doesn't stop at midnight. If some work is done
    // `workDayOffset` hours after midnight then we still count it to the previous day.
    let workDayOffset = 3.0
    let start = DateTimeOffset(day.Date.AddHours workDayOffset, dt.Offset)
    let stop = start.AddDays(1.0)
    start <= dt && dt < stop

type MrSummary =
    { Title : string
      New : bool  // Whether MR was created on specified day.
      Authored : bool  // Whether MR was authored by specified user.
      ActivityAddedCommits : int  // How many commits added a specified user on a specified day.
      ActivityCommented : string list  // Comments added by a specified user on a specified day.
      ActivityReviewed : bool  // Whether a specified user finished a review on a specified day.
      ActivityMerged : bool  // Whether a specified user merged MR on a specified day.
    }

    member me.HasActivity =
        me.ActivityAddedCommits > 0 ||
        not me.ActivityCommented.IsEmpty ||
        me.ActivityReviewed ||
        me.ActivityMerged

let summarizeMrs
    (day : DateTime)
    (userName : string)
    (mrs : GitLab.MergeRequest list) =

    mrs
    |> Seq.map (fun mr ->
        let notes =
            mr.Discussions
            |> Seq.collect (fun d -> d.Notes)
            |> Seq.filter (fun note -> belongsToDay day note.CreatedAt)
            |> Seq.filter (fun note -> note.Author.Username = userName)
            |> Seq.toList
        // TODO Get commit messages instead of just number of commits.
        // FIXME Maybe the number of commits is wrong because single message may stand for multiple commits.
        let activityAddedCommits =
            let addedCommitsRegex = Regex("^added \\d+ commit")
            notes
            |> Seq.filter (fun note -> note.System)
            |> Seq.filter (fun note -> addedCommitsRegex.IsMatch note.Body)
            |> Seq.length
        let activityCommented =
            notes
            |> Seq.filter (fun note -> not note.System)
            |> Seq.map (fun note -> note.Body)
            |> Seq.toList

        let emoticonAdded =
            mr.Emoticons
            |> List.exists (fun e -> belongsToDay day e.CreatedAt && e.User.Username = userName)
        // TODO Detect if MR was approved.
        let activityReviewed = emoticonAdded

        let activityMerged =
            mr.MR.State = "merged" &&
            belongsToDay day mr.MR.MergedAt &&
            mr.MR.MergedBy.Username = userName

        // Activities which happened on `day`.
        { Title = mr.MR.Title
          New = belongsToDay day mr.MR.CreatedAt
          Authored = mr.MR.Author.Username = userName
          ActivityAddedCommits = activityAddedCommits
          ActivityCommented = activityCommented
          ActivityReviewed = activityReviewed
          ActivityMerged = activityMerged
        })
    |> Seq.filter (fun mr -> mr.HasActivity)
    |> Seq.toList

let formatMrsSummaries (summaries : MrSummary list) =
    let header =
        tr [] [
            th [] [str "Title"]
            th [] [str "Commits"]
            th [] [str "Comments"]
            th [] [str "Reviewed"]
            th [] [str "Merged"]
        ]
    let rows =
        summaries
        |> Seq.map (fun s ->
            let color =
                if s.Authored && s.New then "#52be80"  // Authored on specified day - darker green.
                elif s.Authored then "#abebc6"  // Authored other day - green.
                elif s.ActivityAddedCommits > 0 then " #fad7a0 "  // Orange.
                else "white"  // Only added comment or reviewed or merged.
            tr [sprintf $"background-color: %s{color}" |> _style] [
                td [] [s.Title |> str]
                td [] [s.ActivityAddedCommits |> string |> str]
                td [] [s.ActivityCommented |> List.length |> string |> str]
                td [] [s.ActivityReviewed |> string |> str]
                td [] [s.ActivityMerged |> string |> str]
            ])
        |> Seq.toList
    table [] (header :: rows)

let testMessage
    (day : DateTime)
    (userId : string)
    (msg : Teams.Message) =

    let hasAuthor = msg.Author |> Option.exists (fun a -> a.Id = userId)
    let hasDay = belongsToDay day msg.Created
    hasAuthor && hasDay

type ChatSummaryItem =
    | SkippedMessages
    // Not important messages are present only as a context for important ones.
    | Message of {| Message : Teams.Message; Important : bool |}

let summarizeChatMessages
    (day : DateTime)
    (userId : string)
    (messages : Teams.Message list) : ChatSummaryItem list =

    let mutable numOfMessagesToInclude = -1
    messages
    // Ignore messages from different days.
    |> List.filter (fun m -> belongsToDay day m.Created)
    |> List.sortBy (fun m -> m.Created)
    // Mark messages from `userId` as important.
    |> List.map (fun m -> {| Message = m; Important = m.Author |> Option.exists (fun a -> a.Id = userId) |})
    |> List.rev
    |> List.choose (fun m ->
        if m.Important then
            // Include important message + 3 additional messages as a context.
            numOfMessagesToInclude <- 4
        numOfMessagesToInclude <- numOfMessagesToInclude - 1
        match numOfMessagesToInclude with
        | n when n >= 0 -> Some (Message m)
        | -1 -> Some SkippedMessages
        | _ -> None)
    |> List.rev

type ChatSummary = { Chat : Teams.Chat; Messages : ChatSummaryItem list }

let summarizeChat (day : DateTime) (userId : string) (chat : Teams.ChatWithMessages) : ChatSummary =
    { Chat = chat.Chat
      Messages =
        chat.Messages
        // There are no replies in chat.
        |> List.map (fun msgWithReplies -> msgWithReplies.Message)
        |> summarizeChatMessages day userId
    }

type ChannelSummaryItem = { Message : Teams.Message; Important : bool; Replies : ChatSummaryItem list }

type ChannelSummary = { Channel : Teams.Channel; Messages : ChannelSummaryItem list }

let summarizeChannel (day : DateTime) (userId : string) (channel : Teams.ChannelWithMessages) : ChannelSummary =
    { Channel = channel.Channel
      Messages =
        channel.Messages
        |> List.map (fun msgWithReplies ->
            let m = msgWithReplies.Message
            { Message = m
              Important = belongsToDay day m.Created && m.Author |> Option.exists (fun a -> a.Id = userId)
              Replies = summarizeChatMessages day userId msgWithReplies.Replies
            })
        |> List.filter (fun m -> m.Important || not m.Replies.IsEmpty)
        |> List.sortBy (fun m -> m.Message.Created)
    }

let summarizeConversations
    (day : DateTime)
    (userId : string)
    (conversations : Teams.AllConversations) =

    {| Channels =
        conversations.Channels
        |> List.map (summarizeChannel day userId)
        |> List.filter (fun s -> not s.Messages.IsEmpty)
       Chats =
        conversations.Chats
        |> List.map (summarizeChat day userId)
        |> List.filter (fun s -> not s.Messages.IsEmpty)
    |}

let formatConversationSummary
    (summary : {| Channels : ChannelSummary list; Chats : ChatSummary list |}) =

    let formatBody (msg : Teams.Message) (important : bool) =
        let style =
            if important
            then "color: #000"
            else "color: #999"
        match msg.Body with
        | Teams.MessageContent.Text s -> s
        | Teams.MessageContent.Html s ->
            HtmlNode.Parse $"<div>%s{s}</div>"
            |> List.head
            |> HtmlNode.innerText
        |> fun s ->
            if s.Length > 500
            then s.Substring(0, 500) + "…"
            else s
        |> str
        |> fun node -> span [_style style] [node]

    let formatChatMessagesSummary (messages : ChatSummaryItem list) =
        match messages with
        | [] -> span [] []
        | _ ->
            messages
            |> List.map (function
                | SkippedMessages -> li [] [str "⋮"]
                | Message m -> li [] [formatBody m.Message m.Important])
            |> ul []

    let formatChannelSummary (s : ChannelSummary) =
        s.Messages
        |> List.map (fun m ->
            let replies = formatChatMessagesSummary m.Replies
            li [] [
                formatBody m.Message m.Important
                replies
            ])
        |> ul []
        |> fun messages ->
            let name = $"%s{s.Channel.Team.Name} / %s{s.Channel.Name}"
            div [] [
                h4 [] [str name]
                messages
            ]

    let formatChatSummary (s : ChatSummary) =
        formatChatMessagesSummary s.Messages
        |> fun messages ->
            div [] [
                h4 [] [str s.Chat.Name]
                messages
            ]

    let channels = summary.Channels |> List.map formatChannelSummary
    let chats = summary.Chats |> List.map formatChatSummary
    div [] (channels @ chats)

let htmlReport
    (fromDate : DateTime)
    (toDate : DateTime)
    (gitLabUserName : string)
    (teamsUserId : string)
    (mrs : GitLab.MergeRequest list)
    (conversations : Teams.AllConversations) =

    let days =
        Seq.initInfinite (float >> fromDate.AddDays)
        |> Seq.takeWhile (fun d -> d <= toDate)

    days
    |> Seq.map (fun day ->
        let mrSummaries = summarizeMrs day gitLabUserName mrs
        let conversationSummary = summarizeConversations day teamsUserId conversations
        div [] [
            h2 [] [day.ToString "ddd d (MMMM)" |> str]
            formatMrsSummaries mrSummaries
            formatConversationSummary conversationSummary
        ])
    |> Seq.toList
    |> div []
