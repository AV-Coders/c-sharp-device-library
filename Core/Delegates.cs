namespace AVCoders.Core;

public delegate void PowerStateHandler(PowerState state);

public delegate void CommunicationStateHandler(CommunicationState communicationState);

public delegate void ConnectionStateHandler(ConnectionState connectionState);

public delegate void LogHandler(string response, EventLevel level = EventLevel.Verbose);

public delegate void VolumeLevelHandler(int volumeLevel);

public delegate void MuteStateHandler(MuteState state);

public delegate void ResponseHandler(string response);

public delegate void ResponseByteHandler(byte[] response);