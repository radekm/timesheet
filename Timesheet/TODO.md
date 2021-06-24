# TODO

- Replace `FsPickle` format by SQLite with JSON.
- Properly count number of commits. Or do we want to count
  number of pushes?
- It seems that messages from MS Graph API are in reverse order.
  Is it always the case or do we accidentally reverse it somewhere in the code?
  Now it's fixed by sorting in `summarizeChatMessages` and `summarizeChannel`.
  Do we need that sorting or is there another fix?
- Download and store commit messages and show them in report. 
- Functions for saving data to SQLite to different tables
  seem similar to each other. Can we replace them
  by one more general piece of code?
- Use navigational properties to fetch messages for channels and chats.
- Update README.
