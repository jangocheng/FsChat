module RestApi
// implements chat API endpoints for Suave

open System
open Akka.Actor
open Akkling
open Akkling.Streams
open Akka.Streams
open Akka.Streams.Dsl

open Suave
open Suave.Logging
open Suave.WebSocket

open ChannelFlow
open ChatServer
open SocketFlow

open FsChat

let private logger = Log.create "chatapi"        

module private Implementation =
    let inline (|OtherwiseFail|) _ = failwith "no choice"

    type ServerActor = IActorRef<ChatServer.ServerControlMessage>

    type IncomingMessage =
        | ChannelMessage of int * Message
        | ControlMessage of Protocol.ServerMsg
        | Trash of reason: string

    let mapUserInfoToProtocol (u: UserInfo) isbot: Protocol.ChanUserInfo =
        let (UserId uid) = u.id in
        {id = uid; nick = u.nick; isbot = isbot; status = u.status; email = u.email |> Option.defaultValue null; imageUrl = u.imageUrl |> Option.defaultValue null}

    let mapUserToProtocol :(ChatUser -> Protocol.ChanUserInfo) =
        function
        | User u -> mapUserInfoToProtocol u false
        | Bot b ->  mapUserInfoToProtocol b true
        | System uid -> mapUserInfoToProtocol {UserInfo.Empty with id = uid; nick = "system"} true
       
    let encodeChannelMessage (getUser: UserId -> ChatUser option Async) channelId : ClientMessage<UserId, Message> -> Protocol.ClientMsg Async =
        let returnUserEvent f userid = async {
            let! userResult = getUser userid
            return f <|
                match userResult with
                | Some user -> mapUserToProtocol user
                | _ -> mapUserInfoToProtocol {UserInfo.Empty with id = UserId "-1"; nick = "unknown"} true
        }
        function
        | ChatMessage ((id, ts), UserId authorId, Message message) ->
            async.Return <| Protocol.ChanMsg {id = id; ts = ts; text = message; chan = channelId; author = authorId}
        | Joined ((id, ts), userid, _) ->
            returnUserEvent (fun user -> Protocol.UserJoined  ({id = id; ts = ts; user = user}, channelId)) userid
        | Left ((id, ts), userid, _) ->
            returnUserEvent (fun user -> Protocol.UserLeft ({id = id; ts = ts; user = user}, channelId)) userid

    let (|ParseChannelId|_|) s = 
        let (result, value) = Int32.TryParse s
        if result then Some value else None

    // extracts message from websocket reply, only handles User input (channel * string)
    let extractMessage message =
        try
            match message with
            | Text t ->
                match t |> Json.unjson<Protocol.ServerMsg> with
                | Protocol.UserMessage {chan = channelId; text = messageText} ->
                    match channelId with
                    | ParseChannelId chanId -> ChannelMessage (chanId, Message messageText)
                    | _ -> Trash "Bad channel id"
                | message -> ControlMessage message                
            | x -> Trash <| sprintf "Not a Text message '%A'" x
        with e ->
            do logger.error (Message.eventX "Failed to parse message '{msg}': {e}" >> Message.setFieldValue "msg" message  >> Message.setFieldValue "e" e)
            Trash "exception"
    let isChannelMessage = function |ChannelMessage _ -> true | _ -> false
    let isControlMessage = function |ControlMessage _ -> true | _ -> false

    let extractChannelMessage (ChannelMessage (chan, message) | OtherwiseFail (chan, message)) = chan, message
    let extractControlMessage (ControlMessage message | OtherwiseFail message) = message

    let mapChanInfo (data: ChannelData) : Protocol.ChannelInfo =
        {id = data.id.ToString(); name = data.name; topic = data.topic; userCount = 0; users = []; joined = false}

    let setJoined v (ch: Protocol.ChannelInfo) =
        {ch with joined = v}

    let replyErrorProtocol requestId errtext =
        Protocol.CannotProcess (requestId, errtext) |> Protocol.ClientMsg.Error

    let reply requestId = function
        | Ok response ->    response
        | Result.Error e -> replyErrorProtocol requestId e

open Implementation
open UserSession

