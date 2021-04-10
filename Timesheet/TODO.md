# TODO

- Replace `FsPickle` format by JSON. So the stored data
  can be easily inspected and loaded after small schema changes.
  - Teams representation will probably work just fine with `System.Text.Json`
    with `FSharp.SystemTextJson`.
  - On the other hand classes provided by `FSharp.Data`
    used by GitLab representation  will probably not work.
    One option is to create custom converter for `JsonValue` from `FSharp.Data`.
    Other option is replace provided classes by custom records
    (ie. provided classes will be used only for deserializing responses from GitLab).
- Properly count number of commits. Or do we want to count
  number of pushes?
