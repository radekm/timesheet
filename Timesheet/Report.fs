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

let summarizeConversations
    (day : DateTime)
    (userId : string)
    (conversations : Teams.AllConversations) =

    let testMessage = testMessage day userId

    let filterMessageWithReplies (m : Teams.MessageWithReplies) : Teams.MessageWithReplies option =
        let replies = m.Replies |> List.filter testMessage
        // Retain message if it passes test or if some reply passes test.
        if testMessage m.Message || not replies.IsEmpty
        then Some { m with Replies = replies  }
        else None
    let filterChannel (ch : Teams.ChannelWithMessages) : Teams.ChannelWithMessages option =
        let messages = ch.Messages |> List.choose filterMessageWithReplies
        // Retain channel if it contains messages which passed test.
        if not messages.IsEmpty
        then Some { ch with Messages = messages }
        else None
    let filterChat (ch : Teams.ChatWithMessages) : Teams.ChatWithMessages option =
        let messages = ch.Messages |> List.choose filterMessageWithReplies
        // Retain chat if it contains messages which passed test.
        if not messages.IsEmpty
        then Some { ch with Messages = messages }
        else None

    { Teams.Channels = conversations.Channels |> List.choose filterChannel
      Teams.Chats = conversations.Chats |> List.choose filterChat
    }

// TODO Maybe only `summarizeConversations` should use `day` and `userId`. 
let formatConversationSummary
    (day : DateTime)
    (userId : string)
    (summary : Teams.AllConversations) =

    let testMessage = testMessage day userId
    let formatBody (msg : Teams.Message) =
        let style =
            if testMessage msg
            then "color: #000"
            else "color: #999"
        match msg.Body with
        | Teams.MessageContent.Text s -> s
        | Teams.MessageContent.Html s ->
            HtmlNode.Parse $"<div>%s{s}</div>"
            |> List.head
            |> HtmlNode.innerText
        |> fun s ->
            if s.Length > 300
            then s.Substring(0, 300) + "…"
            else s
        |> str
        |> fun node -> span [_style style] [node]

    let formatConversationSummary (name : string) (messages : Teams.MessageWithReplies list) =
        messages
        |> List.map (fun m ->
            let replies =
                match m.Replies with
                | [] -> span [] []
                | _ ->
                    m.Replies
                    |>  List.map (fun r -> li [] [formatBody r])
                    |> ul []
            li [] [
                formatBody m.Message
                replies
            ])
        |> ul []
        |> fun messages ->
            div [] [
                h4 [] [str name]
                messages
            ]

    let channels =
        summary.Channels
        |> List.map (fun ch ->
            let name = $"%s{ch.Channel.Team.Name} / %s{ch.Channel.Name}"
            formatConversationSummary name ch.Messages)
    let chats =
        summary.Chats
        |> List.map (fun ch ->
            let topic = ch.Chat.Topic |> Option.map (sprintf " (%s)") |> Option.defaultValue ""
            let name =
                ch.Chat.Members
                |> List.map (fun m -> m.Name)
                |> String.concat ", "
                |> fun members -> sprintf $"%s{members}%s{topic}"
            formatConversationSummary name ch.Messages)
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
            formatConversationSummary day teamsUserId conversationSummary
        ])
    |> Seq.toList
    |> div []
