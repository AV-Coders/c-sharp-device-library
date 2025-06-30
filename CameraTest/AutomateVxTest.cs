using System.Net;
using AVCoders.Core;

namespace AVCoders.Camera.Tests;

public class AutomateVxTest
{
    private readonly Mock<RestComms> _mockClient;
    private readonly AutomateVX _automateVx;
    
    public AutomateVxTest()
    {
        _mockClient = new("foo", (ushort)1, "Test");
        _automateVx = new AutomateVX(_mockClient.Object);
    }
    
    [Fact]
    public void ResponseHandler_HandlesAToken()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"OK\",\n    \"token\": \"abcdefghijklmonp\"\n}");
        
        _mockClient.Object.HttpResponseHandlers!.Invoke(response);
        
        _mockClient.Verify(x => x.AddDefaultHeader("Authorization", "abcdefghijklmonp"));
    }
    
    [Fact]
    public void ResponseHandler_TokenError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"Error\",\n    \"err\": \"Incorrect Username or Password\"\n}");
    }
    
    [Fact]
    public void ResponseHandler_ProcessesLayoutResponses()
    {
        Mock<LayoutsChangedHandler> mockHandler = new();
        _automateVx.LayoutsChangedHandlers = mockHandler.Object;
        
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"OK\",\n    \"message\": \"Layouts loaded successfully\",\n    \"layouts\": [\n        {\n            \"id\": \"A\",\n            \"name\": \"Full Screen\"\n        },\n        {\n            \"id\": \"B\",\n            \"name\": \"Dynamic Q&A\"\n        },\n        {\n            \"id\": \"C\",\n            \"name\": \"PiP\"\n        }\n    ]\n}");
        _mockClient.Object.HttpResponseHandlers!.Invoke(response);
        
        mockHandler.Verify(x => x.Invoke(It.Is<List<OneBeyondLayout>>(layouts => 
            layouts.Count == 3 && 
            layouts[0].Id == "A" && layouts[0].Name == "Full Screen" &&
            layouts[1].Id == "B" && layouts[1].Name == "Dynamic Q&A" &&
            layouts[2].Id == "C" && layouts[2].Name == "PiP"
        )));
    }
    
    [Fact]
    public void ResponseHandler_ProcessesScenarioResponses()
    {
        Mock<ScenariosChangedHandler> mockHandler = new();
        _automateVx.ScenariosChangedHandlers = mockHandler.Object;
        
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"OK\",\n    \"message\": \"Scenarios loaded successfully\",\n    \"scenarios\": [\n        {\n            \"id\": 1,\n            \"name\": \"Scenario 1\"\n        },\n        {\n            \"id\": 2,\n            \"name\": \"Scenario 2\"\n        }\n    ]\n}");
        _mockClient.Object.HttpResponseHandlers!.Invoke(response);
        
        mockHandler.Verify(x => x.Invoke(It.Is<List<OneBeyondScenario>>(scenarios => 
            scenarios.Count == 2 && 
            scenarios[0].Id == 1 && scenarios[0].Name == "Scenario 1" &&
            scenarios[1].Id == 2 && scenarios[1].Name == "Scenario 2"
        )));
    }

    [Fact]
    public void ResponseHandler_InvokesTheScenarioHandler()
    {
        Mock<IntHandler> mockhandler = new();
        _automateVx.ActiveScenarioChangedHandlers += mockhandler.Object;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"OK\",\n    \"message\": \"Successfully called scenario 1\",\n    \"cameras\": []\n}");
        _mockClient.Object.HttpResponseHandlers!.Invoke(response);
        
        mockhandler.Verify(x => x.Invoke(0));
        Assert.Equal(0, _automateVx.ActiveScenario);
    }

    [Fact]
    public void ResponseHandler_InvokesTheScenarioHandlerForADifferentIndex()
    {
        Mock<IntHandler> mockhandler = new();
        _automateVx.ActiveScenarioChangedHandlers += mockhandler.Object;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"OK\",\n    \"message\": \"Successfully called scenario 2\",\n    \"cameras\": []\n}");
        _mockClient.Object.HttpResponseHandlers!.Invoke(response);
        
        mockhandler.Verify(x => x.Invoke(1));
        Assert.Equal(1, _automateVx.ActiveScenario);
    }
    
    [Fact]
    public void ResponseHandler_InvokesTheLayoutHandler()
    {
        Mock<IntHandler> mockhandler = new();
        _automateVx.ActiveLayoutChangedHandlers += mockhandler.Object;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"OK\",\n    \"message\": \"Changed to Layout C\"\n}");
        _mockClient.Object.HttpResponseHandlers!.Invoke(response);
        
        mockhandler.Verify(x => x.Invoke(2));
        Assert.Equal(2, _automateVx.ActiveLayout);
    }
}