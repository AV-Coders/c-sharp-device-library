namespace AVCoders.Core;

public delegate void ByteHandler(byte[] response);

public delegate void CommunicationStateHandler(CommunicationState communicationState);

public delegate void ConnectionStateHandler(ConnectionState connectionState);

public delegate void HttpResponseHandler(HttpResponseMessage response);

public delegate void LogHandler(string message, EventLevel level = EventLevel.Verbose);

public delegate void MuteStateHandler(MuteState state);

public delegate void PowerStateHandler(PowerState state);

public delegate void MediaStateHandler(MediaState state);

public delegate void TransportStateHandler(TransportState state);

public delegate void StringHandler(string value);

public delegate void StringListHandler(List<string> list);

public delegate void VolumeLevelHandler(int volumeLevel);