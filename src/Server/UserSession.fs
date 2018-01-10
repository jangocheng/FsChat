module UserSession

open Akkling
open Akka.Streams

open ChatUser
open ChannelFlow
open ChatServer

type ClientSession = NoSession | UserLoggedOn of RegisteredUser

type SessionData = {
    server: IActorRef<ServerControlMessage>
    myinfo: RegisteredUser
    channels: Map<ChannelId, UniqueKillSwitch>
}

// creates a new session
let make server me : SessionData =
    { server = server; myinfo = me; channels = Map.empty }

// joins channel
let join listenChannel (channelId: ChannelId) (session: SessionData) =
    // TODO consider extracting connecting logic such as the following method
    // let connect chanId chanActor =
    //     listen chan.id (createChannelFlow chan.channelActor me)
    let byChanId cid c = (c:ChannelData).id = cid

    async {
        if session.channels |> Map.containsKey channelId then
            return Error "User already joined channel"
        else
            let! channel = session.server |> getChannel (byChanId channelId)
            match channel, listenChannel with
            | Ok chan, Some listen ->
                let userId = getUserId session.myinfo
                let ks = listen chan.id (createChannelFlow chan.channelActor userId)
                let newState = {session with channels = session.channels |> Map.add chan.id ks}
                return Ok (newState, chan)
            | Error err, _ ->
                return "getChannel failed: " + err |> Error
            | _, None ->
                return "listenChannel is not set" |> Error
    }

// leaves channel
let leave chanId (session: SessionData) =
    match session.channels |> Map.tryFind chanId with
    | None ->
        Error "User is not joined channel"
    | Some killswitch ->
        do killswitch.Shutdown()
        Ok ({ session with channels = session.channels |> Map.remove chanId }, ())

// leaves all channels
let leaveAll (session: SessionData) =
    do session.channels |> Map.iter (fun _ killswitch -> killswitch.Shutdown())
    Ok ({ session with channels = Map.empty }, ())