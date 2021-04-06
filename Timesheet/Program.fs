module Program

open System
open System.IO
open System.Runtime.Serialization.Formatters.Binary
open System.Text.RegularExpressions
open FSharp.Data

type Config = JsonProvider<"ConfigSample.json">

module DataFile =
    let gitLab dataFolder =
        Path.Combine(dataFolder, "GitLab.bin")
        |> Path.GetFullPath                             

    let teams dataFolder =
        Path.Combine(dataFolder, "Teams.bin")
        |> Path.GetFullPath

    let read dataFile : obj = 
        let bf = BinaryFormatter()
        use stream = File.OpenRead dataFile
        bf.Deserialize stream

    let write dataFile (graph : obj) =
        use stream = File.OpenWrite dataFile
        let bf = BinaryFormatter()
        bf.Serialize(stream, graph)

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

let printSummary (config : Config.Root) dataFolder (fromDate : DateTime) (toDate : DateTime) =
    let userName = config.GitLab.UserName
    
    let mrs = (DataFile.gitLab dataFolder |> DataFile.read) :?> list<GitLab.MergeRequest>

    let dates =
        Seq.initInfinite (float >> fromDate.AddDays)
        |> Seq.takeWhile (fun d -> d <= toDate)
    for d in dates do
        printfn "\n\n######## Activity %A" d
        
        for mr in mrs do
            let created = mr.MR.CreatedAt.Date = d && mr.MR.Author.Username = userName
            let emoticonAdded =
                mr.Emoticons
                |> List.exists (fun e -> e.CreatedAt.Date = d && e.User.Username = userName)
            let notes =
                mr.Discussions
                |> Seq.map (fun d -> d.Notes)
                |> Seq.concat
                |> Seq.filter (fun note -> note.CreatedAt.Date = d)
                |> Seq.filter (fun note -> note.Author.Username = userName)
                |> Seq.toList

            let comments =
                notes
                |> Seq.filter (fun note -> not note.System)
            let numComments = Seq.length comments
            
            let addedCommitsRegex = Regex("^added \\d+ commit")    
            let addedCommits =
                notes
                |> Seq.filter (fun note -> note.System)
                |> Seq.filter (fun note -> addedCommitsRegex.IsMatch note.Body)
                |> Seq.length
                
            if created || emoticonAdded || numComments > 0 || addedCommits > 0 then
                printfn "## Merge request %d (%s)" mr.MR.Iid mr.MR.Title
                mr.Changes.Changes
                |> Array.filter (fun ch -> ch.RenamedFile)
                |> Array.length
                |> (fun cnt -> if cnt > 0 then printfn "Renamed files %d" cnt) 

                mr.Changes.Changes
                |> Array.filter (fun ch -> ch.DeletedFile)
                |> Array.length
                |> (fun cnt -> if cnt > 0 then  printfn "Deleted files %d" cnt) 

                mr.Changes.Changes
                |> Array.filter (fun ch -> ch.NewFile)
                |> Array.fold (fun (newFiles, lines, chars) ch ->
                    let lines' = ch.Diff |> String.filter ((=) '\n') |> String.length
                    let chars' = ch.Diff |> String.length
                    newFiles + 1, lines + lines', chars + chars') (0, 0, 0)
                |>  fun (newFiles, lines, chars) ->
                    if newFiles > 0 then
                        printfn "New files %d (lines %d, chars %d)" newFiles lines chars

                mr.Changes.Changes
                |> Array.filter (fun ch -> (ch.NewFile || ch.DeletedFile || ch.RenamedFile) |> not)
                |> Array.fold (fun (changedFiles, lines, chars) ch ->
                    let lines' = ch.Diff |> String.filter ((=) '\n') |> String.length
                    let chars' = ch.Diff |> String.length
                    changedFiles + 1, lines + lines', chars + chars') (0, 0, 0)
                |>  fun (changedFiles, lines, chars) ->
                    if changedFiles > 0 then
                        printfn "Changed files %d (lines %d, chars %d)" changedFiles lines chars

            if created then
                printfn "-- Created"
            if emoticonAdded then
                printfn "-- Added emoticon"
            if numComments > 0 then
                let lengthOfComments = comments |> Seq.sumBy (fun c -> c.Body.Length)
                printfn "-- Added comment %d (chars %d)" numComments lengthOfComments
            if addedCommits > 0 then
                printfn "-- Added commits %d" addedCommits        

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
    | "print-summary" ->
        let fromDate = DateTime.Parse argv.[3]
        let toDate = DateTime.Parse argv.[4]
        printSummary config dataFolder fromDate toDate
    | _ -> failwithf "Unknown command %s" command

    0
