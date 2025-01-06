namespace AVCoders.Core;

public delegate void PowerStateHandler(PowerState state);

public delegate void CommunicationStateHandler(CommunicationState communicationState);

public delegate void ConnectionStateHandler(ConnectionState connectionState);

public delegate void LogHandler(string message, EventLevel level = EventLevel.Verbose);

public delegate void VolumeLevelHandler(int volumeLevel);

public delegate void MuteStateHandler(MuteState state);

public delegate void StringHandler(string response);

public delegate void ByteHandler(byte[] response);

public delegate void HttpResponseHandler(HttpResponseMessage response);