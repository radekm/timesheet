module GitLab

open FSharp.Data

type Projects = JsonProvider<"ApiResponseSamples/GitLabProjects.json">
type MergeRequests = JsonProvider<"ApiResponseSamples/GitLabMergeRequests.json">
type Discussions = JsonProvider<"ApiResponseSamples/GitLabMergeRequestDiscussions.json">
type Emoticons = JsonProvider<"ApiResponseSamples/GitLabMergeRequestEmoticons.json">
type Changes = JsonProvider<"ApiResponseSamples/GitLabMergeRequestChanges.json">

type Config = { ApiUrl: string; ApiToken: string }

let request (config : Config) (path : string) =
    let url = config.ApiUrl.TrimEnd('/') + "/v4/" + path.TrimStart('/')    
    
    let headers = [
        "Private-Token", config.ApiToken
        "Accept-Charset", "utf-8" ] 
    let resp =
        Http.Request(
            url = url,
            headers = headers,
            // GitLab does not send encoding back.
            responseEncodingOverride = "utf-8")
//    for h in resp.Headers do
//        printfn "%A" h
    match resp.Body with
    | Text str -> str
    | _ -> failwithf "Unexpected response %A" resp

let requestAllPagesParsed (config : Config) (path : string) (parseResp: string -> array<'T>) =
    let paramSeparator = if path.Contains '?' then "&" else "?"    
    let perPage = 100

    let result = ResizeArray()
    let rec fetchPage page =
        let path = path + paramSeparator + sprintf "per_page=%d&page=%d" perPage page
        let resp = request config path
        let parsed =
            try parseResp resp
            with e ->
                eprintfn "Parse of response failed: %s\n%s\n" e.Message resp
                raise e
        if parsed.Length > 0 then
            parsed |> Array.iter result.Add
            fetchPage (page + 1)

    fetchPage 1
    result |> Seq.toList    

let listProjectsOfCurrentUser config =
    let path = "projects?membership=true"
    let projects = requestAllPagesParsed config path Projects.Parse
    projects 

let listMergeRequests config projId =
    let path = sprintf "projects/%d/merge_requests?scope=all" projId
    let mrs = requestAllPagesParsed config path MergeRequests.Parse
    mrs

let listDiscussionsForMergeRequest config projId mrIid =
    let path = sprintf "projects/%d/merge_requests/%d/discussions" projId mrIid    
    let discussions = requestAllPagesParsed config path Discussions.Parse
    discussions

let listEmoticonsForMergeRequest config projId mrIid =
    let path = sprintf "projects/%d/merge_requests/%d/award_emoji" projId mrIid
    let emoticons = requestAllPagesParsed config path Emoticons.Parse
    emoticons

let listChangesForMergeRequest config projId mrIid =
    let path = sprintf "projects/%d/merge_requests/%d/changes" projId mrIid
    let resp = request config path
    let changes = Changes.Parse resp
    changes
