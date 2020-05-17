module Program

open System
open System.IO
open System.Runtime.Serialization.Formatters.Binary
open System.Text.RegularExpressions
open FSharp.Data
open GitLab

type Config = JsonProvider<"ConfigSample.json">

type MergeRequest =
    { Proj : GitLab.Projects.Root
      MR : GitLab.MergeRequests.Root
      Discussions : list<GitLab.Discussions.Root>
      Emoticons : list<GitLab.Emoticons.Root>
      Changes: GitLab.Changes.Root
    }

let fetchDataFromGitLab config =
    let projs = GitLab.listProjectsOfCurrentUser config
    seq {
        for proj in projs do
            let mrs = GitLab.listMergeRequests config proj.Id
            for mr in mrs do
                let discussions = GitLab.listDiscussionsForMergeRequest config proj.Id mr.Iid
                let emoticons = GitLab.listEmoticonsForMergeRequest config proj.Id mr.Iid
                let changes = GitLab.listChangesForMergeRequest config proj.Id mr.Iid
                yield { Proj = proj
                        MR = mr
                        Discussions = discussions
                        Emoticons = emoticons
                        Changes = changes
                      }
    } |> Seq.toList

module DataFile =
    let gitLab dataFolder = Path.Combine(dataFolder, "GitLab.bin")                             

let downloadData (config : Config.Root) dataFolder =
    let gitLabConfig =
        { GitLab.ApiUrl = config.GitLab.ApiUrl
          GitLab.ApiToken = config.GitLab.ApiToken }

    printfn "Downloading data from GitLab"
    let mrs = fetchDataFromGitLab gitLabConfig

    use stream = File.OpenWrite <| DataFile.gitLab dataFolder
    let bf = BinaryFormatter()
    bf.Serialize(stream, mrs)
    printfn "Data from GitLab downloaded and saved"

let printSummary (config : Config.Root) dataFolder (fromDate : DateTime) (toDate : DateTime) =
    let userName = config.GitLab.UserName
    
    let bf = BinaryFormatter()
    use stream = File.OpenRead <| DataFile.gitLab dataFolder
    let mrs = bf.Deserialize(stream) :?> list<MergeRequest>

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
    let config = Config.Load argv.[1]
    let dataFolder = argv.[2]
    
    match command with
    | "download-data" -> downloadData config dataFolder
    | "print-summary" ->
        let fromDate = DateTime.Parse argv.[3]
        let toDate = DateTime.Parse argv.[4]
        printSummary config dataFolder fromDate toDate
    | _ -> failwithf "Unknown command %s" command

    0
