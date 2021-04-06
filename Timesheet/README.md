This program can summarize what a user was doing on GitLab.
It can do one of two things:

- download info about MRs on GitLab and store it into a file
- or read the file with MRs and print a summary for the given dates.

# Configuration

Create `Config.json` file which is used to communicate
with GitLab API and also contains `UserName` of a GitLab user
whose activities we want to summarize.

```json
{
  "GitLab": {
    "ApiUrl": "https://gitlab.com/api",
    "ApiToken": "token",
    "UserName": "user"
  },
  "Teams": {
    "AppId": "id"
  }
}
```

# Downloading data

To download data call:

```
Timesheet.dll download-data-gitlab Config.json .
Timesheet.dll download-data-teams Config.json .
```

# Printing summary of activities

To print a summary of activities of a user from the config:

```
Timesheet.dll  print-summary Config.json . 2020-2-24 2020-5-1
```
