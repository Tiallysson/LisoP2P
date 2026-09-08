namespace LisoP2P.Core.Protocol;

public enum MessageType : byte
{
    Announce = 1,
    Goodbye = 2,

    Hello = 10,
    HelloAck = 11,
    Ping = 12,
    Pong = 13,
    NicknameUpdate = 14,

    ChatMessage = 20,
    ChatAck = 21,

    Disconnect = 30,

    ScreenShareStart = 40,
    ScreenShareStop = 41,
    KeyframeRequest = 42,

    VoiceStart = 50,
    VoiceStop = 51,

    RoomInvite = 60,
    RoomJoin = 61,
    RoomMemberList = 62,
    RoomLeave = 63,
    RoomSpeaking = 64,
}
