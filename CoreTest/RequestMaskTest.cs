using System.Text;

namespace AVCoders.Core.Tests;

public class RequestMaskTest
{
    private const string Secret = "\"Password\":\"s3cret-pw\"";
    private const string Shown = "\"Password\":\"***\"";
    private const string Logon = """{"jsonrpc":"2.0","id":1,"method":"Logon","params":{"User":"tech","Password":"s3cret-pw"}}""";
    private const string ShownLogon = """{"jsonrpc":"2.0","id":1,"method":"Logon","params":{"User":"tech","Password":"***"}}""";

    private class TestCommunicationClient() : CommunicationClient("Test", "10.0.0.1", 1710, CommandStringFormat.Ascii)
    {
        public override void Send(string message)
        {
            InvokeRequestHandlers(Bytes.FromString(message));
            InvokeRequestHandlers(message);
        }

        public override void Send(byte[] bytes) => InvokeRequestHandlers(bytes);
    }

    private readonly TestCommunicationClient _client = new();
    private readonly List<string> _requests = [];
    private readonly List<byte[]> _requestBytes = [];

    public RequestMaskTest()
    {
        _client.RequestHandlers += _requests.Add;
        _client.RequestByteHandlers += _requestBytes.Add;
    }

    [Fact]
    public void ARequestCarryingAMaskedText_ReachesTheStringAndByteHandlersWithTheMaskInItsPlace()
    {
        _client.MaskInRequests(Secret, Shown);

        _client.Send(Logon);

        Assert.Equal([ShownLogon], _requests);
        Assert.Equal(Encoding.Latin1.GetBytes(ShownLogon), Assert.Single(_requestBytes));
    }

    [Fact]
    public void ARequestWithoutTheMaskedText_ReachesTheHandlersUnchanged()
    {
        _client.MaskInRequests(Secret, Shown);

        _client.Send("""{"jsonrpc":"2.0","id":2,"method":"StatusGet","params":0}""");

        Assert.Equal(["""{"jsonrpc":"2.0","id":2,"method":"StatusGet","params":0}"""], _requests);
    }

    [Fact]
    public void AnEmptyText_MasksNothing()
    {
        _client.MaskInRequests("", Shown);

        _client.Send(Logon);

        Assert.Equal([Logon], _requests);
    }
}
