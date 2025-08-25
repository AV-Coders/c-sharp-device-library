namespace AVCoders.Display;

public record InputInfo(string Name, Input Input);

public record DisplayInfo(Display Display, InputInfo[] Inputs, int MaxVolume = 100);