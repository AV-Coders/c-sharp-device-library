using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoRoomOsRecentCallsTest
{
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<StringListHandler> _mockStringListHandler = new ();
    private CiscoRoomOsRecentCalls _recentCallManager;

    public CiscoRoomOsRecentCallsTest()
    {
        _recentCallManager = new CiscoRoomOsRecentCalls(_mockClient.Object, 30);
        _recentCallManager.CallListUpdatedHandlers += _mockStringListHandler.Object;
    }

    [Fact]
    public void Module_RequestsHistory_WhenNotifiedOfAnUpdate()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*e CallHistory Updated");
        
        _mockClient.Verify(x => x.Send("xCommand CallHistory Get Limit:30\n"));
    }
    
    [Fact]
    public void ResponseHandler_ClearsWhenReceivingANewList()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r CallHistoryGetResult Entry 0 CallbackNumber: \"sip:+61355500193@local.domain\"");
        _mockClient.Object.ResponseHandlers!.Invoke("*r CallHistoryGetResult (status=OK):");
        
        Assert.Empty(_recentCallManager.RecentCalls);
    }
    
    [Fact]
    public void ResponseHandler_AddsItems()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r CallHistoryGetResult Entry 0 CallbackNumber: \"sip:+61355500193@local.domain\"");
        _mockClient.Object.ResponseHandlers!.Invoke("*r CallHistoryGetResult Entry 1 CallbackNumber: \"sip:*0491570156@local.domain\"");
        
        Assert.Equal(2, _recentCallManager.RecentCalls.Count);
        Assert.Equal("sip:+61355500193@local.domain", _recentCallManager.RecentCalls[0]);
        Assert.Equal("sip:*0491570156@local.domain", _recentCallManager.RecentCalls[1]);
    }

    [Fact]
    public void ResponseHandler_InvokesTheDelegate_WhenLastItemReceived()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r CallHistoryGetResult Entry 29 CallbackNumber: \"sip:*0491570156@local.domain\"");
        
        _mockStringListHandler.Verify(x => x.Invoke(It.IsAny<List<string>>()));
    }
}