let connectWebSocket ({server = server; me = me; actorSystem = actorSystem }) : WebPart =
    fun ctx -> async {
        let materializer = actorSystem.Materializer()

        // session data
        let mutable session = UserSession.make server me
        let mutable listenChannel = None

        let updateSession requestId f = function
            | Ok (newSession, response) -> session <- newSession; f response
            | Result.Error e ->            replyErrorProtocol requestId e

        let makeChannelInfoResult v =
            async {
                match v with
                | Ok (arg1, channel: ChannelData) ->
                    let! (userIds: UserId list) = channel.channelActor <? ListUsers
                    let! users = server |> getUsersInfo userIds
                    let chaninfo = { mapChanInfo channel with users = users |> List.map mapUserToProtocol}
                    return Ok (arg1, chaninfo)
                | Result.Error e -> return Result.Error e
            }

        let processControlMessage message =
            async {
                let requestId = "" // TODO take from server message
                match message with

                | Protocol.ServerMsg.Greets ->
                    let! serverChannels = server |> (listChannels (fun _ -> true))

                    let makeChanInfo chanData =
                        { mapChanInfo chanData with joined = session.channels |> Map.containsKey chanData.id}

                    let makeHello channels =
                        Protocol.ClientMsg.Hello {nick = me.nick; name = ""; email = None; channels = channels}

                    return serverChannels |> Result.map (List.map makeChanInfo >> makeHello) |> reply ""

                | Protocol.ServerMsg.Join chanIdStr ->
                    match chanIdStr with
                    | ParseChannelId channelId ->
                        let! result = session |> UserSession.join listenChannel channelId
                        let! chaninfo = makeChannelInfoResult result
                        return chaninfo |> updateSession requestId (setJoined true >> Protocol.JoinedChannel)
                    | _ -> return replyErrorProtocol requestId "bad channel id"

                | Protocol.ServerMsg.JoinOrCreate channelName ->
                    let! channelResult = server |> getOrCreateChannel channelName
                    match channelResult with
                    | Ok channelData ->
                        let! result = session |> UserSession.join listenChannel channelData.id
                        let! chaninfo = makeChannelInfoResult result
                        return chaninfo |> updateSession requestId (setJoined true >> Protocol.JoinedChannel)
                    | Result.Error err ->
                        return replyErrorProtocol requestId err

                | Protocol.ServerMsg.Leave chanIdStr ->
                    return chanIdStr |> function
                        | ParseChannelId channelId ->
                            let result = session |> UserSession.leave channelId
                            result |> updateSession requestId (fun _ -> Protocol.LeftChannel chanIdStr)
                        | _ ->
                            replyErrorProtocol requestId "bad channel id"

                | _ ->
                    return replyErrorProtocol requestId "event was not processed"
            }

        let sessionFlow = createUserSessionFlow<UserId,Message,int> materializer
        let controlMessageFlow = Flow.empty<_, Akka.NotUsed> |> Flow.asyncMap 1 processControlMessage

        let serverEventsSource: Source<Protocol.ClientMsg, Akka.NotUsed> =
            let notifyNew sub = subscribeNotify server (User me) sub; Akka.NotUsed.Instance
            let source = Source.actorRef OverflowStrategy.Fail 1 |> Source.mapMaterializedValue notifyNew

            source |> Source.map (function
                | AddChannel ch -> ch |> (mapChanInfo >> Protocol.ClientMsg.NewChannel)
                | DropChannel ch -> ch |> (mapChanInfo >> Protocol.ClientMsg.RemoveChannel)
            )
        let getUser i = ChatServer.getUser i server

        let userMessageFlow =
            Flow.empty<IncomingMessage, Akka.NotUsed>
            |> Flow.filter isChannelMessage
            |> Flow.map extractChannelMessage
            // |> Flow.log "User flow"
            |> Flow.viaMat sessionFlow Keep.right
            |> Flow.asyncMap 1 (fun (channel: int, message) -> encodeChannelMessage getUser (channel.ToString()) message)

        let controlFlow =
            Flow.empty<IncomingMessage, Akka.NotUsed>
            |> Flow.filter isControlMessage
            |> Flow.map extractControlMessage
            // |> Flow.log "Control flow"
            |> Flow.via controlMessageFlow
            |> Flow.mergeMat serverEventsSource Keep.left

        let combinedFlow : Flow<IncomingMessage,Protocol.ClientMsg,_> =
            FlowImpl.split2 userMessageFlow controlFlow Keep.left

        let socketFlow =
            Flow.empty<WsMessage, Akka.NotUsed>
            |> Flow.map extractMessage
            // |> Flow.log "Extracting message"
            |> Flow.viaMat combinedFlow Keep.right
            |> Flow.map (Json.json >> Text)

        let materialize materializer (source: Source<WsMessage, Akka.NotUsed>) (sink: Sink<WsMessage, _>) =
            listenChannel <-
                source
                |> Source.viaMat socketFlow Keep.right
                |> Source.toMat sink Keep.left
                |> Graph.run materializer |> Some
            ()

        return! handShake (handleWebsocketMessages actorSystem materialize) ctx
    }
