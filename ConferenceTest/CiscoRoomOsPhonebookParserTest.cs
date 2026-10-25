using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Conference.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class CiscoRoomOsPhonebookParserTest
{
    private const string ResultHeader = "*r PhonebookSearchResult (status=OK):";
    private const string ErrorHeader = "*r PhonebookSearchResult (status=Error):";
    private const string ErrorReason = "*r PhonebookSearchResult Reason: \"No phonebook server\"";
    private const string RootSearch = "xCommand Phonebook Search PhonebookType: Corporate Offset:0 Limit: 300\n";
    private const int WaitTimeoutMs = 2000;
    private const int WaitStepMs = 10;
    private static readonly TimeSpan ShortSearchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);
    private readonly CiscoRoomOsPhonebookParser _parser;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<PhonebookUpdated> _phonebookUpdated = new();
    private readonly List<string> _folderReply = new()
    {
        "*r PhonebookSearchResult (status=OK):",
        "*r PhonebookSearchResult ResultInfo Offset: 0",
        "*r PhonebookSearchResult ResultInfo Limit: 300",
        "*r PhonebookSearchResult ResultInfo TotalRows: 3",
        "*r PhonebookSearchResult Folder 1 LocalId: \"c_61\"",
        "*r PhonebookSearchResult Folder 1 FolderId: \"c_61\"",
        "*r PhonebookSearchResult Folder 1 Name: \"AV Coders\"",
        "*r PhonebookSearchResult Folder 2 LocalId: \"c_35\"",
        "*r PhonebookSearchResult Folder 2 FolderId: \"c_35\"",
        "*r PhonebookSearchResult Folder 2 Name: \"A new folder with spaces\"",
        "*r PhonebookSearchResult Folder 3 LocalId: \"c_63\"",
        "*r PhonebookSearchResult Folder 3 FolderId: \"c_63\"",
        "*r PhonebookSearchResult Folder 3 Name: \"More folders\"",
        "** end"
    };
    private readonly List<string> _contactReply = new()
    {
        "*r PhonebookSearchResult (status=OK):",
        "*r PhonebookSearchResult ResultInfo Offset: 0",
        "*r PhonebookSearchResult ResultInfo Limit: 300",
        "*r PhonebookSearchResult ResultInfo TotalRows: 2",
        "*r PhonebookSearchResult Contact 1 Name: \"Contact One\"",
        "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
        "*r PhonebookSearchResult Contact 2 Name: \"Contact Two\"",
        "*r PhonebookSearchResult Contact 2 ContactId: \"e_2\"",
        "** end"
    };

    public CiscoRoomOsPhonebookParserTest()
    {
        _parser = new CiscoRoomOsPhonebookParser(_mockClient.Object);
        _parser.PhonebookUpdated = _phonebookUpdated.Object;
    }

    private void Receive(List<string> lines, string lineEnding = "\n") =>
        lines.ForEach(line => _mockClient.Object.ResponseHandlers!.Invoke(line + lineEnding));

    private static CiscoRoomOsPhonebookParser CreateLoggedParser(ScopeCapturingLoggerFactory logs, Mock<TcpClient> client)
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return new CiscoRoomOsPhonebookParser(client.Object);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private static int Sends(Mock<TcpClient> client, string command) =>
        client.Invocations.Count(invocation => invocation.Method.Name == nameof(TcpClient.Send) && Equals(invocation.Arguments[0], command));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(WaitTimeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(WaitStepMs);
        Assert.True(condition());
    }

    [Fact]
    public void Parser_RequestsThePhonebook_OnANewCodecSession()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");

        _mockClient.Verify(x => x.Send("xCommand Phonebook Search PhonebookType: Corporate Offset:0 Limit: 300\n"));
    }

    [Fact]
    public void PhonebookFolders_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):", 
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 3",
            "*r PhonebookSearchResult Folder 1 LocalId: \"c_61\"",
            "*r PhonebookSearchResult Folder 1 FolderId: \"c_61\"",
            "*r PhonebookSearchResult Folder 1 Name: \"AV Coders\"",
            "*r PhonebookSearchResult Folder 2 LocalId: \"c_35\"",
            "*r PhonebookSearchResult Folder 2 FolderId: \"c_35\"",
            "*r PhonebookSearchResult Folder 2 Name: \"A new folder with spaces\"",
            "*r PhonebookSearchResult Folder 3 LocalId: \"c_63\"",
            "*r PhonebookSearchResult Folder 3 FolderId: \"c_63\"",
            "*r PhonebookSearchResult Folder 3 Name: \"More folders\"",
            "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        CiscoRoomOsPhonebookFolder firstFolder = (CiscoRoomOsPhonebookFolder)_parser.PhoneBook.Items[0];
        
        Assert.Equal(3, _parser.PhoneBook.Items.Count);
        Assert.Equal("AV Coders", firstFolder.Name);
        Assert.Equal("c_61", firstFolder.LocalId);
        Assert.Equal("c_61", firstFolder.FolderId);
        Assert.Equal("A new folder with spaces", _parser.PhoneBook.Items[1].Name);
        Assert.Equal("More folders", _parser.PhoneBook.Items[2].Name);
    }

    [Fact]
    public void PhonebookContacts_AreParsedCorrectly()
    {
        new List<string>
        {
        "*r PhonebookSearchResult (status=OK):", 
        "*r PhonebookSearchResult ResultInfo Offset: 0",
        "*r PhonebookSearchResult ResultInfo Limit: 300",
        "*r PhonebookSearchResult ResultInfo TotalRows: 3",
        "*r PhonebookSearchResult Contact 1 Name: \"Jeoffery Sparnston\"",
        "*r PhonebookSearchResult Contact 1 ContactId: \"e_55246\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"SIP:123@234.com\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 CallRate: 768",
        "*r PhonebookSearchResult Contact 2 Name: \"Storbven La Trattore\"",
        "*r PhonebookSearchResult Contact 2 ContactId: \"e_55247\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 Number: \"SIP:930293@thebestsipever.au\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 CallRate: 768",
        "*r PhonebookSearchResult Contact 3 Name: \"Foo McBar\"",
        "*r PhonebookSearchResult Contact 3 ContactId: \"e_55249\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 Number: \"SIP:foomcbar@mcbarindustries.co.uk\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 CallRate: 768",
        "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));
        
        CiscoRoomOsPhonebookContact firstContact = (CiscoRoomOsPhonebookContact)_parser.PhoneBook.Items[0];
        
        Assert.Equal(3, _parser.PhoneBook.Items.Count);
        Assert.Equal("Jeoffery Sparnston", firstContact.Name);
        Assert.Equal("e_55246", firstContact.ContactId);
        Assert.Equal("SIP:123@234.com", firstContact.ContactMethods[0].Number);
        Assert.Equal("Storbven La Trattore", _parser.PhoneBook.Items[1].Name);
        Assert.Equal("Foo McBar", _parser.PhoneBook.Items[2].Name);
    }

    [Fact]
    public void PhonebookFolders_OutOfOrder_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):",
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Folder 2 Name: \"Second Folder\"",
            "*r PhonebookSearchResult Folder 1 Name: \"First Folder\"",
            "*r PhonebookSearchResult Folder 2 LocalId: \"c_2\"",
            "*r PhonebookSearchResult Folder 1 LocalId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 FolderId: \"c_1\"",
            "*r PhonebookSearchResult Folder 2 FolderId: \"c_2\"",
            "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Equal(2, _parser.PhoneBook.Items.Count);
        
        var firstFolder = (CiscoRoomOsPhonebookFolder?)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "First Folder");
        var secondFolder = (CiscoRoomOsPhonebookFolder?)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "Second Folder");
        
        Assert.NotNull(firstFolder);
        Assert.Equal("c_1", firstFolder.LocalId);
        Assert.NotNull(secondFolder);
        Assert.Equal("c_2", secondFolder.LocalId);
    }

    [Fact]
    public void PhonebookContacts_Interleaved_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):",
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Contact 1 Name: \"Contact One\"",
            "*r PhonebookSearchResult Contact 2 Name: \"Contact Two\"",
            "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
            "*r PhonebookSearchResult Contact 2 ContactId: \"e_2\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 ContactMethodId: \"1\"",
            "*r PhonebookSearchResult Contact 2 ContactMethod 1 ContactMethodId: \"1\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"111\"",
            "*r PhonebookSearchResult Contact 2 ContactMethod 1 Number: \"222\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Protocol: SIP",
            "*r PhonebookSearchResult Contact 2 ContactMethod 1 Protocol: SIP",
            "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Equal(2, _parser.PhoneBook.Items.Count);

        var c1 = (CiscoRoomOsPhonebookContact?)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "Contact One");
        var c2 = (CiscoRoomOsPhonebookContact?)_parser.PhoneBook.Items.FirstOrDefault(i => i.Name == "Contact Two");

        Assert.NotNull(c1);
        Assert.Equal("e_1", c1.ContactId);
        Assert.Equal("111", c1.ContactMethods[0].Number);

        Assert.NotNull(c2);
        Assert.Equal("e_2", c2.ContactId);
        Assert.Equal("222", c2.ContactMethods[0].Number);
    }

    [Fact]
    public void PhonebookContacts_WithMultipleMethods_AreParsedCorrectly()
    {
        new List<string>
        {
            "*r PhonebookSearchResult (status=OK):",
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 1",
            "*r PhonebookSearchResult Contact 1 Name: \"Multi Method\"",
            "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 2 ContactMethodId: \"2\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 2 Number: \"222\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 2 Protocol: SIP",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 ContactMethodId: \"1\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"111\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Protocol: H323",
            "** end"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Single(_parser.PhoneBook.Items);
        var contact = (CiscoRoomOsPhonebookContact)_parser.PhoneBook.Items[0];
        Assert.Equal(2, contact.ContactMethods.Count);
        
        var m1 = contact.ContactMethods.FirstOrDefault(m => m.Number == "111");
        var m2 = contact.ContactMethods.FirstOrDefault(m => m.Number == "222");
        
        Assert.NotNull(m1);
        Assert.Equal("H323", ((CiscoRoomOsPhonebookContactMethod)m1).Protocol);
        Assert.NotNull(m2);
        Assert.Equal("SIP", ((CiscoRoomOsPhonebookContactMethod)m2).Protocol);
    }

    [Fact]
    public void PhonebookFolders_InOneChunk_AreParsedCorrectly()
    {
        _mockClient.Object.ResponseHandlers!.Invoke(string.Concat(_folderReply.Select(line => line + "\r\n")));

        Assert.Equal(3, _parser.PhoneBook.Items.Count);
        Assert.Equal("AV Coders", _parser.PhoneBook.Items[0].Name);
        Assert.Equal("c_61", ((CiscoRoomOsPhonebookFolder)_parser.PhoneBook.Items[0]).FolderId);
        Assert.Equal("More folders", _parser.PhoneBook.Items[2].Name);
    }

    [Fact]
    public void StrayEnds_BeforeASearch_DoNotRaisePhonebookUpdated()
    {
        Receive(new List<string> { "*s Call 102 Status: Connected", "** end", "** end" });

        _phonebookUpdated.VerifyNoOtherCalls();
    }

    [Fact]
    public void StrayEnds_AfterASearch_DoNotRaisePhonebookUpdatedAgain()
    {
        Receive(_contactReply);

        Receive(new List<string> { "*s Call 102 Status: Connected", "** end", "** end" });

        _phonebookUpdated.Verify(x => x.Invoke(It.IsAny<PhonebookFolder>()), Times.Once);
        Assert.Equal(2, _parser.PhoneBook.Items.Count);
    }

    [Fact]
    public void AnEmptyFolder_DoesNotSkipTheNextFolder()
    {
        Receive(new List<string>
        {
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Folder 1 LocalId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 FolderId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 Name: \"Empty\"",
            "*r PhonebookSearchResult Folder 2 LocalId: \"c_2\"",
            "*r PhonebookSearchResult Folder 2 FolderId: \"c_2\"",
            "*r PhonebookSearchResult Folder 2 Name: \"Full\"",
            "** end",
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 0",
            "** end"
        });

        _mockClient.Verify(x => x.Send("xCommand Phonebook Search PhonebookType: Corporate Offset:0 FolderId: c_2 Limit: 300\n"), Times.Once);
        _phonebookUpdated.VerifyNoOtherCalls();

        Receive(new List<string>
        {
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 1",
            "*r PhonebookSearchResult Contact 1 Name: \"Someone\"",
            "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
            "** end"
        });

        Assert.Empty(((CiscoRoomOsPhonebookFolder)_parser.PhoneBook.Items[0]).Items);
        Assert.Single(((CiscoRoomOsPhonebookFolder)_parser.PhoneBook.Items[1]).Items);
        _phonebookUpdated.Verify(x => x.Invoke(It.IsAny<PhonebookFolder>()), Times.Once);
    }

    [Fact]
    public void RequestPhonebook_DuringAReply_WaitsForItsEndThenLoadsWithoutTheOldRows()
    {
        _parser.RequestPhonebook();
        Receive(_folderReply.Take(7).ToList());

        _parser.RequestPhonebook();

        _mockClient.Verify(x => x.Send(RootSearch), Times.Once);

        Receive(_folderReply.Skip(7).ToList());

        _mockClient.Verify(x => x.Send(RootSearch), Times.Exactly(2));
        Assert.Empty(_parser.PhoneBook.Items);

        Receive(_contactReply);

        Assert.Equal(new[] { "Contact One", "Contact Two" }, _parser.PhoneBook.Items.Select(item => item.Name));
        _mockClient.Verify(x => x.Send(It.Is<string>(command => command.Contains("FolderId"))), Times.Never);
        _phonebookUpdated.Verify(x => x.Invoke(_parser.PhoneBook), Times.Once);
    }

    [Fact]
    public void RequestsWhileOneIsHeld_SendOneSearch_UnderTheLatestTriggerId()
    {
        var sends = new List<(string Wire, string? TriggerId)>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(wire => sends.Add((wire, Trigger.Id)));
        Trigger.Start();
        _parser.RequestPhonebook();
        Trigger.Start();
        _parser.RequestPhonebook();
        var latest = Trigger.Start();
        _parser.RequestPhonebook();

        Trigger.Start();
        Receive(_contactReply);

        Assert.Equal(new[] { RootSearch, RootSearch }, sends.Select(send => send.Wire));
        Assert.Equal(latest, sends[1].TriggerId);
    }

    [Fact]
    public async Task HeldRequest_IsSentAfterTheSearchTimeout()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = TestFactory.CreateTcpClient();
        var parser = CreateLoggedParser(logs, client);
        parser.SearchTimeout = ShortSearchTimeout;
        parser.RequestPhonebook();
        parser.RequestPhonebook();

        await WaitUntil(() => Sends(client, RootSearch) == 2);

        Assert.Contains(logs.Logs, log => log.Message.StartsWith($"No complete reply to {RootSearch.TrimEnd()} within "));

        _contactReply.ForEach(line => client.Object.ResponseHandlers!.Invoke(line + "\n"));

        Assert.Equal(new[] { "Contact One", "Contact Two" }, parser.PhoneBook.Items.Select(item => item.Name));
    }

    [Fact]
    public void AnErrorReply_EndsTheSearch_AndReleasesTheHeldRequest()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = TestFactory.CreateTcpClient();
        var parser = CreateLoggedParser(logs, client);
        var phonebookUpdated = new Mock<PhonebookUpdated>();
        parser.PhonebookUpdated = phonebookUpdated.Object;
        parser.RequestPhonebook();
        parser.RequestPhonebook();

        new List<string> { ErrorHeader, ErrorReason, "** end" }
            .ForEach(line => client.Object.ResponseHandlers!.Invoke(line + "\n"));

        Assert.Equal(2, Sends(client, RootSearch));
        var messages = logs.Logs.Select(log => log.Message).ToList();
        Assert.Contains($"Phonebook search failed: {ErrorHeader}", messages);
        Assert.Contains($"Phonebook search failed: {ErrorReason}", messages);
        Assert.Equal(CommunicationState.Error, parser.CommunicationState);
        Assert.Empty(parser.PhoneBook.Items);
        phonebookUpdated.VerifyNoOtherCalls();
    }

    [Fact]
    public void ADroppedConnection_EndsTheOutstandingSearch()
    {
        _parser.RequestPhonebook();
        _parser.RequestPhonebook();

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Disconnected);
        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");

        _mockClient.Verify(x => x.Send(RootSearch), Times.Exactly(2));

        Receive(_contactReply);

        _mockClient.Verify(x => x.Send(RootSearch), Times.Exactly(2));
        Assert.Equal(new[] { "Contact One", "Contact Two" }, _parser.PhoneBook.Items.Select(item => item.Name));
    }

    [Fact]
    public void APhonebookUpdatedSubscriber_CanRequestThePhonebookFromAnotherThread()
    {
        var joined = false;
        _parser.PhonebookUpdated = _ =>
        {
            var request = new Thread(_parser.RequestPhonebook);
            request.Start();
            joined = request.Join(JoinTimeout);
        };
        _parser.RequestPhonebook();

        Receive(_contactReply);

        Assert.True(joined);
        _mockClient.Verify(x => x.Send(RootSearch), Times.Exactly(2));
    }

    [Fact]
    public void NextPage_AtTheRoot_HasNoFolderId()
    {
        Receive(new List<string>
        {
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 2",
            "*r PhonebookSearchResult ResultInfo TotalRows: 3",
            "*r PhonebookSearchResult Contact 1 Name: \"Contact One\"",
            "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
            "*r PhonebookSearchResult Contact 2 Name: \"Contact Two\"",
            "*r PhonebookSearchResult Contact 2 ContactId: \"e_2\"",
            "** end"
        });

        _mockClient.Verify(x => x.Send("xCommand Phonebook Search PhonebookType: Corporate Offset:2 Limit: 300\n"), Times.Once);
    }

    [Fact]
    public void NextPage_WithoutALimitLine_UsesTheSearchLimit()
    {
        var reply = new List<string>
        {
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo TotalRows: 301"
        };
        for (var row = 1; row <= 300; row++)
        {
            reply.Add($"*r PhonebookSearchResult Contact {row} Name: \"Contact {row}\"");
            reply.Add($"*r PhonebookSearchResult Contact {row} ContactId: \"e_{row}\"");
        }
        reply.Add("** end");

        Receive(reply);

        _mockClient.Verify(x => x.Send("xCommand Phonebook Search PhonebookType: Corporate Offset:300 Limit: 300\n"), Times.Once);
    }

    [Fact]
    public void PhonebookContacts_AreMarkedLoaded()
    {
        Receive(_contactReply);

        var loadedRows = (HashSet<int>)typeof(CiscoRoomOsPhonebookParser)
            .GetField("_loadedRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_parser)!;
        Assert.Equal(new[] { 1, 2 }, loadedRows.Order());
    }

    [Fact]
    public void Folders_WithTheSameName_AreKeptApart()
    {
        Receive(new List<string>
        {
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Folder 1 LocalId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 FolderId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 Name: \"Sales\"",
            "*r PhonebookSearchResult Folder 2 LocalId: \"c_2\"",
            "*r PhonebookSearchResult Folder 2 FolderId: \"c_2\"",
            "*r PhonebookSearchResult Folder 2 Name: \"Sales\"",
            "** end"
        });

        Assert.Equal(new[] { "c_1", "c_2" },
            _parser.PhoneBook.Items.Cast<CiscoRoomOsPhonebookFolder>().Select(folder => folder.FolderId));
    }

    [Fact]
    public void Contacts_WithTheSameName_AreKeptApart()
    {
        Receive(new List<string>
        {
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Contact 1 Name: \"Alex\"",
            "*r PhonebookSearchResult Contact 1 ContactId: \"e_1\"",
            "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"111\"",
            "*r PhonebookSearchResult Contact 2 Name: \"Alex\"",
            "*r PhonebookSearchResult Contact 2 ContactId: \"e_2\"",
            "*r PhonebookSearchResult Contact 2 ContactMethod 1 Number: \"222\"",
            "** end"
        });

        var contacts = _parser.PhoneBook.Items.Cast<CiscoRoomOsPhonebookContact>().ToList();
        Assert.Equal(new[] { "e_1", "e_2" }, contacts.Select(contact => contact.ContactId));
        Assert.Equal("111", contacts[0].ContactMethods.Single().Number);
        Assert.Equal("222", contacts[1].ContactMethods.Single().Number);
    }

    [Fact]
    public void AFolderAndAContact_WithTheSameName_AreBothKept()
    {
        Receive(new List<string>
        {
            ResultHeader,
            "*r PhonebookSearchResult ResultInfo Offset: 0",
            "*r PhonebookSearchResult ResultInfo Limit: 300",
            "*r PhonebookSearchResult ResultInfo TotalRows: 2",
            "*r PhonebookSearchResult Folder 1 LocalId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 FolderId: \"c_1\"",
            "*r PhonebookSearchResult Folder 1 Name: \"Reception\"",
            "*r PhonebookSearchResult Contact 2 Name: \"Reception\"",
            "*r PhonebookSearchResult Contact 2 ContactId: \"e_2\"",
            "** end"
        });

        Assert.Equal(2, _parser.PhoneBook.Items.Count);
        Assert.IsType<CiscoRoomOsPhonebookFolder>(_parser.PhoneBook.Items[0]);
        Assert.IsType<CiscoRoomOsPhonebookContact>(_parser.PhoneBook.Items[1]);
    }

    [Theory]
    [InlineData("*r PhonebookSearchResult ResultInfo")]
    [InlineData("*r PhonebookSearchResult ResultInfo Offset:")]
    [InlineData("*r PhonebookSearchResult Folder 1")]
    [InlineData("*r PhonebookSearchResult Contact 1")]
    [InlineData("*r PhonebookSearchResult Contact 1 ContactMethod")]
    public void ShortLines_AreIgnored(string line)
    {
        var exception = Record.Exception(() => Receive(new List<string> { ResultHeader, line, "** end" }, ""));

        Assert.Null(exception);
        Assert.Empty(_parser.PhoneBook.Items);
    }
}