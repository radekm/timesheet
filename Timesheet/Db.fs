module Db

open System
open System.ComponentModel.DataAnnotations
open System.Text.Json
open System.Text.Json.Serialization

open EntityFrameworkCore.FSharp.Extensions
open Microsoft.EntityFrameworkCore

[<CLIMutable>]
type Channel = { [<Key>] Id : string
                 mutable Name : string
                 mutable TeamName : string
                 mutable Deleted : bool
                 mutable Json : string
                 // Last time when all messages from channel were downloaded and stored to db.
                 mutable LastDownload : DateTimeOffset
                 // Navigation properties:
                 Messages : ResizeArray<ChannelMessage>
               }
and [<CLIMutable>] ChannelMessage = { Id : string  // It seems that id is unique only in channel.
                                      ChannelId : string  // FK
                                      mutable Created : DateTimeOffset
                                      mutable Json : string
                                    }

[<CLIMutable>]
type Chat = { [<Key>] Id : string
              mutable Name : string  // Artificially created from list of members.
              mutable Json : string
              // Last time when all messages from channel were downloaded and stored to db.
              mutable LastDownload : DateTimeOffset
              // Navigation properties:
              Messages : ResizeArray<ChatMessage>
            }
and [<CLIMutable>] ChatMessage = { Id : string  // It seems that id is unique only in channel.
                                   ChatId : string  // FK
                                   mutable Created : DateTimeOffset
                                   mutable Json : string
                                 }

let private jsonOptions =
    let options = JsonSerializerOptions()
    options.Converters.Add(JsonFSharpConverter())
    options

let convertToJson (a : 'T) : string = JsonSerializer.Serialize(a, jsonOptions)

let convertFromJson (json : string) : 'T = JsonSerializer.Deserialize<'T>(json, jsonOptions)

type TimesheetDbContext() =
    inherit DbContext()

    [<DefaultValue>] val mutable channels : DbSet<Channel>
    member me.Channels with get() = me.channels and set v = me.channels <- v

    [<DefaultValue>] val mutable channelMessages : DbSet<ChannelMessage>
    member me.ChannelMessages with get() = me.channelMessages and set v = me.channelMessages <- v

    [<DefaultValue>] val mutable chats : DbSet<Chat>
    member me.Chats with get() = me.chats and set v = me.chats <- v

    [<DefaultValue>] val mutable chatMessages : DbSet<ChatMessage>
    member me.ChatMessages with get() = me.chatMessages and set v = me.chatMessages <- v

    override _.OnModelCreating builder =
        builder.RegisterOptionTypes()

        let entity = builder.Entity<ChannelMessage>()
        entity
            .HasKey(fun e -> (e.ChannelId, e.Id) :> obj)
        |> ignore

        let entity = builder.Entity<ChatMessage>()
        entity
            .HasKey(fun e -> (e.ChatId, e.Id) :> obj)
        |> ignore

    override _.OnConfiguring(options: DbContextOptionsBuilder) : unit =
        options.UseSqlite("Data Source=Timesheet.db") |> ignore

let queryChannelMessages (ctx : TimesheetDbContext) (ch : Channel) =
    query { for m in ctx.ChannelMessages do
            where (m.ChannelId = ch.Id)
            select m }

let queryChatMessages (ctx : TimesheetDbContext) (ch : Chat) =
    query { for m in ctx.ChatMessages do
            where (m.ChatId = ch.Id)
            select m }
