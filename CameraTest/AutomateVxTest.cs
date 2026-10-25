using System.Net;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.Camera.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class AutomateVxTest
{
    private const string TokenReply = "{\"status\": \"OK\", \"token\": \"abc\"}";
    private const string UnauthorizedReply = "{\"status\": \"Error\", \"err\": \"Unauthorized\"}";
    private readonly Mock<RestComms> _mockClient;
    private readonly AutomateVX _automateVx;

    public AutomateVxTest()
    {
        _mockClient = new("foo", (ushort)1, "Test");
        _automateVx = new AutomateVX(_mockClient.Object);
    }

    private static HttpResponseMessage Reply(string path, string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
            RequestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri("https://foo:1" + path))
        };

    private void Respond(string path, string body) => _mockClient.Object.HttpResponseHandlers!.Invoke(Reply(path, body));

    private void VerifyPosts(string path, Times times) =>
        _mockClient.Verify(x => x.Post(It.Is<Uri>(uri => uri.ToString() == path), It.IsAny<string>(), "application/json"), times);

    private static (Mock<RestComms> Client, AutomateVX Camera, ScopeCapturingLoggerFactory Logs) CreateWithCapturedLogs()
    {
        Mock<RestComms> client = new("foo", (ushort)1, "Test");
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (client, new AutomateVX(client.Object), logs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
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

    [Fact]
    public void SetScenarioById_PostsTheIdToGoToScenario()
    {
        _automateVx.SetScenarioById(7);

        _mockClient.Verify(x => x.Post(
            It.Is<Uri>(uri => uri.ToString() == "/api/GoToScenario"),
            "{\"id\": \"7\"}",
            "application/json"));
    }

    [Fact]
    public void ResponseHandler_UpdatesTheCommunicationState()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"OK\",\n    \"message\": \"Changed to Layout C\"\n}");

        _mockClient.Object.HttpResponseHandlers!.Invoke(response);

        Assert.Equal(CommunicationState.Okay, _automateVx.CommunicationState);
    }

    [Fact]
    public void ResponseHandler_GivenAFailureStatusCode_ReportsAnError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        response.Content = new StringContent("Server error");

        _mockClient.Object.HttpResponseHandlers!.Invoke(response);

        Assert.Equal(CommunicationState.Error, _automateVx.CommunicationState);
    }

    [Fact]
    public void ResponseHandler_GivenAnErrorStatus_ReportsAnError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("{\n    \"status\": \"Error\",\n    \"err\": \"Bad token\"\n}");

        _mockClient.Object.HttpResponseHandlers!.Invoke(response);

        Assert.Equal(CommunicationState.Error, _automateVx.CommunicationState);
    }

    [Fact]
    public void Constructor_TokenReplyBeforeThePostReturns_FetchesTheLayoutsAndScenarios()
    {
        Mock<RestComms> client = new("foo", (ushort)1, "Test");
        client.Setup(x => x.Post(It.Is<Uri>(uri => uri.ToString() == "/get-token"), "", "application/json"))
            .Callback(() => client.Object.HttpResponseHandlers!.Invoke(Reply("/get-token", TokenReply)))
            .Returns(Task.CompletedTask);

        _ = new AutomateVX(client.Object);

        client.Verify(x => x.Post(It.Is<Uri>(uri => uri.ToString() == "/api/GetLayouts"), "", "application/json"), Times.Once);
        client.Verify(x => x.Post(It.Is<Uri>(uri => uri.ToString() == "/api/GetScenarios"), "", "application/json"), Times.Once);
    }

    [Fact]
    public void ResponseHandler_UnauthorizedReply_RetriesTheFailedActionAfterTheToken()
    {
        Respond("/get-token", TokenReply);
        _automateVx.StartAutoSwitch();
        _automateVx.StopAutoSwitch();

        Respond("/api/StartAutoSwitch", UnauthorizedReply);
        Respond("/get-token", TokenReply);

        VerifyPosts("/get-token", Times.Exactly(2));
        VerifyPosts("/api/StartAutoSwitch", Times.Exactly(2));
        VerifyPosts("/api/StopAutoSwitch", Times.Once());
    }

    [Fact]
    public void ResponseHandler_UnauthorizedReplyToGetScenarios_RetriesGetScenarios()
    {
        Respond("/get-token", TokenReply);

        Respond("/api/GetScenarios", UnauthorizedReply);
        Respond("/get-token", TokenReply);

        VerifyPosts("/api/GetScenarios", Times.Exactly(2));
        VerifyPosts("/api/GetLayouts", Times.Once());
    }

    [Fact]
    public void ResponseHandler_UnauthorizedReplyMatchingNoRequest_LogsItAndRetriesNothing()
    {
        var (client, _, logs) = CreateWithCapturedLogs();
        client.Object.HttpResponseHandlers!.Invoke(Reply("/get-token", TokenReply));

        client.Object.HttpResponseHandlers!.Invoke(Reply("/api/Unknown", UnauthorizedReply));
        client.Object.HttpResponseHandlers!.Invoke(Reply("/get-token", TokenReply));

        Assert.Contains(logs.Logs,
            log => log.Message == "The unauthorized reply to /api/Unknown matches no request, so nothing is retried");
        client.Verify(x => x.Post(It.Is<Uri>(uri => uri.ToString() == "/get-token"), "", "application/json"),
            Times.Exactly(2));
        client.Verify(x => x.Post(It.Is<Uri>(uri => uri.ToString() == "/api/GetLayouts"), "", "application/json"),
            Times.Once);
        client.Verify(x => x.Post(It.Is<Uri>(uri => uri.ToString() == "/api/GetScenarios"), "", "application/json"),
            Times.Once);
    }

    [Fact]
    public void SetLayout_BeforeTheLayoutsLoad_PostsNothingAndAddsAnErrorEvent()
    {
        _automateVx.SetLayout(0);

        VerifyPosts("/api/ChangeLayout", Times.Never());
        Assert.Contains(_automateVx.Events,
            e => e.Type == EventType.Error && e.Info == "Layout 0 is out of range, 0 layouts are loaded");
    }

    [Fact]
    public void SetLayout_AfterTheLayoutsLoad_PostsTheLayoutId()
    {
        Respond("/api/GetLayouts",
            "{\"status\": \"OK\", \"layouts\": [{\"id\": \"A\", \"name\": \"Full Screen\"}, {\"id\": \"B\", \"name\": \"PiP\"}]}");

        _automateVx.SetLayout(1);

        _mockClient.Verify(x => x.Post(It.Is<Uri>(uri => uri.ToString() == "/api/ChangeLayout"), "{\"id\": \"B\"}",
            "application/json"), Times.Once);
    }

    [Fact]
    public void SetScenario_BeforeTheScenariosLoad_PostsNothingAndAddsAnErrorEvent()
    {
        _automateVx.SetScenario(0);

        VerifyPosts("/api/GoToScenario", Times.Never());
        Assert.Contains(_automateVx.Events,
            e => e.Type == EventType.Error && e.Info == "Scenario 0 is out of range, 0 scenarios are loaded");
    }

    [Fact]
    public void SetScenario_LogsTheScenarioName()
    {
        var (client, camera, logs) = CreateWithCapturedLogs();
        client.Object.HttpResponseHandlers!.Invoke(Reply("/api/GetScenarios",
            "{\"status\": \"OK\", \"scenarios\": [{\"id\": 1, \"name\": \"Lectern\"}]}"));

        camera.SetScenario(0);

        Assert.Contains(logs.Logs, log => log.Message == "Setting Scenario to Lectern");
    }

    [Fact]
    public void ResponseHandler_NonStringStatus_LogsTheContentAsUnparseable()
    {
        var (client, camera, logs) = CreateWithCapturedLogs();

        client.Object.HttpResponseHandlers!.Invoke(Reply("/api/GetLayouts", "{\"status\": 5}"));

        Assert.Contains(logs.Logs, log => log.Message == "Failed to parse 1Beyond response: {\"status\": 5}");
        Assert.Empty(camera.Errors);
    }
}